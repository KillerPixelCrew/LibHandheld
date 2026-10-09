using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledCycleIdentity(string Firmware);

internal sealed partial class ProfiledPowerService(
    Func<ICpuPowerTransport> open,
    DeviceRecoveryJournal journal,
    ProfiledDeviceModel? model = null) : DeviceService<ProfiledCycleIdentity>("power")
{
    private ICpuPowerTransport? _transport;
    private bool _unknownOriginalChanged;
    internal CpuPowerLimits? Observed { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    public override bool Suspendable => true;

    internal override ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        _transport = null;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _transport = open();
        await journal.ReconcileAsync<ProfiledRecoveryState>(ServiceId, context.Identity.Firmware,
            async (original, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var verified = RestorePower(Original(original));
                await journal.SetStatusAsync(ServiceId, verified
                    ? DeviceRecoveryStatus.RestoredVerified
                    : DeviceRecoveryStatus.RestoredUnverified, ct).ConfigureAwait(false);
            }, token).ConfigureAwait(false);

        Refresh();
        return Set(DeviceServiceState.Owned);
    }

    internal void Refresh()
    {
        if (_transport is null)
        {
            return;
        }

        try
        {
            Observed = _transport.ReadLimits();
            ObservedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            /* Write support does not depend on read support. */
        }
    }

    internal async ValueTask WriteAsync(CpuPowerLimits limits, string firmware, CancellationToken token,
        Action dispatched)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("CPU power transport is not owned.");
        }


        await journal.BeginAsync(ServiceId, firmware, () =>
        {
            var original = _transport.ReadLimits();
            return new ProfiledRecoveryState
                { Sustained = original.SustainedWatts, Slow = original.SlowWatts, Boost = original.BoostWatts };
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        if (journal.EntryFor<ProfiledRecoveryState>(ServiceId) is null)
        {
            _unknownOriginalChanged = true;
        }

        dispatched();
        _transport.WriteLimits(limits);
        Observed = limits;
        ObservedAt = DateTimeOffset.UtcNow;
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        var transport = _transport;
        if (transport is null)
        {
            return _unknownOriginalChanged
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original CPU power limits were not readable."))
                : Set(DeviceServiceState.Idle);
        }

        try
        {
            if (journal.PendingOriginalFor<ProfiledRecoveryState>(ServiceId) is { } original)
            {
                var verified = RestorePower(Original(original));
                await journal.SetStatusAsync(ServiceId, verified
                    ? DeviceRecoveryStatus.RestoredVerified
                    : DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None).ConfigureAwait(false);
            }

            return _unknownOriginalChanged || journal.EntryFor<ProfiledRecoveryState>(ServiceId) is not null
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing(
                        "CPU limits were written without readable originals; their original values cannot be restored."))
                : Set(DeviceServiceState.Idle);
        }
        catch
        {
            await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            transport.Dispose();
            _transport = null;
            Observed = null;
            ObservedAt = null;
        }
    }

    private static CpuPowerLimits Original(ProfiledRecoveryState state)
    {
        return new CpuPowerLimits(state.Sustained!.Value, state.Slow!.Value, state.Boost!.Value);
    }

    private bool RestorePower(CpuPowerLimits original)
    {
        _transport!.WriteLimits(original);
        try
        {
            var actual = _transport.ReadLimits();
            return Math.Abs(actual.SustainedWatts - original.SustainedWatts) <= 0.125
                   && Math.Abs(actual.SlowWatts - original.SlowWatts) <= 0.125
                   && Math.Abs(actual.BoostWatts - original.BoostWatts) <= 0.125;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}

internal sealed partial class ProfiledFanService(
    EcFanProfile profile,
    Func<IEmbeddedController> open,
    DeviceRecoveryJournal journal) : DeviceService<ProfiledCycleIdentity>("fan")
{
    private IEmbeddedController? _ec;
    internal int? Duty { get; private set; }
    internal int? Rpm { get; private set; }
    internal string? Mode { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    public override bool Suspendable => true;

    internal override ValueTask DisposeAsync()
    {
        _ec?.Dispose();
        _ec = null;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ec = open();
        foreach (var recoveryId in new[] { ServiceId, "fan-unlock" })
        {
            await journal.ReconcileAsync<ProfiledRecoveryState>(recoveryId, context.Identity.Firmware,
                async (original, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var verified = Restore(original);
                    await journal.SetStatusAsync(recoveryId, verified
                        ? DeviceRecoveryStatus.RestoredVerified
                        : DeviceRecoveryStatus.RestoredUnverified, ct).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
        }

        if (profile.UnlockAddress is { } unlock)
        {
            if (profile.UnlockChipAddress is { } chip && _ec.ReadByte(chip) != profile.UnlockChipValue)
            {
                return Set(DeviceServiceState.Passive, Missing("The GPD SuperIO chip signature did not match."));
            }

            var original = _ec.ReadByte(unlock);
            await journal.BeginAsync("fan-unlock", context.Identity.Firmware,
                new ProfiledRecoveryState { Addresses = [unlock], Values = [original] }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ec.WriteByte(unlock, (byte)(original | profile.UnlockMask));
        }

        Refresh();
        return Set(DeviceServiceState.Owned);
    }

    internal void Refresh()
    {
        if (_ec is null)
        {
            return;
        }

        try
        {
            if (profile.ReadDuty)
            {
                var raw = _ec.ReadByte(profile.DutyAddresses[0]);
                if (raw <= profile.MaximumDuty)
                {
                    Duty = (int)Math.Round(raw * 100d / profile.MaximumDuty);
                    ObservedAt = DateTimeOffset.UtcNow;
                }
            }

            if (profile.ManualAddress is { } manual)
            {
                var raw = _ec.ReadByte(manual);
                Mode = raw == profile.AutomaticValue ? "automatic"
                    : raw == profile.ManualValue ? Duty == 100 ? "full-speed" : "custom" : null;
            }

            if (profile.RpmHighAddress is { } high && profile.RpmLowAddress is { } low)
            {
                Rpm = _ec.Transaction(() => (_ec.ReadByte(high) << 8) | _ec.ReadByte(low));
                ObservedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    internal async ValueTask WriteAsync(string mode, int? percent, string firmware, CancellationToken token,
        Action dispatched)
    {
        if (_ec is null)
        {
            throw new InvalidOperationException("EC fan transport is not owned.");
        }

        await journal.BeginAsync(ServiceId, firmware, () =>
        {
            List<ushort> addresses = [];
            List<byte> values = [];
            foreach (var address in profile.DutyAddresses
                         .Concat(profile.ManualAddress is { } manual ? [manual] : Array.Empty<ushort>()).Distinct())
            {
                if (!profile.ReadDuty && profile.DutyAddresses.Contains(address))
                {
                    continue;
                }

                try
                {
                    values.Add(_ec.ReadByte(address));
                    addresses.Add(address);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    DeviceTrace.Warn("recovery", $"Cannot capture fan register {address:X4}: {exception.Message}.");
                }
            }

            return addresses.Count == 0
                ? null
                : new ProfiledRecoveryState
                    { Addresses = addresses.ToArray(), Values = values.ToArray(), HasExactOriginal = true };
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        dispatched();
        _ec.Transaction(() =>
        {
            if (profile.ManualAddress is { } manual)
            {
                _ec.WriteByte(manual, mode == "automatic" ? profile.AutomaticValue : profile.ManualValue);
            }

            if (mode == "automatic" && profile.ManualAddress is null)
            {
                foreach (var address in profile.DutyAddresses)
                {
                    _ec.WriteByte(address, 0);
                }
            }
            else if (mode == "full-speed" || percent is not null)
            {
                foreach (var address in profile.DutyAddresses)
                {
                    _ec.WriteByte(address, profile.EncodeDuty(mode == "full-speed" ? 100 : percent!.Value));
                }
            }

            return true;
        });
        Mode = mode;
        ObservedAt = DateTimeOffset.UtcNow;
        if (percent is not null || mode == "full-speed")
        {
            Duty = mode == "full-speed" ? 100 : percent;
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        if (_ec is null)
        {
            return Set(DeviceServiceState.Idle);
        }

        try
        {
            foreach (var recoveryId in new[] { ServiceId, "fan-unlock" })
            {
                if (journal.PendingOriginalFor<ProfiledRecoveryState>(recoveryId) is { } original)
                {
                    var verified = Restore(original);
                    await journal.SetStatusAsync(recoveryId, verified
                        ? DeviceRecoveryStatus.RestoredVerified
                        : DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None).ConfigureAwait(false);
                }
            }

            return journal.EntryFor<ProfiledRecoveryState>(ServiceId) is not null ||
                   journal.EntryFor<ProfiledRecoveryState>("fan-unlock") is not null
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The restored fan state could not be independently confirmed."))
                : Set(DeviceServiceState.Idle);
        }
        catch
        {
            if (journal.EntryFor<ProfiledRecoveryState>(ServiceId) is not null)
            {
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (journal.EntryFor<ProfiledRecoveryState>("fan-unlock") is not null)
            {
                await journal.SetStatusAsync("fan-unlock", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _ec.Dispose();
            _ec = null;
            Duty = null;
            Rpm = null;
            Mode = null;
            ObservedAt = null;
        }
    }

    private bool Restore(ProfiledRecoveryState state)
    {
        // Restore duties before the control/unlock flags so firmware owns the final state.
        _ec!.Transaction(() =>
        {
            for (var i = 0; i < state.Addresses.Length; i++)
            {
                _ec.WriteByte(state.Addresses[i], state.Values[i]);
            }

            return true;
        });
        if (!state.HasExactOriginal)
        {
            return false;
        }

        try
        {
            return _ec.Transaction(() => state.Addresses.Select((address, index) =>
                _ec.ReadByte(address) == state.Values[index]).All(value => value));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
