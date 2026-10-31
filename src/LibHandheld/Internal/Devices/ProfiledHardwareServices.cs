using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledCycleIdentity(string Firmware);

internal sealed partial class ProfiledPowerService(
    Func<ICpuPowerTransport> open,
    DeviceRecoveryJournal journal,
    ProfiledDeviceModel? model = null,
    string? kxPath = null) : DeviceService<ProfiledCycleIdentity>("power")
{
    // Intel models choose PawnIO or KX; AMD keeps its single SMU path.
    private readonly IntelPowerPaths? _paths = model?.Cpu == CpuPowerKind.Intel
        ? new IntelPowerPaths(journal, "power", hasFirmware: false)
        : null;
    private bool _captureAttempted;
    private ICpuPowerTransport? _transport;
    private bool _unknownOriginalChanged;
    private readonly Dictionary<CpuPowerLimit, (double Watts, DateTimeOffset At)> _writtenLimits = [];
    internal CpuPowerLimits? Observed { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    public override bool Suspendable => true;

    internal override ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        _transport = null;
        _paths?.Dispose();
        return ValueTask.CompletedTask;
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        _paths?.Refresh();
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
        if (_paths is not null)
        {
            _paths.Acquire(kxPath, pawnIo: true);
            await _paths.ResumeAsync(context.Identity.Firmware, token).ConfigureAwait(false);
        }

        return Set(DeviceServiceState.Owned);
    }

    internal async ValueTask WriteAsync(CpuPowerLimits limits, string firmware, CancellationToken token,
        Action dispatched)
    {
        await CapturePowerOriginalAsync(firmware, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        dispatched();
        _transport!.WriteLimits(limits);
        if (_paths is not null)
        {
            await _paths.WriteMchbarAsync((int)Math.Round(limits.SustainedWatts), (int)Math.Round(limits.BoostWatts),
                firmware, token).ConfigureAwait(false);
        }

        var at = DateTimeOffset.UtcNow;
        _writtenLimits[CpuPowerLimit.Sustained] = (limits.SustainedWatts, at);
        _writtenLimits[CpuPowerLimit.Slow] = (limits.SlowWatts, at);
        _writtenLimits[CpuPowerLimit.Boost] = (limits.BoostWatts, at);
        Observed = limits;
        ObservedAt = at;
    }

    private async ValueTask WriteIndependentAsync(CpuPowerLimit limit, int watts, string firmware,
        CancellationToken token, Action dispatched)
    {
        await CapturePowerOriginalAsync(firmware, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        dispatched();
        try
        {
            _transport!.WriteLimit(limit, watts);
            _writtenLimits[limit] = (watts, DateTimeOffset.UtcNow);
        }
        catch
        {
            _writtenLimits.Remove(limit);
            throw;
        }

        ObservedAt = DateTimeOffset.UtcNow;
        Observed = _writtenLimits.TryGetValue(CpuPowerLimit.Sustained, out var sustained)
                   && _writtenLimits.TryGetValue(CpuPowerLimit.Slow, out var slow)
                   && _writtenLimits.TryGetValue(CpuPowerLimit.Boost, out var fast)
            ? new CpuPowerLimits(sustained.Watts, slow.Watts, fast.Watts)
            : null;
    }

    private async ValueTask CapturePowerOriginalAsync(string firmware, CancellationToken token)
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

        if (journal.EntryFor<ProfiledRecoveryState>(ServiceId) is null)
        {
            _unknownOriginalChanged = true;
        }

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

            if (_paths is not null)
            {
                await _paths.RestoreAsync(context.Identity.Firmware, token).ConfigureAwait(false);
            }

            return _unknownOriginalChanged || journal.EntryFor<ProfiledRecoveryState>(ServiceId) is not null
                                           || _paths?.HasUnrestored == true
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
            _writtenLimits.Clear();
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
    internal int? MeasuredDuty { get; private set; }
    internal string? Mode { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    internal DateTimeOffset? RpmObservedAt { get; private set; }
    internal DateTimeOffset? MeasuredDutyObservedAt { get; private set; }
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
            if (profile.ObserveDuty)
            {
                var raw = _ec.ReadByte(profile.DutyAddresses[0]);
                MeasuredDuty = Math.Clamp((int)Math.Round(raw * 100d / profile.MaximumDuty), 0, 100);
                MeasuredDutyObservedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MeasuredDuty = null;
            MeasuredDutyObservedAt = null;
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
                             .Concat(profile.AutomaticDutyAddress is { } automatic
                                 ? [automatic] : Array.Empty<ushort>())
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
                var automaticAddresses = profile.AutomaticDutyAddress is { } automatic
                    ? new[] { automatic } : profile.DutyAddresses;
                foreach (var address in automaticAddresses)
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
            MeasuredDuty = null;
            MeasuredDutyObservedAt = null;
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
