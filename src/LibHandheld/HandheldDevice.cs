using System.Collections.ObjectModel;
using LibHandheld.Contracts;
using LibHandheld.Internal;
using LibHandheld.Internal.Settings;
using N = LibHandheld.Internal.Plugin;
using NC = LibHandheld.Internal.Capabilities;
using NI = LibHandheld.Internal.Input;
using NL = LibHandheld.Internal.Lifecycle;

namespace LibHandheld;

/// <summary>Direct typed access to native handheld services, independent of application policy.</summary>
/// <remarks>
///     Consumers can own cycle generations explicitly or use the convenience lifecycle overloads. Await native completion
///     before disposal; cancellation
///     requests cooperation and does not prove a dispatched write stopped. No physical resource is opened by detection or
///     construction.
/// </remarks>
public sealed partial class HandheldDevice : IAsyncDisposable, N.IDeviceSink
{
    private readonly Action<DeviceLogEntry>? _diagnostics;
    private readonly N.IDeviceEngine _engine;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _operations = new();
    private readonly Func<DeviceIdentitySnapshot> _readIdentity;
    private readonly string _stateDirectory;
    private bool _acceptPublications;
    private long _cycleGeneration;
    private Task? _disposeTask;
    private bool _disposing;
    private TaskCompletionSource? _drained;
    private int _inFlight;
    private bool? _ownsCycleGeneration;
    private bool _startAttempted;
    private bool _started;
    private DeviceStopResult? _stopResult;
    private bool _stopped;
    private bool _suspended;

    private HandheldDevice(HandheldDefinition definition, string stateDirectory, Action<DeviceLogEntry>? diagnostics)
        : this(definition, stateDirectory, diagnostics, DeviceFamilies.Create(definition))
    {
    }

    internal HandheldDevice(HandheldDefinition definition, string stateDirectory, Action<DeviceLogEntry>? diagnostics,
        N.IDeviceEngine engine, Func<DeviceIdentitySnapshot>? readIdentity = null)
    {
        Definition = definition;
        _stateDirectory = stateDirectory;
        _diagnostics = diagnostics;
        _engine = engine;
        _readIdentity = readIdentity ?? ReadCurrentIdentity;
    }

    /// <summary>All currently implemented exact models. Additional families are added through their native implementations.</summary>
    public static IReadOnlyList<HandheldDefinition> Definitions => DeviceFamilies.Models;

    /// <summary>Exact immutable model selected for this instance.</summary>
    public HandheldDefinition Definition { get; }

    /// <summary>Borrowed typed publication observer. The device never disposes this consumer.</summary>
    public IHandheldObserver? Observer { get; set; }

    /// <summary>
    ///     Joins outstanding native calls, restores owned temporary state and disposes resources. Call StopAsync first
    ///     to receive the restoration result; disposal is the final cleanup boundary.
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
            _acceptPublications = false;
            _descriptors = null;
            _states.Clear();
            var pending = _inFlight == 0
                ? Task.CompletedTask
                : (_drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _disposeTask = Task.Run(async () =>
            {
                await pending.ConfigureAwait(false);
                try
                {
                    await _engine.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _commands.Dispose();
                    _lifecycle.Dispose();
                }
            });
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Current consumer-owned native cycle generation.</summary>
    public long CycleGeneration => Interlocked.Read(ref _cycleGeneration);

    ValueTask N.IDeviceSink.PublishDescriptorsAsync(NC.CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        var published = HandheldConversions.ToPublic(descriptors);
        lock (_operations)
        {
            if (!_acceptPublications || published.CycleGeneration != _cycleGeneration || _disposing ||
                (_descriptors is { } current && published.Generation <= current.Generation))
            {
                return ValueTask.CompletedTask;
            }

            _descriptors = published;
            _states.Clear();
        }

        return Observer?.DescriptorsChangedAsync(published, cancellationToken) ??
               ValueTask.CompletedTask;
    }

    ValueTask N.IDeviceSink.PublishCapabilityStateAsync(NC.CapabilityState state, CancellationToken cancellationToken)
    {
        var published = HandheldConversions.ToPublic(state);
        lock (_operations)
        {
            if (!_acceptPublications || _disposing || _descriptors is not { } descriptors ||
                published.CycleGeneration != _cycleGeneration ||
                published.DescriptorGeneration != descriptors.Generation ||
                !descriptors.Descriptors.Any(descriptor => descriptor.CapabilityId == published.CapabilityId &&
                                                           descriptor.InstanceId == published.InstanceId))
            {
                return ValueTask.CompletedTask;
            }

            _states[(published.CapabilityId, published.InstanceId)] = published;
        }

        return Observer?.StateChangedAsync(published, cancellationToken) ??
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
        ClearControls();
        Observer?.Faulted(scope, message);
    }

    /// <summary>Matches only supplied normalized identity; does not enumerate or open hardware.</summary>
    /// <param name="identity">Normalized machine observations.</param>
    /// <returns>The exact definition, or null when no native implementation matches.</returns>
    public static HandheldDefinition? Detect(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return DeviceFamilies.Detect(HandheldConversions.ToNative(identity));
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
        return await StartCoreAsync(cycleGeneration, controllerManagementEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Starts services with a library-owned fresh generation, optionally acquiring controller ownership.</summary>
    /// <remarks>Do not mix this overload with consumer-owned generation overloads on the same instance.</remarks>
    public ValueTask<DeviceStartResult> StartAsync(bool controllerManagementEnabled = true,
        CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(null, controllerManagementEnabled, cancellationToken);
    }

    private async ValueTask<DeviceStartResult> StartCoreAsync(long? cycleGeneration,
        bool controllerManagementEnabled, CancellationToken cancellationToken)
    {
        using var operation = Enter();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_startAttempted || _stopped)
            {
                throw new InvalidOperationException("This device instance has already begun its native lifetime. "
                                                    + "Resume a suspended cycle, or create a new instance after stopping.");
            }

            var detection = await _engine.DetectAsync(new N.PluginDetectionContext
            {
                Identity = HandheldConversions.ToNative(_readIdentity())
            }, cancellationToken).ConfigureAwait(false);
            if (!detection.Matched || detection.DeviceDefinitionId != Definition.Id)
            {
                throw new NotSupportedException("The selected model does not match the current machine identity.");
            }

            var cycle = BeginCycle(cycleGeneration);
            _startAttempted = true;
            var result = await _engine.StartAsync(new N.PluginStartContext
            {
                Host = this, CycleGeneration = cycle, DeviceDefinitionId = Definition.Id,
                StateDirectory = _stateDirectory,
                ControllerManagementEnabled = controllerManagementEnabled && Definition.HasController
            }, cancellationToken).ConfigureAwait(false);
            _started = true;
            return StartResult(result);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Quiesces native services without terminating the device cycle.</summary>
    public async ValueTask SuspendAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await WaitLifecycleAsync(deadline, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started || _stopped)
            {
                throw new InvalidOperationException("Only a running device cycle can be suspended.");
            }

            if (_suspended)
            {
                return;
            }

            await _engine.SuspendAsync(new N.PluginQuiesceContext(NativeDeadline(deadline)), cancellationToken)
                .ConfigureAwait(false);
            _suspended = true;
            ClearControls();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Revalidates and reacquires services into a fresh consumer-owned cycle.</summary>
    public async ValueTask<DeviceStartResult> ResumeAsync(long cycleGeneration, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        return await ResumeCoreAsync(cycleGeneration, deadline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reacquires services with a library-owned fresh generation.</summary>
    /// <remarks>Do not mix this overload with consumer-owned generation overloads on the same instance.</remarks>
    public ValueTask<DeviceStartResult> ResumeAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        return ResumeCoreAsync(null, deadline, cancellationToken);
    }

    private async ValueTask<DeviceStartResult> ResumeCoreAsync(long? cycleGeneration, Deadline deadline,
        CancellationToken cancellationToken)
    {
        using var operation = Enter();
        await WaitLifecycleAsync(deadline, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started || !_suspended || _stopped)
            {
                throw new InvalidOperationException("Only a successfully suspended device cycle can be resumed.");
            }

            var cycle = BeginCycle(cycleGeneration);
            _suspended = false;
            return StartResult(await _engine
                .ResumeAsync(new N.PluginResumeContext(cycle, NativeDeadline(deadline)), cancellationToken)
                .ConfigureAwait(false));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Dispatches one typed hardware command, retaining its native outcome and timestamp.</summary>
    public async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    private async ValueTask<CapabilityCommandResult> ExecuteCoreAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
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
        await WaitLifecycleAsync(deadline, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return _stopResult ?? new DeviceStopResult(DeviceStopStatus.Failed,
                    new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        "The first native stop did not complete."));
            }

            _stopped = true;
            ClearControls();
            var result = await _engine
                .StopAsync(new N.PluginStopContext((N.PluginStopReason)reason, NativeDeadline(deadline)),
                    cancellationToken)
                .ConfigureAwait(false);
            _stopResult = new DeviceStopResult((DeviceStopStatus)result.Status,
                result.Reason is { } r ? HandheldConversions.ToPublic(r) : null);
            return _stopResult;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private long BeginCycle(long? requested)
    {
        lock (_operations)
        {
            var ownsGeneration = requested is null;
            if (_ownsCycleGeneration is { } established && established != ownsGeneration)
            {
                throw new InvalidOperationException("Do not mix library-owned and consumer-owned cycle generations.");
            }

            var cycle = requested ?? checked(_cycleGeneration + 1);
            if (cycle <= _cycleGeneration)
            {
                throw new ArgumentOutOfRangeException(nameof(requested), "A new cycle must have a fresh generation.");
            }

            _ownsCycleGeneration = ownsGeneration;
            _cycleGeneration = cycle;
            _acceptPublications = true;
            _descriptors = null;
            _states.Clear();
            return cycle;
        }
    }

    private async ValueTask WaitLifecycleAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        using var waiting = deadline.CreateCancellationSource(cancellationToken);
        await _lifecycle.WaitAsync(waiting.Token).ConfigureAwait(false);
    }

    private void ClearControls()
    {
        lock (_operations)
        {
            _acceptPublications = false;
            _descriptors = null;
            _states.Clear();
        }
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
