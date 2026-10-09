using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Testing;

/// <summary>In-memory host adapter for focused plugin tests.</summary>
/// <remarks>
///     Records publications without dispatching commands or enforcing production descriptor, generation,
///     freshness or settings validation. Collection getters return snapshots; records and nested collections
///     are retained by reference except physical-device and OEM-control lists, which are copied on receipt.
///     Recording is synchronized but this adapter does not model host lifecycle or hardware ownership.
/// </remarks>
internal sealed class TestPluginHostAdapter : IDeviceSink
{
    private readonly List<CapabilityState> _capabilityStates = [];
    private readonly List<(DeviceTraceLevel Level, string Scope, string Key, string Message)> _changes = [];
    private readonly List<CanonicalControllerSample> _controllerSamples = [];
    private readonly List<CapabilityDescriptorSet> _descriptorSets = [];
    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<OemControlDescriptor>> _oemControlSets = [];
    private readonly List<OemControlEvent> _oemEvents = [];
    private readonly List<IReadOnlyList<PhysicalDeviceIdentity>> _physicalDeviceSets = [];
    private readonly List<PluginSettingsManifest> _settingsManifests = [];
    private readonly List<(DeviceTraceLevel Level, string Scope, string Message)> _traces = [];

    /// <summary>Creates an adapter for one cycle generation.</summary>
    /// <param name="cycleGeneration">Cycle generation exposed to the plugin.</param>
    public TestPluginHostAdapter(long cycleGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycleGeneration);
        CycleGeneration = cycleGeneration;
    }

    /// <summary>Descriptor replacements in publication order.</summary>
    public IReadOnlyList<CapabilityDescriptorSet> DescriptorSets => Snapshot(_descriptorSets);

    /// <summary>Capability observations in publication order.</summary>
    public IReadOnlyList<CapabilityState> CapabilityStates => Snapshot(_capabilityStates);

    /// <summary>Physical-device sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<PhysicalDeviceIdentity>> PhysicalDeviceSets =>
        Snapshot(_physicalDeviceSets);

    /// <summary>Haptic capabilities from the most recent physical-device publication.</summary>
    public HapticCapabilities? PublishedOutput { get; private set; }

    /// <summary>Canonical controller samples in publication order.</summary>
    public IReadOnlyList<CanonicalControllerSample> ControllerSamples => Snapshot(_controllerSamples);

    /// <summary>OEM-control descriptor sets in publication order.</summary>
    public IReadOnlyList<IReadOnlyList<OemControlDescriptor>> OemControlSets => Snapshot(_oemControlSets);

    /// <summary>OEM-control events in publication order.</summary>
    public IReadOnlyList<OemControlEvent> OemEvents => Snapshot(_oemEvents);

    /// <summary>Settings manifests the plugin declared, in publication order.</summary>
    public IReadOnlyList<PluginSettingsManifest> SettingsManifests => Snapshot(_settingsManifests);

    /// <summary>Trace lines in emission order.</summary>
    /// <remarks>Empty messages are omitted; retained messages are not sanitized or length-limited.</remarks>
    public IReadOnlyList<(DeviceTraceLevel Level, string Scope, string Message)> Traces =>
        Snapshot(_traces);

    /// <summary>Keyed state lines in emission order, from <see cref="DeviceTrace.Change" />.</summary>
    public IReadOnlyList<(DeviceTraceLevel Level, string Scope, string Key, string Message)> Changes =>
        Snapshot(_changes);

    /// <inheritdoc />
    public long CycleGeneration { get; }

    /// <inheritdoc />
    public ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        return RecordAsync(_descriptorSets, descriptors, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken)
    {
        return RecordAsync(_capabilityStates, state, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        PublishedOutput = output;
        return RecordAsync(
            _physicalDeviceSets,
            [.. devices],
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        return RecordAsync(_controllerSamples, sample, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return RecordAsync(
            _oemControlSets,
            [.. controls],
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken)
    {
        return RecordAsync(_oemEvents, controlEvent, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken)
    {
        return RecordAsync(_settingsManifests, manifest, cancellationToken);
    }

    /// <inheritdoc />
    public void Trace(DeviceTraceLevel level, string scope, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        lock (_gate)
        {
            _traces.Add((level, scope ?? string.Empty, message));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Recorded with its key but without suppression: a test asserting that a poll loop reports a
    ///     transition should see every call it made, and a test asserting suppression would be testing
    ///     the host's dedup rather than the plugin's decision to report.
    /// </remarks>
    public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        lock (_gate)
        {
            _changes.Add((level, scope ?? string.Empty, key ?? string.Empty, message));
        }
    }

    private ValueTask RecordAsync<T>(
        List<T> target,
        T item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            target.Add(item);
        }

        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> source)
    {
        lock (_gate)
        {
            return [.. source];
        }
    }
}
