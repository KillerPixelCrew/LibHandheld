using LibHandheld.Contracts;
using LibHandheld.Internal.Services;

namespace LibHandheld.Internal.Runtime;

internal interface NativeEngine : IAsyncDisposable
{
    ValueTask<EngineStartResult> StartAsync(
        EngineStartContext context,
        CancellationToken cancellationToken);

    ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    ValueTask SuspendAsync(EngineQuiesceContext context, CancellationToken cancellationToken);

    ValueTask<EngineStartResult> ResumeAsync(
        EngineResumeContext context,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken cancellationToken);

    ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken);

    ValueTask ReleaseControllerAsync(
        EngineControllerReleaseContext context,
        CancellationToken cancellationToken);

    ValueTask SetControllerManagementAsync(
        EngineControllerManagementContext context,
        CancellationToken cancellationToken);

    ValueTask<EngineStopResult> StopAsync(
        EngineStopContext context,
        CancellationToken cancellationToken);
}

internal sealed record EngineStartContext
{
    public required IDeviceSink Host { get; init; }
    public required DeviceIdentitySnapshot Identity { get; init; }
    public HandheldResources Resources { get; init; } = new();
    public required string DeviceDefinitionId { get; init; }
    public required string StateDirectory { get; init; }
    public required DeviceRecoveryJournal Journal { get; init; }
    public required bool ControllerManagementEnabled { get; init; }
}

internal sealed record EngineStartResult
{
    public required EngineOperationalState State { get; init; }
    public CapabilityReason? Reason { get; init; }
}

internal enum EngineOperationalState
{
    Passive,
    Active,
    Degraded
}

internal sealed record EngineStopResult
{
    public required EngineStopStatus Status { get; init; }
    public CapabilityReason? Reason { get; init; }
}

internal enum EngineStopStatus
{
    Clean,
    Unverified,
    Failed
}

internal sealed record EngineQuiesceContext(Deadline Deadline);

internal sealed record EngineResumeContext(Deadline Deadline);

internal sealed record EngineControllerReleaseContext(Deadline Deadline);

internal sealed record EngineControllerManagementContext(
    bool Enabled,
    Deadline Deadline);

internal sealed record EngineStopContext(
    Deadline Deadline);
