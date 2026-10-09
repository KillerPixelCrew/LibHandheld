using System.Collections.ObjectModel;
using LibHandheld.Contracts;
using LibHandheld.Families.MsiClaw;
using LibHandheld.Families.RogAlly;
using LibHandheld.Internal.Settings;
using N = LibHandheld.Internal.Plugin;
using NC = LibHandheld.Internal.Capabilities;
using NI = LibHandheld.Internal.Input;
using NL = LibHandheld.Internal.Lifecycle;

namespace LibHandheld;

/// <summary>Direct typed access to native handheld services, independent of application policy.</summary>
/// <remarks>
///     Callers own cycle generations and lifecycle sequencing. Await native completion before disposal; cancellation
///     requests cooperation and does not prove a dispatched write stopped. No physical resource is opened by detection or
///     construction.
/// </remarks>
public sealed partial class HandheldDevice : IAsyncDisposable, N.IDeviceSink
{
    private readonly Action<DeviceLogEntry>? _diagnostics;
    private readonly N.IDeviceEngine _engine;
    private readonly Lock _operations = new();
    private readonly string _stateDirectory;
    private long _cycleGeneration;
    private Task? _disposeTask;
    private bool _disposing;
    private TaskCompletionSource? _drained;
    private int _inFlight;

    private HandheldDevice(HandheldDefinition definition, string stateDirectory, Action<DeviceLogEntry>? diagnostics)
    {
        Definition = definition;
        _stateDirectory = stateDirectory;
        _diagnostics = diagnostics;
        _engine = definition.FamilyId == ClawHardwareFacts.PackageId ? new ClawDeviceEngine() : new AllyDeviceEngine();
    }

    /// <summary>All currently implemented exact models. Additional families are added through their native implementations.</summary>
    public static IReadOnlyList<HandheldDefinition> Definitions { get; } = Array.AsReadOnly(
        ClawModels.All.Select(m =>
                new HandheldDefinition(m.DefinitionId, ClawHardwareFacts.PackageId, m.DisplayName, "msi-claw"))
            .Concat(AllyModels.All.Select(m =>
                new HandheldDefinition(m.DefinitionId, AllyModels.PackageId, m.DisplayName, m.GlyphProfileId)))
            .ToArray());

    /// <summary>Exact immutable model selected for this instance.</summary>
    public HandheldDefinition Definition { get; }

    /// <summary>Borrowed typed publication observer. The device never disposes this consumer.</summary>
    public IHandheldObserver? Observer { get; set; }

    /// <summary>
    ///     Joins outstanding native calls and disposes resources. Call StopAsync first to restore hardware; disposal
    ///     itself never writes native state.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_operations)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposing = true;
            var pending = _inFlight == 0
                ? Task.CompletedTask
                : (_drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = Task.Run(async () =>
            {
                await pending.ConfigureAwait(false);
                await _engine.DisposeAsync().ConfigureAwait(false);
            });
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Current consumer-owned native cycle generation.</summary>
    public long CycleGeneration => Interlocked.Read(ref _cycleGeneration);

    ValueTask N.IDeviceSink.PublishDescriptorsAsync(NC.CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        return Observer?.DescriptorsChangedAsync(HandheldConversions.ToPublic(descriptors), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishCapabilityStateAsync(NC.CapabilityState state, CancellationToken cancellationToken)
    {
        return Observer?.StateChangedAsync(HandheldConversions.ToPublic(state), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishPhysicalDevicesAsync(IReadOnlyList<NI.PhysicalDeviceIdentity> devices,
        NI.HapticCapabilities? output, CancellationToken cancellationToken)
    {
        return Observer?.PhysicalDevicesChangedAsync(
            Array.AsReadOnly(devices.Select(HandheldConversions.ToPublic).ToArray()),
            output is null ? null : HandheldConversions.ToPublic(output), cancellationToken) ?? ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishControllerSampleAsync(NI.CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        return Observer?.ControllerSampleAsync(HandheldConversions.ToPublic(sample), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishOemControlsAsync(IReadOnlyList<NI.OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        return Observer?.OemControlsChangedAsync(
                   Array.AsReadOnly(controls.Select(HandheldConversions.ToPublic).ToArray()), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishOemEventAsync(NI.OemControlEvent controlEvent, CancellationToken cancellationToken)
    {
        return Observer?.OemEventAsync(HandheldConversions.ToPublic(controlEvent), cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishSettingsManifestAsync(PluginSettingsManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.Settings.Count != 0)
        {
            throw new NotSupportedException("A native preference contract must accompany nonempty preferences.");
        }

        return ValueTask.CompletedTask;
    }

    void N.IDeviceSink.Trace(N.DeviceTraceLevel level, string scope, string message)
    {
        _diagnostics?.Invoke(new DeviceLogEntry((DeviceLogLevel)level, scope, message));
    }

    void N.IDeviceSink.TraceChange(N.DeviceTraceLevel level, string scope, string key, string message)
    {
        _diagnostics?.Invoke(new DeviceLogEntry((DeviceLogLevel)level, scope, message, key));
    }

    void N.IDeviceSink.ReportFault(string scope, string message)
    {
        Observer?.Faulted(scope, message);
    }

    /// <summary>Matches only supplied normalized identity; does not enumerate or open hardware.</summary>
    /// <param name="identity">Normalized machine observations.</param>
    /// <returns>The exact definition, or null when no native implementation matches.</returns>
    public static HandheldDefinition? Detect(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var native = HandheldConversions.ToNative(identity);
        var claw = ClawModels.Find(native);
        if (claw is not null)
        {
            return Definitions.Single(d => d.Id == claw.DefinitionId);
        }

        var ally = AllyModels.Match(native);
        return ally is null ? null : Definitions.Single(d => d.Id == ally.DefinitionId);
    }

    /// <summary>Creates a native engine without acquiring hardware.</summary>
    /// <param name="definition">Exact supported definition returned by detection or Definitions.</param>
    /// <param name="stateDirectory">Existing family-owned recovery state directory.</param>
    /// <param name="diagnostics">Optional transition diagnostics callback, which must not throw.</param>
    /// <returns>A direct native device instance.</returns>
    public static HandheldDevice Create(HandheldDefinition definition, string stateDirectory,
        Action<DeviceLogEntry>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var canonical = Definitions.SingleOrDefault(d => d == definition) ??
                        throw new ArgumentException("Unknown handheld definition.", nameof(definition));
        return new HandheldDevice(canonical, stateDirectory, diagnostics);
    }

    /// <summary>Starts native services inside the supplied cycle; controller acquisition is independently optional.</summary>
    public async ValueTask<DeviceStartResult> StartAsync(long cycleGeneration, bool controllerManagementEnabled,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        Interlocked.Exchange(ref _cycleGeneration, cycleGeneration);
        var result = await _engine.StartAsync(new N.PluginStartContext
        {
            Host = this, CycleGeneration = cycleGeneration, DeviceDefinitionId = Definition.Id,
            StateDirectory = _stateDirectory, ControllerManagementEnabled = controllerManagementEnabled
        }, cancellationToken).ConfigureAwait(false);
        return StartResult(result);
    }

    /// <summary>Quiesces native services without terminating the device cycle.</summary>
    public async ValueTask SuspendAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _engine.SuspendAsync(new N.PluginQuiesceContext(NativeDeadline(deadline)), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Revalidates and reacquires services into a fresh consumer-owned cycle.</summary>
    public async ValueTask<DeviceStartResult> ResumeAsync(long cycleGeneration, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        Interlocked.Exchange(ref _cycleGeneration, cycleGeneration);
        return StartResult(await _engine
            .ResumeAsync(new N.PluginResumeContext(cycleGeneration, NativeDeadline(deadline)), cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Dispatches one typed hardware command, retaining its native outcome and timestamp.</summary>
    public async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        return HandheldConversions.ToPublic(await _engine
            .ExecuteCommandAsync(HandheldConversions.ToNative(command), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Changes physical controller ownership without stopping other services.</summary>
    public async ValueTask SetControllerManagementAsync(bool enabled, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _engine
            .SetControllerManagementAsync(new N.PluginControllerManagementContext(enabled, NativeDeadline(deadline)),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases physical acquisition and writes the documented native baseline, without requiring readback.</summary>
    public async ValueTask ReleaseControllerAsync(HandoffScope scope, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _engine
            .ReleaseControllerAsync(
                new N.PluginControllerReleaseContext((NL.HandoffScope)scope, NativeDeadline(deadline)),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers complete motor state. An explicit zero frame stops output.</summary>
    public async ValueTask ApplyHapticsAsync(HapticOutputFrame frame, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _engine.ApplyHapticOutputAsync(HandheldConversions.ToNative(frame), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns detached bounded native service and recovery facts.</summary>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        var facts = await _engine.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(facts.Values,
            StringComparer.Ordinal));
    }

    /// <summary>Restores and releases all native resources, reporting actual cleanup evidence.</summary>
    public async ValueTask<DeviceStopResult> StopAsync(DeviceStopReason reason, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        var result = await _engine
            .StopAsync(new N.PluginStopContext((N.PluginStopReason)reason, NativeDeadline(deadline)), cancellationToken)
            .ConfigureAwait(false);
        return new DeviceStopResult((DeviceStopStatus)result.Status,
            result.Reason is { } r ? HandheldConversions.ToPublic(r) : null);
    }

    private Operation Enter()
    {
        lock (_operations)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _inFlight++;
            return new Operation(this);
        }
    }

    private void Leave()
    {
        TaskCompletionSource? drained;
        lock (_operations)
        {
            _inFlight--;
            drained = _inFlight == 0 ? _drained : null;
        }

        drained?.TrySetResult();
    }

    private static NL.Deadline NativeDeadline(Deadline deadline)
    {
        return NL.Deadline.After(deadline.Remaining);
    }

    private static DeviceStartResult StartResult(N.PluginStartResult result)
    {
        return new DeviceStartResult((DeviceOperationalState)result.State,
            result.Reason is { } r ? HandheldConversions.ToPublic(r) : null);
    }

    private readonly struct Operation(HandheldDevice owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Leave();
        }
    }
}
