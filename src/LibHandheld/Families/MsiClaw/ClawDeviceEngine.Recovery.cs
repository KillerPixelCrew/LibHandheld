using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
internal sealed partial class ClawDeviceEngine
{
    /// <remarks>
    ///     No outcome here blocks a service. A power or fan entry waits while this start cannot read the BIOS
    ///     version it is bound to, one from another BIOS is dropped, and any other is written once through the
    ///     shared journal reconciliation: a restore whose writes went through retires it, and one that failed
    ///     keeps it for the next release or start. The controller entry binds to <see cref="ClawServiceIds.McuFirmware" />,
    ///     so it is reconciled at every start.
    /// </remarks>
    private async ValueTask ReconcileOutstandingAsync(ClawIdentityState identity, CancellationToken cancellationToken)
    {
        if (_journal is not { } journal)
        {
            return;
        }

        foreach (var entry in journal.Entries<ClawRecoveryState>())
        {
            var current = entry.ServiceId == ClawServiceIds.Controller
                ? ClawServiceIds.McuFirmware
                : identity.RecoveryBinding;
            if (current is null)
            {
                DeviceTrace.Info("recovery",
                    $"'{entry.ServiceId}' waits: this start cannot read the BIOS version it is bound to.");
                continue;
            }

            await journal.ReconcileAsync<ClawRecoveryState>(entry.ServiceId, current,
                (original, token) => RestoreEntryAsync(journal, entry.ServiceId, original, token),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes an entry's original, or throws when a write failed.</summary>
    /// <remarks>An entry too incomplete to restore is retired: kept, it would hold the place of the next capture.</remarks>
    private async ValueTask RestoreEntryAsync(
        DeviceRecoveryJournal journal,
        string serviceId,
        ClawRecoveryState original,
        CancellationToken cancellationToken)
    {
        switch (serviceId)
        {
            case ClawServiceIds.Power when original.ToPower() is { } power:
                await _powerCapability!.RestoreAsync(power, cancellationToken).ConfigureAwait(false);
                await journal.MarkRestoredAsync(serviceId, CancellationToken.None).ConfigureAwait(false);
                return;
            case ClawServiceIds.Fans when original.ToFans() is { } fans:
                await _fanCapability!.RestoreAsync(fans, cancellationToken).ConfigureAwait(false);
                await journal.MarkRestoredAsync(serviceId, CancellationToken.None).ConfigureAwait(false);
                return;
            case ClawServiceIds.Controller when original.ToControllerMode() is { } mode:
                await RestoreControllerModeAsync(journal, mode, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await journal.RetireAsync(serviceId, "the recorded original is incomplete", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Puts the controller back in the journalled mode.</summary>
    /// <remarks>
    ///     The mode is read before anything is written, so this runs at every start. Otherwise, with controller
    ///     management off, a pad left in DirectInput by a failed release would stay there. A pad that is not
    ///     present yet leaves the entry as it is for the next start. The settled topology is logged, never
    ///     acted on.
    /// </remarks>
    private async ValueTask RestoreControllerModeAsync(
        DeviceRecoveryJournal journal,
        ClawControllerMode mode,
        CancellationToken cancellationToken)
    {
        var current = await _services.Controller.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || string.IsNullOrWhiteSpace(current.PhysicalLocation))
        {
            DeviceTrace.Info("recovery", "the controller is not present yet; its recovery entry waits.");
            return;
        }

        bool? matched = true;
        if (current.Mode != mode)
        {
            var restored = await _services.Mcu.SwitchModeAsync(
                mode,
                current.PhysicalLocation,
                Deadline.After(TimeSpan.FromSeconds(6)),
                cancellationToken).ConfigureAwait(false);
            matched = restored.Mode == mode
                      && HidDevices.SamePhysicalLocation(restored.PhysicalLocation, current.PhysicalLocation);
        }

        await journal.CompleteRestoreAsync(ClawServiceIds.Controller, () => matched).ConfigureAwait(false);
    }
}
