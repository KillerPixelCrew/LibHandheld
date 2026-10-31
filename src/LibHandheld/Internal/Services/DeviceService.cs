using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Services;

/// <summary>Where one plugin service is in its device cycle.</summary>
internal enum DeviceServiceState
{
    /// <summary>Not acquired, or released cleanly.</summary>
    Idle,

    /// <summary>Being acquired.</summary>
    Acquiring,

    /// <summary>Acquired and usable.</summary>
    Owned,

    /// <summary>Not acquired because a prerequisite is missing; nothing is wrong with the device.</summary>
    Passive,

    /// <summary>Kept in the cycle but not usable right now, such as a pad waiting to reappear.</summary>
    Degraded,

    /// <summary>Being released.</summary>
    Releasing,

    /// <summary>Released, but the release could not show that every change was undone.</summary>
    ReleasedUnverified,

    /// <summary>Failed; unusable until it is acquired again.</summary>
    Faulted
}

/// <summary>The live facts one device cycle runs against.</summary>
/// <typeparam name="TIdentity">The plugin's own identity snapshot for the cycle.</typeparam>
/// <param name="Deadline">When the current lifecycle operation must finish.</param>
/// <param name="Identity">The identity read for this operation.</param>
internal readonly record struct DeviceCycleContext<TIdentity>(
    Deadline Deadline,
    TIdentity Identity);

/// <summary>The state one service operation ended in.</summary>
/// <param name="State">The resulting state.</param>
/// <param name="Reason">Why the service is not owned, when it is not.</param>
internal sealed record DeviceServiceResult(DeviceServiceState State, CapabilityReason? Reason = null);

/// <summary>The state every plugin service reports, whether or not it follows the device cycle.</summary>
/// <param name="serviceId">Stable service identifier, used in diagnostics and recovery entries.</param>
internal abstract class DeviceServiceStatus(string serviceId)
{
    /// <summary>Stable service identifier.</summary>
    public string ServiceId { get; } = serviceId;

    /// <summary>The current state.</summary>
    public DeviceServiceState State { get; private set; }

    /// <summary>When this owner's state or reason last actually changed.</summary>
    public DateTimeOffset ChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Why the service is not owned, when it is not.</summary>
    public CapabilityReason? Reason { get; private set; }

    /// <summary>Takes the state and reason a lifecycle operation returned.</summary>
    /// <param name="result">The operation's result.</param>
    public void ApplyResult(DeviceServiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _ = Set(result.State, result.Reason);
    }

    /// <summary>Marks the service faulted until it is next acquired.</summary>
    /// <param name="reason">Why it faulted.</param>
    /// <remarks>
    ///     Resume and controller re-enablement acquire the service again.
    /// </remarks>
    public void Fault(CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _ = Set(DeviceServiceState.Faulted, reason);
    }

    /// <summary>Sets the state and reason and returns them as a result.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="reason">Why the service is not owned, when it is not.</param>
    /// <returns>The result carrying the same state and reason.</returns>
    protected DeviceServiceResult Set(DeviceServiceState state, CapabilityReason? reason = null)
    {
        if (State != state || Reason != reason)
        {
            ChangedAt = DateTimeOffset.UtcNow;
        }

        State = state;
        Reason = reason;
        return new DeviceServiceResult(state, reason);
    }

    /// <summary>A missing-prerequisite reason.</summary>
    /// <param name="detail">What is missing.</param>
    /// <returns>The reason.</returns>
    protected static CapabilityReason Missing(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, detail);
    }

    /// <summary>A release refused for lack of time: nothing was written and the original stays pending.</summary>
    /// <returns>A Quiescing reason saying the original remains recorded for the next start.</returns>
    protected static CapabilityReason NoTimeToRestore()
    {
        return new CapabilityReason(CapabilityReasonCode.Quiescing,
            "Not enough time to restore; the original stays recorded for the next start.");
    }
}

/// <summary>A resource the plugin acquires for a device cycle and releases when the cycle ends.</summary>
/// <typeparam name="TIdentity">The plugin's own identity snapshot for the cycle.</typeparam>
/// <param name="serviceId">Stable service identifier.</param>
/// <remarks>
///     <see cref="DeviceServiceLifecycle" /> runs these operations and applies their results. Implementations
///     own transport cleanup and temporary-state restoration. The context deadline is data; implementations
///     must enforce it or use the caller's bounded token. Callers serialize access to mutable service state.
/// </remarks>
internal abstract class DeviceService<TIdentity>(string serviceId) : DeviceServiceStatus(serviceId)
{
    internal virtual bool RequiresControllerOwnership => false;
    internal virtual IReadOnlyList<CapabilityDescriptor> Descriptors => [];
    internal virtual IReadOnlyList<CapabilityState> States => [];
    internal virtual IReadOnlyList<OemControlDescriptor> OemControls => [];

    /// <summary>Whether the service stops for suspend and is reacquired on resume.</summary>
    public virtual bool Suspendable => false;

    internal virtual ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ValueTask.FromResult(CommandResults.Rejected(command,
            CapabilityReasonCode.Unsupported, "This service does not own the requested control."));
    }

    internal virtual ValueTask RefreshAsync(CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    internal virtual ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    internal virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Acquires the service for the cycle.</summary>
    /// <param name="context">The cycle the service is acquired for.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>Owned, Passive, Degraded or Faulted.</returns>
    public abstract ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken);

    /// <summary>Releases the service and undoes its temporary changes.</summary>
    /// <param name="context">The cycle being released.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    /// <returns>Idle, ReleasedUnverified or Faulted.</returns>
    public abstract ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken);

    /// <summary>Writes a journalled original back on release, as the last step of undoing a temporary change.</summary>
    /// <typeparam name="TOriginal">The service's typed original state.</typeparam>
    /// <param name="journal">The recovery journal holding the original under <see cref="DeviceServiceStatus.ServiceId" />.</param>
    /// <param name="original">The original to write, or null when nothing is held for this firmware.</param>
    /// <param name="deadline">The release deadline; too little time leaves the original for the next start.</param>
    /// <param name="restore">Writes the original once; completion means the transport accepted every write.</param>
    /// <param name="cancellationToken">Cancels the restore; an interrupted one is left to the lifecycle as unverified.</param>
    /// <returns>Idle once restored or with nothing to restore, ReleasedUnverified without time, Faulted on failure.</returns>
    /// <remarks>
    ///     Only an owned or faulted service restores: a faulted one may still hold captured state, and stop is its
    ///     restore point. Accepted writes retire the entry without a confirming read; a failed restore keeps it
    ///     pending and is not written again until the next release or start.
    /// </remarks>
    protected async ValueTask<DeviceServiceResult> RestoreOriginalAsync<TOriginal>(
        DeviceRecoveryJournal journal,
        TOriginal? original,
        Deadline deadline,
        Func<TOriginal, CancellationToken, ValueTask> restore,
        CancellationToken cancellationToken)
        where TOriginal : class
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(restore);
        if (State is not (DeviceServiceState.Owned or DeviceServiceState.Faulted) || original is null)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (!DeviceWriteBudget.IsAvailable(deadline))
        {
            return Set(DeviceServiceState.ReleasedUnverified, NoTimeToRestore());
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await restore(original, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure(ServiceId, "Restoration failed; the recovery entry stays pending", ex);
            return Set(DeviceServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException($"Restoring '{ServiceId}' failed", ex)));
        }

        // The writes went through, so the entry is retired even when cancellation follows them.
        await journal.MarkRestoredAsync(ServiceId, CancellationToken.None).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Stops a suspendable service for a suspend; by default the same as a release.</summary>
    /// <param name="context">The cycle being suspended.</param>
    /// <param name="cancellationToken">Cancels the suspend.</param>
    /// <returns>The state the service is left in.</returns>
    public virtual ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }
}
