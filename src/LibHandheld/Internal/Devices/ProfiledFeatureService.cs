using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledFeatureContext(string StateDirectory, string Firmware, IDeviceSink Sink)
{
    internal required DeviceRecoveryJournal Journal { get; init; }
    internal HandheldResources Resources { get; init; } = new();
    internal bool ControllerManagementEnabled { get; init; }
    internal DeviceIdentitySnapshot Identity { get; init; } = new();
    internal string DeviceDefinitionId { get; init; } = string.Empty;
}

/// <summary>A family's additional native codec joins the same lifecycle and command serialization.</summary>
internal abstract class ProfiledFeatureService(string serviceId) : DeviceService<ProfiledCycleIdentity>(serviceId)
{
    internal abstract override IReadOnlyList<CapabilityDescriptor> Descriptors { get; }
    internal virtual bool DescriptorsChanged => false;
    internal abstract override IReadOnlyList<CapabilityState> States { get; }

    internal virtual void AcknowledgeDescriptors()
    {
    }

    internal abstract override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        string firmware,
        CancellationToken token);

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    internal virtual ValueTask RefreshAsync(string? capabilityId, CancellationToken token)
    {
        return RefreshAsync(token);
    }

    /// <summary>Reads live measurements without settings observation, recovery or watchdog writes.</summary>
    /// <remarks>
    ///     A maintenance turn runs the full <see cref="RefreshAsync(CancellationToken)" /> instead of this hook,
    ///     so a service that overrides this must also sample those measurements in its full refresh.
    /// </remarks>
    internal virtual ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    internal virtual ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }
}
