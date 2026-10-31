using LibHandheld.Contracts;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

/// <summary>A cycle service whose temporary changes the recovery journal restores on release.</summary>
/// <param name="serviceId">Stable journal/service identity used across cycles and package versions.</param>
internal abstract class ClawJournalledService(string serviceId) : DeviceService<ClawIdentityState>(serviceId)
{
    /// <summary>Writes a pending original back. A restore is complete once its writes went through.</summary>
    /// <remarks>
    ///     Captured originals remain available after a failed restore. A release without the time to write
    ///     leaves its original for the next start. Recovery bookkeeping never blocks the next explicit command.
    /// </remarks>
    /// <typeparam name="TSnapshot">Capability-specific original-state representation.</typeparam>
    /// <param name="context">Current identity and deadline; restoration requires the captured firmware binding.</param>
    /// <param name="journal">Borrowed durable recovery owner; status writes are attempted without caller cancellation.</param>
    /// <param name="readSnapshot">Extracts a valid typed original, or null when the entry cannot be restored.</param>
    /// <param name="restoreAsync">
    ///     Writes the original once; completion means accepted writes, without a required confirming
    ///     read.
    /// </param>
    /// <param name="cancellationToken">
    ///     Cancels before dispatch or inside restore; an interrupted restore is recorded as
    ///     failed.
    /// </param>
    /// <returns>The service state after restoration, skipping, or insufficient-budget release.</returns>
    /// <remarks>Transport and cancellation failures propagate; journal storage failures are logged.</remarks>
    protected async ValueTask<DeviceServiceResult> RestoreJournalledAsync<TSnapshot>(
        DeviceCycleContext<ClawIdentityState> context,
        DeviceRecoveryJournal journal,
        Func<ClawRecoveryState?, TSnapshot?> readSnapshot,
        Func<TSnapshot, CancellationToken, ValueTask> restoreAsync,
        CancellationToken cancellationToken)
        where TSnapshot : class
    {
        // Only on the BIOS the original was captured under: an entry a start could not compare waits.
        if (State is not DeviceServiceState.Owned
            || journal.EntryFor<ClawRecoveryState>(ServiceId) is not { } entry
            || !string.Equals(entry.FirmwareIdentity, context.Identity.RecoveryBinding, StringComparison.Ordinal)
            || readSnapshot(entry.OriginalState) is not { } restoreSnapshot)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (!DeviceWriteBudget.IsAvailable(context.Deadline))
        {
            return Set(DeviceServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Not enough time to restore; the original stays recorded for the next start."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await restoreAsync(restoreSnapshot, cancellationToken).ConfigureAwait(false);
        await journal.MarkRestoredAsync(ServiceId, CancellationToken.None).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }
}

internal static class ServiceIds
{
    public const string OemEvents = "msi-oem-events";
    public const string Power = "msi-power";
    public const string ChargeLimit = "msi-charge-limit";
    public const string Fans = "msi-fans";
    public const string Telemetry = "msi-telemetry";
    public const string Lighting = "claw-lighting";
    public const string Motion = "claw-motion";
    public const string Controller = "physical-controller";
    public const string ChordSuppressor = "firmware-chord-suppressor";
}

internal static class ClawFirmwareIdentities
{
    /// <summary>
    ///     Deliberately carries no revision. A controller journal entry only records the mode to put
    ///     back, and that write is valid on any MCU firmware the exact machine ships with; a
    ///     revision here would strand the entry, and with it the controller, after every firmware
    ///     update.
    /// </summary>
    public const string Mcu = "mcu";

    /// <summary>The power and fan binding for a BIOS version, <c>bios:&lt;version&gt;</c>.</summary>
    /// <param name="biosVersion">SMBIOS BIOS version to preserve verbatim in the binding.</param>
    /// <returns>The bios:-prefixed binding; no version parsing is performed.</returns>
    public static string Bios(string biosVersion)
    {
        return "bios:" + biosVersion;
    }
}
