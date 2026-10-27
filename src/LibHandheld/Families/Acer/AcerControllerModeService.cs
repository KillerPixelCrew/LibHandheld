using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Owns the native rear-button reporting mode only while the host manages the controller.</summary>
/// <remarks>
///     DInput 2 plus rear Combination 1 supplies native gamepad/OEM usages without prior Predator Sense
///     assignments. Macro mode 0 can otherwise consume rear presses. The original pair is captured
///     before command 24 and restored on release/suspend. This service exposes no user-facing mode
///     control and uses the verified F90A mode/rumble lane and never saves controller ROM.
/// </remarks>
internal sealed class AcerControllerModeService(ProfiledFeatureContext context,
    Func<IAcerControllerModeTransport?>? open = null) : ProfiledFeatureService("acer-controller-mode")
{
    private static readonly AcerControllerMode Managed = new(Gamepad: 2, RearKeys: 1);
    private IAcerControllerModeTransport? _transport;
    private bool _restoreUnverified;

    internal override bool RequiresControllerOwnership => true;
    public override bool Suspendable => true;
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => [];
    internal override IReadOnlyList<CapabilityState> States => [];

    /// <summary>Reconciles any pending mode change, snapshots the actual pair and selects managed reporting if needed.</summary>
    /// <remarks>
    ///     An unresolved previous mutation prevents a new dispatch. The snapshot getter is necessary
    ///     recovery/topology data, not a readback gate on ordinary user settings. Already-managed modes
    ///     are left untouched and require no new recovery record.
    /// </remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _restoreUnverified = false;
        _transport = open is null ? AcerControllerTransport.Open() : open();
        if (_transport is null)
        {
            return Set(DeviceServiceState.Passive, Missing("The measured Atlas controller mode lane is unavailable."));
        }

        await context.Journal.ReconcileAsync<AcerControllerMode>(ServiceId, context.Firmware, RestoreAsync, token)
            .ConfigureAwait(false);
        if (context.Journal.HasUnrestoredMutation(ServiceId))
        {
            throw new IOException("The previous Atlas controller mode has not been restored; no new mode was dispatched.");
        }

        // The original topology/mode is recovery data. It is never inferred from a user profile.
        var original = await _transport.ReadModeAsync(token).ConfigureAwait(false);
        if (original != Managed)
        {
            await context.Journal.BeginAsync(ServiceId, context.Firmware, original, token).ConfigureAwait(false);
            await _transport.WriteModeAsync(Managed, token, () => { }).ConfigureAwait(false);
        }

        return Set(DeviceServiceState.Owned);
    }

    /// <summary>Writes the retained gamepad/rear pair once and independently classifies query-based restoration.</summary>
    /// <remarks>An absent observation is RestoredUnverified; a differing pair is RestoreFailed and remains journaled.</remarks>
    private async ValueTask RestoreAsync(AcerControllerMode original, CancellationToken token)
    {
        await _transport!.WriteModeAsync(original, token, () => { }).ConfigureAwait(false);
        bool? verified = null;
        try
        {
            verified = await _transport.ReadModeAsync(token).ConfigureAwait(false) == original;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Restored controller mode observation unavailable: {exception.Message}");
        }

        _restoreUnverified |= verified != true;
        await context.Journal.SetStatusAsync(ServiceId,
            verified == true ? DeviceRecoveryStatus.RestoredVerified
                : verified == false ? DeviceRecoveryStatus.RestoreFailed : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Restores the captured pair before closing the lane; an incomplete restore cannot become Idle.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_transport is not null)
            {
                await context.Journal.ReconcileAsync<AcerControllerMode>(ServiceId, context.Firmware, RestoreAsync, token)
                    .ConfigureAwait(false);
            }

            return Set(_restoreUnverified || context.Journal.HasUnrestoredMutation(ServiceId)
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes only the command lane; disposing alone does not restore controller mode.</summary>
    internal override async ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
            "Controller mode is owned by the input lifecycle, not a user setting."));
    }
}
