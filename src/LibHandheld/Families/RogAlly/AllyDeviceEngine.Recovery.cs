using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
internal sealed partial class AllyDeviceEngine
{
    /// <remarks>
    ///     No outcome here blocks a service. An entry whose transport is not there yet waits, one from another
    ///     BIOS is dropped, and any other is written once through the shared journal reconciliation: a restore
    ///     that dispatched removes it, and one that failed to dispatch leaves it pending for the next release or
    ///     start, with no status write.
    /// </remarks>
    private async ValueTask ReconcileOutstandingAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (_journal is not { } journal || _model is null)
        {
            return;
        }

        foreach (var entry in journal.Entries<AllyRecoveryState>())
        {
            string? current;
            if (entry.ServiceId == AllyServiceIds.Controller)
            {
                current = _vendor is not null && await _vendor.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
                    ? AllyServiceIds.McuFirmware
                    : null;
            }
            else
            {
                current = _hardware.Acpi.TryOpen() ? identity.FirmwareIdentity : null;
            }

            if (current is null)
            {
                DeviceTrace.Info("recovery", $"'{entry.ServiceId}' waits: its transport is not available yet.");
                continue;
            }

            await journal.ReconcileAsync<AllyRecoveryState>(entry.ServiceId, current, async (original, token) =>
            {
                await RestoreEntryAsync(entry.ServiceId, original, token).ConfigureAwait(false);
                await journal.MarkRestoredAsync(entry.ServiceId, CancellationToken.None).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes an entry's original, or throws when a write failed to dispatch.</summary>
    private async ValueTask RestoreEntryAsync(
        string serviceId,
        AllyRecoveryState original,
        CancellationToken cancellationToken)
    {
        switch (serviceId)
        {
            case AllyServiceIds.Power when original.ToPower() is { } power:
                await new AllyPowerCapability(_hardware.Acpi, _model!, _hardware.Delay)
                    .RestoreAsync(power, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case AllyServiceIds.Fans when original.ToFans() is { } fans:
                await new AllyFanCapability(_hardware.Acpi, _hardware.Delay)
                    .RestoreAsync(fans, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case AllyServiceIds.Controller:
                // HC sends every table even when one is refused. Each gets one attempt, and any refusal
                // leaves the recovery entry pending.
                var refused = await ControllerService.WriteTablesAsync(_vendor!, AllyProtocol.DefaultConfiguration,
                    cancellationToken).ConfigureAwait(false);
                if (refused.Count != 0)
                {
                    throw new IOException($"{refused.Count} factory controller tables could not be written back.");
                }

                return;
        }
    }
}
