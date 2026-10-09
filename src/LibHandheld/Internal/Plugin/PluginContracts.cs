using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Plugin;

/// <summary>The entry point of the sole installed device package.</summary>
/// <remarks>
///     The interface is semantic at the host boundary. Implementations own their hardware transports
///     internally; none of those transports or handles can be returned through this API. The host owns
///     sequencing and generations; implementations own cooperative cancellation and native resource cleanup.
///     A canceled wait does not prove a dispatched hardware operation stopped or that its changes were undone.
/// </remarks>
internal interface IDeviceEngine : IAsyncDisposable
{
    /// <summary>Stable package identifier from <c>plugin.wsgm.json</c>.</summary>
    string PackageId { get; }

    /// <summary>Detects an exact supported device without acquiring a mutable resource.</summary>
    /// <param name="context">Normalized host observations.</param>
    /// <param name="cancellationToken">Cancels detection.</param>
    /// <returns>Exact detection outcome.</returns>
    ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken);

    /// <summary>Begins one device cycle.</summary>
    /// <param name="context">Host adapter, cycle generation, and package-owned state directory.</param>
    /// <param name="cancellationToken">Cancels startup and requires acquired services to unwind.</param>
    /// <returns>The plugin's aggregate operational state after startup.</returns>
    ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken);

    /// <summary>Applies one semantic capability command after authoritative plugin revalidation.</summary>
    /// <param name="command">Semantic command from WSGM.</param>
    /// <param name="cancellationToken">
    ///     Requests cooperative cancellation. Once a write may have reached hardware, report uncertainty
    ///     rather than a pre-dispatch rejection unless the effect is independently known.
    /// </param>
    /// <returns>A result carrying the request's command ID; uncertain writes must not be retried automatically.</returns>
    ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    /// <summary>Receives the effective value of every setting the plugin declared.</summary>
    /// <param name="values">Every declared setting's value, already validated by WSGM.</param>
    /// <param name="cancellationToken">Cancels application.</param>
    /// <returns>A task completing when the plugin has taken the values into account.</returns>
    /// <remarks>
    ///     Called once after start and again on every change, always as a complete set. A plugin that
    ///     declares no settings never sees this, so the default implementation does nothing rather than
    ///     forcing every plugin to carry an empty override.
    ///     <para>
    ///         These are preferences, not hardware commands: applying one may change how the plugin behaves
    ///         but must not become a hidden path for writing device state that WSGM cannot see.
    ///     </para>
    /// </remarks>
    ValueTask ApplySettingsAsync(
        IReadOnlyList<DeviceSettingValue> values,
        CancellationToken cancellationToken
    )
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Quiesces volatile work for suspend or session lock.</summary>
    /// <param name="context">Bounded quiescence deadline.</param>
    /// <param name="cancellationToken">Cancels waiting at the deadline.</param>
    /// <returns>A task completing when no new long operation can begin.</returns>
    ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken);

    /// <summary>Revalidates identity and reacquires resources after resume.</summary>
    /// <param name="context">New device generation and deadline.</param>
    /// <param name="cancellationToken">Cancels resume.</param>
    /// <returns>The plugin's aggregate operational state after resume.</returns>
    ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken);

    /// <summary>Returns bounded, plugin-owned diagnostic facts without exposing transports.</summary>
    /// <param name="cancellationToken">Cancels diagnostic collection.</param>
    /// <returns>Current service and recovery details.</returns>
    ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>Applies canonical virtual-target output to the physical device.</summary>
    /// <param name="frame">Complete motor state with normalized intensities; a silent frame requests an explicit stop.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>A task completing when the frame was handled or explicitly dropped.</returns>
    ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken);

    /// <summary>Stops physical-controller reading and restores the package's documented native controller baseline.</summary>
    /// <param name="context">Handoff scope and deadline.</param>
    /// <param name="cancellationToken">Cancels waiting while still requiring best-effort cleanup.</param>
    /// <returns>A task completing once the plugin let go of the controller.</returns>
    /// <remarks>
    ///     Best effort, like HC's <c>Close</c>: write the device back, log what did not work, and return.
    ///     The host never waits for a readback of the result.
    /// </remarks>
    ValueTask ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken);

    /// <summary>Enables or disables physical-controller ownership while other resources continue.</summary>
    /// <param name="context">Wanted state and deadline.</param>
    /// <param name="cancellationToken">Cancels acquisition or bounded release.</param>
    /// <returns>A task completing after controller resource state was republished.</returns>
    ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken);

    /// <summary>Restores and releases every remaining resource.</summary>
    /// <param name="context">Terminal reason and cleanup deadline.</param>
    /// <param name="cancellationToken">Cancels waiting at the host deadline.</param>
    /// <returns>Whether cleanup was verified, unverified, or failed.</returns>
    ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken);
}

/// <summary>Read-only observations supplied for exact device detection.</summary>
internal sealed record PluginDetectionContext
{
    /// <summary>Normalized machine and device identity.</summary>
    public required DeviceIdentitySnapshot Identity { get; init; }
}

/// <summary>Outcome of exact plugin detection.</summary>
internal sealed record PluginDetectionResult
{
    /// <summary>Whether an exact device definition matched.</summary>
    public required bool Matched { get; init; }

    /// <summary>Matched device definition, present only when <see cref="Matched" /> is true.</summary>
    public string? DeviceDefinitionId { get; init; }

    /// <summary>Why detection failed or stayed passive.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Inputs to one process-long start.</summary>
internal sealed record PluginStartContext
{
    /// <summary>Borrowed host publication surface, retained for this plugin lifetime and never disposed by the plugin.</summary>
    public required IDeviceSink Host { get; init; }

    /// <summary>Cycle generation owning all handles opened during startup.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Exact matched device definition.</summary>
    public required string DeviceDefinitionId { get; init; }

    /// <summary>Private writable directory for package-owned durable state.</summary>
    /// <remarks>
    ///     The plugin owns the schema and atomicity of every file below this directory. The host does
    ///     not interpret or merge those files, and the plugin must keep them bounded.
    /// </remarks>
    public required string StateDirectory { get; init; }

    /// <summary>Whether to acquire physical-controller ownership; other services may still run when false.</summary>
    public required bool ControllerManagementEnabled { get; init; }
}

/// <summary>The plugin's aggregate state after starting or resuming its direct services.</summary>
internal sealed record PluginStartResult
{
    /// <summary>Whether services are active, passive, or partly degraded.</summary>
    public required PluginOperationalState State { get; init; }

    /// <summary>Structured reason when the aggregate state is not active.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Aggregate result of starting or resuming direct plugin services.</summary>
internal enum PluginOperationalState
{
    /// <summary>No mutable service could be acquired.</summary>
    Passive,

    /// <summary>Every required service started successfully.</summary>
    Active,

    /// <summary>At least one service is usable and at least one is unavailable.</summary>
    Degraded
}

/// <summary>Bounded plugin-owned diagnostic facts.</summary>
internal sealed record PluginDiagnostics
{
    /// <summary>Short stable keys and sanitized values for current service and recovery state.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Truthful aggregate result of terminal plugin cleanup.</summary>
internal sealed record PluginStopResult
{
    /// <summary>Whether restoration was verified, unverified, or failed.</summary>
    public required PluginStopStatus Status { get; init; }

    /// <summary>Structured detail when restoration was not fully verified.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Aggregate terminal cleanup status.</summary>
internal enum PluginStopStatus
{
    /// <summary>Every changed temporary state was restored and verified.</summary>
    Clean,

    /// <summary>Cleanup ran, but at least one restoration could not be confirmed.</summary>
    Unverified,

    /// <summary>At least one restoration failed.</summary>
    Failed
}

/// <summary>Bounded suspend or lock quiescence.</summary>
/// <param name="Deadline">Active-time deadline after which the host stops waiting.</param>
internal sealed record PluginQuiesceContext(Deadline Deadline);

/// <summary>Bounded resume into a fresh cycle generation.</summary>
/// <param name="CycleGeneration">Generation that all newly opened handles belong to.</param>
/// <param name="Deadline">Active-time deadline after which the host stops waiting.</param>
internal sealed record PluginResumeContext(long CycleGeneration, Deadline Deadline);

/// <summary>Controller-only or full release request.</summary>
/// <param name="Scope">Whether the process-long device cycle continues.</param>
/// <param name="Deadline">Active-time deadline for stopping acquisition and restoring topology.</param>
internal sealed record PluginControllerReleaseContext(HandoffScope Scope, Deadline Deadline);

/// <summary>Controller-only ownership transition inside a continuing device cycle.</summary>
/// <param name="Enabled">Whether physical acquisition should be active.</param>
/// <param name="Deadline">Active-time transition deadline.</param>
/// <remarks>The device cycle and its generation continue: turning the controller on is not a new device.</remarks>
internal sealed record PluginControllerManagementContext(
    bool Enabled,
    Deadline Deadline);

/// <summary>Why one device cycle is ending.</summary>
internal enum PluginStopReason
{
    /// <summary>WSGM is exiting normally.</summary>
    WsgmExiting,

    /// <summary>The user disabled Device Integration.</summary>
    IntegrationDisabled,

    /// <summary>WSGM is updating and uses the compressed cleanup budget.</summary>
    Updating,

    /// <summary>The interactive session is ending.</summary>
    SessionEnding,

    /// <summary>WSGM is being uninstalled after a bounded restoration attempt.</summary>
    Uninstalling,

    /// <summary>The startup owner canceled after plugin activation may have begun.</summary>
    StartCanceled,

    /// <summary>Startup failed after plugin activation may have begun.</summary>
    StartFailed,

    /// <summary>A plugin-owned background service failed during an active cycle.</summary>
    RuntimeFault
}

/// <summary>Terminal cleanup request.</summary>
/// <param name="Reason">Why the cycle is ending.</param>
/// <param name="Deadline">Active-time deadline for plugin-owned restoration.</param>
internal sealed record PluginStopContext(
    PluginStopReason Reason,
    Deadline Deadline);
