using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;

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
    public DeviceServiceState State { get; protected set; } = DeviceServiceState.Idle;

    /// <summary>Why the service is not owned, when it is not.</summary>
    public CapabilityReason? Reason { get; private set; }

    /// <summary>Takes the state and reason a lifecycle operation returned.</summary>
    /// <param name="result">The operation's result.</param>
    public void ApplyResult(DeviceServiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        State = result.State;
        Reason = result.Reason;
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
