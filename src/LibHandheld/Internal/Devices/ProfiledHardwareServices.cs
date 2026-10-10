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
    private bool _captureAttempted;
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
                RestorePower(Original(original));
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredUnverified, ct)
                    .ConfigureAwait(false);
            }, token).ConfigureAwait(false);

        _captureAttempted = false;
        return Set(DeviceServiceState.Owned);
    }

    internal async ValueTask WriteAsync(CpuPowerLimits limits, string firmware, CancellationToken token,
        Action dispatched)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("CPU power transport is not owned.");
        }


        if (!_captureAttempted)
        {
            _captureAttempted = true;
            // Capture only the original needed for restoration; it never supplies control state.
            await journal.BeginAsync(ServiceId, firmware, () =>
            {
                var original = _transport.ReadLimits();
                return new ProfiledRecoveryState
                    { Sustained = original.SustainedWatts, Slow = original.SlowWatts, Boost = original.BoostWatts };
            }, token).ConfigureAwait(false);
        }

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
                RestorePower(Original(original));
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredUnverified,
                    CancellationToken.None).ConfigureAwait(false);
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

    private void RestorePower(CpuPowerLimits original)
    {
        _transport!.WriteLimits(original);
    }
}

internal sealed partial class ProfiledFanService(
    EcFanProfile profile,
    Func<IEmbeddedController> open,
    DeviceRecoveryJournal journal) : DeviceService<ProfiledCycleIdentity>("fan")
{
    private bool _captureAttempted;
    private IEmbeddedController? _ec;
    private bool _originalIncomplete;
    private bool _unknownOriginalChanged;
    internal int? Duty { get; private set; }
    internal int? Rpm { get; private set; }
    internal string? Mode { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    internal DateTimeOffset? RpmObservedAt { get; private set; }
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
                    Restore(original);
                    await journal.SetStatusAsync(recoveryId, DeviceRecoveryStatus.RestoredUnverified, ct)
                        .ConfigureAwait(false);
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

        _captureAttempted = false;
        _originalIncomplete = false;
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
            if (profile.RpmHighAddress is { } high && profile.RpmLowAddress is { } low)
            {
                Rpm = _ec.Transaction(() => (_ec.ReadByte(high) << 8) | _ec.ReadByte(low));
                RpmObservedAt = DateTimeOffset.UtcNow;
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

        if (!_captureAttempted)
        {
            _captureAttempted = true;
            var captured = await journal.BeginAsync(ServiceId, firmware, () =>
            {
                List<ushort> addresses = [];
                List<byte> values = [];
                foreach (var address in profile.DutyAddresses
                             .Concat(profile.ManualAddress is { } manual ? [manual] : Array.Empty<ushort>()).Distinct())
                {
                    if (!profile.ReadDuty && profile.DutyAddresses.Contains(address))
                    {
                        _originalIncomplete = true;
                        continue;
                    }

                    try
                    {
                        values.Add(_ec.ReadByte(address));
                        addresses.Add(address);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        _originalIncomplete = true;
                        DeviceTrace.Warn("recovery", $"Cannot capture fan register {address:X4}: {exception.Message}.");
                    }
                }

                return addresses.Count == 0
                    ? null
                    : new ProfiledRecoveryState
                    {
                        Addresses = addresses.ToArray(), Values = values.ToArray(),
                        HasExactOriginal = !_originalIncomplete
                    };
            }, token).ConfigureAwait(false);
            _originalIncomplete |= captured is null;
        }

        token.ThrowIfCancellationRequested();
        dispatched();
        _unknownOriginalChanged |= _originalIncomplete;
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
        else if (mode == "automatic")
        {
            Duty = null;
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
                    Restore(original);
                    await journal.SetStatusAsync(recoveryId, DeviceRecoveryStatus.RestoredUnverified,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            return _unknownOriginalChanged || journal.EntryFor<ProfiledRecoveryState>(ServiceId) is not null ||
                   journal.EntryFor<ProfiledRecoveryState>("fan-unlock") is not null
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original fan settings were not completely captured for restoration."))
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
            RpmObservedAt = null;
        }
    }

    private void Restore(ProfiledRecoveryState state)
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
    }
}
