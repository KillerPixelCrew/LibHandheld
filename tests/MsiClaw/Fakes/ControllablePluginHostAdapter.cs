using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Settings;
using LibHandheld.Internal.Testing;

namespace LibHandheld.Families.MsiClaw.Tests.Fakes;

internal sealed class ControllablePluginHostAdapter(long cycleGeneration) : IDeviceSink
{
    private readonly TaskCompletionSource _controllerSampleEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<(string Scope, string Message)> _faults = [];
    private readonly Lock _gate = new();
    private readonly TestPluginHostAdapter _inner = new(cycleGeneration);

    private readonly TaskCompletionSource _oemEventEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FailNextNonEmptyOemPublication { get; set; }

    public bool BlockControllerSamples { get; init; }

    public bool BlockOemEvents { get; init; }
    public TaskCompletionSource? CapabilityPublicationBlock { get; set; }

    public TaskCompletionSource CapabilityPublicationEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<CapabilityDescriptorSet> DescriptorSets => _inner.DescriptorSets;

    public IReadOnlyList<IReadOnlyList<PhysicalDeviceIdentity>> PhysicalDeviceSets =>
        _inner.PhysicalDeviceSets;

    public IReadOnlyList<IReadOnlyList<OemControlDescriptor>> OemControlSets =>
        _inner.OemControlSets;

    public IReadOnlyList<(string Scope, string Message)> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _faults];
            }
        }
    }

    public Task ControllerSampleEntered => _controllerSampleEntered.Task;

    public Task OemEventEntered => _oemEventEntered.Task;

    public long CycleGeneration => _inner.CycleGeneration;

    public ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        return _inner.PublishDescriptorsAsync(descriptors, cancellationToken);
    }

    public ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken)
    {
        if (CapabilityPublicationBlock is not { } blocked)
        {
            return _inner.PublishCapabilityStateAsync(state, cancellationToken);
        }

        CapabilityPublicationEntered.TrySetResult();
        return new ValueTask(blocked.Task);
    }

    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken)
    {
        return _inner.PublishPhysicalDevicesAsync(devices, output, cancellationToken);
    }

    public async ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        await _inner.PublishControllerSampleAsync(sample, cancellationToken).ConfigureAwait(false);
        if (!BlockControllerSamples)
        {
            return;
        }

        _controllerSampleEntered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        await _inner.PublishOemControlsAsync(controls, cancellationToken).ConfigureAwait(false);
        if (controls.Count > 0 && FailNextNonEmptyOemPublication)
        {
            FailNextNonEmptyOemPublication = false;
            throw new IOException("Synthetic OEM publication failure after acceptance.");
        }
    }

    public async ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken)
    {
        await _inner.PublishOemEventAsync(controlEvent, cancellationToken).ConfigureAwait(false);
        if (!BlockOemEvents)
        {
            return;
        }

        _oemEventEntered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken)
    {
        return _inner.PublishSettingsManifestAsync(manifest, cancellationToken);
    }

    public void Trace(DeviceTraceLevel level, string scope, string message)
    {
        _inner.Trace(level, scope, message);
    }

    public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
    {
        _inner.Trace(level, scope, message);
    }

    public void ReportFault(string scope, string message)
    {
        lock (_gate)
        {
            _faults.Add((scope, message));
        }

        _inner.Trace(DeviceTraceLevel.Error, scope, message);
    }
}
