using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledCycleIdentity(string Firmware);

internal sealed class ProfiledPowerService(
    Func<ICpuPowerTransport> open,
    ProfiledRecoveryJournal journal) : DeviceService<ProfiledCycleIdentity>("power")
{
    private ICpuPowerTransport? _transport;
    private bool _unknownOriginalChanged;
    internal CpuPowerLimits? Observed { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _transport = open();
        if (journal.EntryFor(ServiceId) is { } entry)
        {
            if (entry.FirmwareIdentity != context.Identity.Firmware)
            {
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredVerified, token)
                    .ConfigureAwait(false);
            }
            else if (entry.Status == DeviceRecoveryStatus.Pending)
            {
                try
                {
                    var verified = RestorePower(Original(entry.OriginalState));
                    await journal.SetStatusAsync(ServiceId, verified
                        ? DeviceRecoveryStatus.RestoredVerified
                        : DeviceRecoveryStatus.RestoredUnverified, token).ConfigureAwait(false);
                }
                catch
                {
                    await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
            }
        }

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

        if (journal.EntryFor(ServiceId) is { Status: not DeviceRecoveryStatus.Pending } existing &&
            existing.FirmwareIdentity == firmware)
        {
            await journal.BeginAsync(ServiceId, firmware, existing.OriginalState, token).ConfigureAwait(false);
        }

        if (journal.EntryFor(ServiceId)?.FirmwareIdentity != firmware)
        {
            CpuPowerLimits? original = null;
            try
            {
                original = _transport.ReadLimits();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }

            if (original is { } known)
            {
                await journal.BeginAsync(ServiceId, firmware, new ProfiledRecoveryState
                        { Sustained = known.SustainedWatts, Slow = known.SlowWatts, Boost = known.BoostWatts }, token)
                    .ConfigureAwait(false);
            }
        }

        token.ThrowIfCancellationRequested();
        if (journal.EntryFor(ServiceId) is null)
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
            if (journal.PendingOriginalFor(ServiceId) is { } original)
            {
                var verified = RestorePower(Original(original));
                await journal.SetStatusAsync(ServiceId, verified
                    ? DeviceRecoveryStatus.RestoredVerified
                    : DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None).ConfigureAwait(false);
            }

            return _unknownOriginalChanged || journal.EntryFor(ServiceId) is not null
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

internal sealed class ProfiledFanService(
    EcFanProfile profile,
    Func<IEmbeddedController> open,
    ProfiledRecoveryJournal journal) : DeviceService<ProfiledCycleIdentity>("fan")
{
    private IEmbeddedController? _ec;
    private bool _unlockNeedsAction;
    internal int? Duty { get; private set; }
    internal int? Rpm { get; private set; }
    internal string? Mode { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ec = open();
        foreach (var recoveryId in new[] { ServiceId, "fan-unlock" })
        {
            if (journal.EntryFor(recoveryId) is { } entry)
            {
                if (entry.FirmwareIdentity != context.Identity.Firmware)
                {
                    await journal.SetStatusAsync(recoveryId, DeviceRecoveryStatus.RestoredVerified, token)
                        .ConfigureAwait(false);
                }
                else if (entry.Status == DeviceRecoveryStatus.Pending)
                {
                    try
                    {
                        var verified = Restore(entry.OriginalState);
                        if (recoveryId == "fan-unlock" && !verified)
                        {
                            _unlockNeedsAction = true;
                        }

                        await journal.SetStatusAsync(recoveryId, verified
                            ? DeviceRecoveryStatus.RestoredVerified
                            : DeviceRecoveryStatus.RestoredUnverified, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        await journal
                            .SetStatusAsync(recoveryId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw;
                    }
                }
                else if (recoveryId == "fan-unlock")
                {
                    _unlockNeedsAction = true;
                }
            }
        }

        if (!_unlockNeedsAction && profile.UnlockAddress is { } unlock)
        {
            if (profile.UnlockChipAddress is { } chip && _ec.ReadByte(chip) != profile.UnlockChipValue)
            {
                return Set(DeviceServiceState.Passive, Missing("The GPD SuperIO chip signature did not match."));
            }

            var original = _ec.ReadByte(unlock);
            await journal.BeginAsync("fan-unlock", context.Identity.Firmware,
                new ProfiledRecoveryState { Addresses = [unlock], Values = [original] }, token).ConfigureAwait(false);
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
                Rpm = (_ec.ReadByte(high) << 8) | _ec.ReadByte(low);
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

        if (_unlockNeedsAction && profile.UnlockAddress is { } unlock)
        {
            var prior = journal.EntryFor("fan-unlock")!.OriginalState;
            await journal.BeginAsync("fan-unlock", firmware, prior, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            dispatched();
            _ec.WriteByte(unlock, (byte)(prior.Values[0] | profile.UnlockMask));
            _unlockNeedsAction = false;
        }

        var current = journal.EntryFor(ServiceId);
        if (current is { Status: not DeviceRecoveryStatus.Pending } && current.FirmwareIdentity == firmware)
        {
            await journal.BeginAsync(ServiceId, firmware, current.OriginalState, token).ConfigureAwait(false);
        }

        var addresses = profile.DutyAddresses
            .Concat(profile.ManualAddress is { } manual ? [manual] : Array.Empty<ushort>()).Distinct().ToArray();
        if (current is null)
        {
            var original = new byte[addresses.Length];
            var exact = true;
            for (var index = 0; index < addresses.Length; index++)
            {
                // A missing readback never blocks the proved write path. When an original cannot
                // be captured, restoring the documented firmware/automatic baseline is explicit.
                if (!profile.ReadDuty && profile.DutyAddresses.Contains(addresses[index]))
                {
                    original[index] = 0;
                    exact = false;
                }
                else
                {
                    try
                    {
                        original[index] = _ec.ReadByte(addresses[index]);
                        if (profile.DutyAddresses.Contains(addresses[index]) && original[index] > profile.MaximumDuty)
                        {
                            original[index] = 0;
                            exact = false;
                        }

                        if (profile.ManualAddress == addresses[index] && original[index] != profile.AutomaticValue &&
                            original[index] != profile.ManualValue)
                        {
                            original[index] = profile.AutomaticValue;
                            exact = false;
                        }
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        original[index] = profile.ManualAddress == addresses[index] ? profile.AutomaticValue : (byte)0;
                        exact = false;
                    }
                }
            }

            await journal.BeginAsync(ServiceId, firmware, new ProfiledRecoveryState
                { Addresses = addresses, Values = original, HasExactOriginal = exact }, token).ConfigureAwait(false);
        }

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
                if (journal.PendingOriginalFor(recoveryId) is { } original)
                {
                    var verified = Restore(original);
                    await journal.SetStatusAsync(recoveryId, verified
                        ? DeviceRecoveryStatus.RestoredVerified
                        : DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None).ConfigureAwait(false);
                }
            }

            return journal.EntryFor(ServiceId) is not null || journal.EntryFor("fan-unlock") is not null
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The restored fan state could not be independently confirmed."))
                : Set(DeviceServiceState.Idle);
        }
        catch
        {
            if (journal.EntryFor(ServiceId) is not null)
            {
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (journal.EntryFor("fan-unlock") is not null)
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
