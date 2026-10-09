using LibHandheld.Contracts;
using LibHandheld.Internal.Services;

namespace LibHandheld.Internal.Devices;

internal sealed partial class ProfiledDeviceEngine
{
    private readonly Dictionary<(string Id, string? Instance), DeviceService<ProfiledCycleIdentity>>
        _controlOwners = [];

    private void BuildSurface()
    {
        _controlOwners.Clear();
        List<CapabilityDescriptor> descriptors = [];
        foreach (var service in _services)
        {
            foreach (var descriptor in service.Descriptors)
            {
                if (!_controlOwners.TryAdd((descriptor.CapabilityId, descriptor.InstanceId), service))
                {
                    throw new InvalidOperationException("Two native services declared the same control identity.");
                }

                descriptors.Add(descriptor);
            }
        }

        _surface = new CapabilityDescriptorSet { Descriptors = descriptors.ToArray() };
        foreach (var service in _nativeServices)
        {
            service.AcknowledgeDescriptors();
        }
    }

    private async ValueTask PublishAsync(CancellationToken token)
    {
        if (_sink is null || _surface is null)
        {
            return;
        }

        foreach (var service in _services)
        {
            // Evaluate a provider's observation collection once, rather than once for each control.
            var states = service.States;
            foreach (var state in states)
            {
                if (_controlOwners.TryGetValue((state.CapabilityId, state.InstanceId), out var owner) &&
                    owner == service)
                {
                    await _sink.PublishCapabilityStateAsync(state, token).ConfigureAwait(false);
                }
            }
        }
    }
}
