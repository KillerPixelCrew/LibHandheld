using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Devices;

/// <summary>A provider's control publications are merged by the shared engine; input goes directly to its consumer.</summary>
internal sealed class ServicePublications(IDeviceSink sink) : IDeviceSink
{
    private readonly Dictionary<(string, string?), CapabilityState> _states = [];
    internal IReadOnlyList<CapabilityState> States => _states.Values.ToArray();
    internal IReadOnlyList<OemControlDescriptor> OemControls { get; private set; } = [];
    internal bool DescriptorsChanged { get; private set; }

    public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken token)
    {
        DescriptorsChanged = true;
        var ids = descriptors.Descriptors.Select(descriptor => (descriptor.CapabilityId, descriptor.InstanceId))
            .ToHashSet();
        foreach (var id in _states.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _states.Remove(id);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken token)
    {
        _states[(state.CapabilityId, state.InstanceId)] = state;
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishOemControlsAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken token)
    {
        OemControls = controls;
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPhysicalDevicesAsync(IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output, CancellationToken token)
    {
        return sink.PublishPhysicalDevicesAsync(devices, output, token);
    }

    public ValueTask PublishControllerSampleAsync(CanonicalControllerSample sample, CancellationToken token)
    {
        return sink.PublishControllerSampleAsync(sample, token);
    }

    public ValueTask PublishOemEventAsync(OemControlEvent controlEvent, CancellationToken token)
    {
        return sink.PublishOemEventAsync(controlEvent, token);
    }

    public void ReportFault(string scope, string message)
    {
        sink.ReportFault(scope, message);
    }

    internal void AcknowledgeDescriptors()
    {
        DescriptorsChanged = false;
    }
}
