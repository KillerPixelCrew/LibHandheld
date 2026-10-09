using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
internal sealed partial class ClawDeviceEngine
{
    /// <remarks>
    ///     No outcome here blocks a service. A power or fan entry follows <see cref="ClawRecoveryJournal.Decide" />;
    ///     a restore that throws is recorded as failed and is not written again until a command re-arms it.
    /// </remarks>
    private async ValueTask ReconcileOutstandingAsync(
        IReadOnlyList<DeviceRecoveryEntry<ClawRecoveryState>> entries,
        ClawIdentityState identity,
        ClawPowerCapability powerCapability,
        ClawFanCapability fanCapability,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.ServiceId == ServiceIds.Controller)
            {
                await ReconcileControllerEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var action = ClawRecoveryJournal.Decide(
                entry,
                identity.WmiAvailable ? identity.RecoveryBinding : null,
                identity.WmiAvailable ? identity.LegacyRecoveryBinding : null);
            switch (action)
            {
                case ClawReconciliationAction.Wait:
                    DeviceTrace.Info("recovery",
                        $"the {entry.ServiceId} entry waits: this start cannot read the firmware it is bound to.");
                    continue;
                case ClawReconciliationAction.Keep:
                    DeviceTrace.Warn("recovery",
                        $"the {entry.ServiceId} entry kept its {entry.Status} restore; it is not written again "
                        + "until a command re-arms it.");
                    continue;
                case ClawReconciliationAction.Discard:
                    var current = ClawFirmwareIdentities.IsLegacy(entry.FirmwareIdentity)
                        ? identity.LegacyRecoveryBinding
                        : identity.RecoveryBinding;
                    DeviceTrace.Warn("recovery",
                        $"dropped the {entry.Status} {entry.ServiceId} entry bound to {entry.FirmwareIdentity}: "
                        + $"the firmware now reads {current ?? "unknown"}, so its captured state is not restored.");
                    await _journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoredVerified,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                case ClawReconciliationAction.Restore:
                default:
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeviceRecoveryStatus status;
            try
            {
                if (ClawRecoveryValues.TryPower(entry.OriginalState, out var power))
                {
                    await powerCapability.RestoreAsync(power!, cancellationToken).ConfigureAwait(false);
                }
                else if (ClawRecoveryValues.TryFans(entry.OriginalState, out var fans))
                {
                    await fanCapability.RestoreAsync(fans!, cancellationToken).ConfigureAwait(false);
                }

                status = DeviceRecoveryStatus.RestoredVerified;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Failure("recovery", $"restoring the {entry.ServiceId} entry failed; it is kept", ex);
                status = DeviceRecoveryStatus.RestoreFailed;
            }

            // Preserve the attempted restore's outcome even when cancellation interrupted the writes.
            // Leaving it pending would replay an uncertain restore at the next start.
            await _journal.SetStatusAsync(entry.ServiceId, status, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Puts the controller back in the journalled mode, whatever the entry's status.</summary>
    /// <remarks>
    ///     The mode is read before anything is written, so this runs at every start. Otherwise, with controller
    ///     management off, a pad left in DirectInput by a failed release would stay there. A pad that is not
    ///     present yet leaves the entry as it is for the next start.
    /// </remarks>
    private async ValueTask ReconcileControllerEntryAsync(
        DeviceRecoveryEntry<ClawRecoveryState> entry,
        CancellationToken cancellationToken)
    {
        if (!ClawRecoveryValues.TryControllerMode(entry.OriginalState, out var mode))
        {
            return;
        }

        DeviceRecoveryStatus status;
        try
        {
            var current = await _services.Controller.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (current is null || string.IsNullOrWhiteSpace(current.PhysicalLocation))
            {
                DeviceTrace.Info("recovery", "the controller is not present yet; its recovery entry waits.");
                return;
            }

            status = DeviceRecoveryStatus.RestoredVerified;
            if (current.Mode != mode)
            {
                var restored = await _services.Mcu.SwitchModeAsync(
                    mode,
                    current.PhysicalLocation,
                    Deadline.After(TimeSpan.FromSeconds(6)),
                    cancellationToken).ConfigureAwait(false);
                if (restored.Mode != mode
                    || !HidDevices.SamePhysicalLocation(restored.PhysicalLocation, current.PhysicalLocation))
                {
                    status = DeviceRecoveryStatus.RestoredUnverified;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            DeviceTrace.Failure("recovery", "restoring the controller mode failed; the next start tries again", ex);
            status = DeviceRecoveryStatus.RestoreFailed;
        }

        DeviceTrace.Info("recovery", $"controller mode entry: {status}.");
        await _journal!.SetStatusAsync(entry.ServiceId, status, CancellationToken.None).ConfigureAwait(false);
    }

    private void BlockService(string serviceId, CapabilityReason reason)
    {
        var service = ServiceFor(serviceId);
        service?.ReconciliationBlockReason = reason;
    }

    private DeviceServiceStatus? ServiceFor(string serviceId)
    {
        return serviceId switch
        {
            ServiceIds.Power => _power,
            ServiceIds.Fans => _fans,
            ServiceIds.Controller => _controller,
            _ => null
        };
    }
}
