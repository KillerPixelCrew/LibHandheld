using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;

namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledFeatureContext(string StateDirectory, string Firmware, IDeviceSink Sink)
{
    internal bool ControllerManagementEnabled { get; init; }
}

/// <summary>A family's additional native codec joins the same lifecycle and command serialization.</summary>
internal abstract class ProfiledFeatureService(string serviceId) : DeviceService<ProfiledCycleIdentity>(serviceId)
{
    internal abstract IReadOnlyList<CapabilityDescriptor> Descriptors { get; }
    internal virtual long DescriptorGeneration => 0;
    internal abstract IReadOnlyList<CapabilityState> States { get; }

    internal abstract ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token);

    internal virtual ValueTask RefreshAsync(CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    internal virtual ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }
}
