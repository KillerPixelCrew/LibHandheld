using LibHandheld.Contracts;
using LibHandheld.Internal;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld;

/// <summary>Owns one exact handheld's native services and semantic controls.</summary>
public sealed partial class HandheldDevice : IAsyncDisposable, IDeviceSink
{
    private readonly HashSet<(string Id, string? Instance)> _controlIds = [];
    private readonly Action<DeviceLogEntry>? _diagnostics;
    private readonly NativeEngine _engine;
    private readonly DeviceIdentitySnapshot _identity;
    private readonly Lock _operations = new();
    private readonly SemaphoreSlim _owner = new(1, 1);
    private readonly HandheldResources _resources;
    private readonly string _stateDirectory;
    private volatile bool _active;
    private volatile HapticCapabilities? _hapticOutputCapabilities;
    private bool _disposed;
    private bool _started;
    private DeviceStopResult? _stopResult;
    private bool _suspended;

    internal HandheldDevice(HandheldDefinition definition, DeviceIdentitySnapshot identity, string stateDirectory,
        Action<DeviceLogEntry>? diagnostics, NativeEngine engine, HandheldResources? resources = null)
    {
        Definition = definition;
        _identity = identity;
        _stateDirectory = stateDirectory;
        _diagnostics = diagnostics;
        _engine = engine;
        _resources = resources ?? new HandheldResources();
    }

    /// <summary>Every supported exact model.</summary>
    public static IReadOnlyList<HandheldDefinition> Definitions => DeviceFamilies.Models;

    /// <summary>The exact model selected from the supplied identity.</summary>
    public HandheldDefinition Definition { get; }

    /// <summary>Borrowed publication observer, never disposed by the device.</summary>
    public IHandheldObserver? Observer { get; set; }

    /// <summary>Releases handles only. StopAsync owns hardware restoration.</summary>
    public async ValueTask DisposeAsync()
    {
        await _owner.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _active = false;
            await _engine.DisposeAsync().ConfigureAwait(false);
            ClearControls();
            DeviceTrace.Install(null);
            Observer = null;
        }
        finally
        {
            _owner.Release();
        }
    }

    ValueTask IDeviceSink.PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken token)
    {
        if (!_active || _disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_operations)
        {
            _descriptors = descriptors;
            _controlIds.Clear();
            foreach (var item in descriptors.Descriptors)
            {
                _controlIds.Add((item.CapabilityId, item.InstanceId));
            }

            foreach (var key in _states.Keys.Where(key => !_controlIds.Contains(key)).ToArray())
            {
                _states.Remove(key);
            }
        }

        return Observer?.DescriptorsChangedAsync(descriptors, token) ?? ValueTask.CompletedTask;
    }

    ValueTask IDeviceSink.PublishCapabilityStateAsync(CapabilityState state, CancellationToken token)
    {
        if (_disposed || (!_active && state.Available))
        {
            return ValueTask.CompletedTask;
        }

        lock (_operations)
        {
            if (!_controlIds.Contains((state.CapabilityId, state.InstanceId)))
            {
                return ValueTask.CompletedTask;
            }

            _states[(state.CapabilityId, state.InstanceId)] = state;
        }

        return Observer?.StateChangedAsync(state, token) ?? ValueTask.CompletedTask;
    }

    ValueTask IDeviceSink.PublishPhysicalDevicesAsync(IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output, CancellationToken token)
    {
        if (_disposed || (!_active && devices.Count != 0))
        {
            return ValueTask.CompletedTask;
        }

        _hapticOutputCapabilities = output;
        return Observer?.PhysicalDevicesChangedAsync(devices, output, token) ?? ValueTask.CompletedTask;
    }

    ValueTask IDeviceSink.PublishControllerSampleAsync(CanonicalControllerSample sample, CancellationToken token)
    {
        return _active && !_disposed
            ? Observer?.ControllerSampleAsync(sample, token) ?? ValueTask.CompletedTask
            : ValueTask.CompletedTask;
    }

    ValueTask IDeviceSink.PublishOemControlsAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken token)
    {
        return !_disposed && (_active || controls.Count == 0)
            ? Observer?.OemControlsChangedAsync(controls, token) ?? ValueTask.CompletedTask
            : ValueTask.CompletedTask;
    }

    ValueTask IDeviceSink.PublishOemEventAsync(OemControlEvent controlEvent, CancellationToken token)
    {
        // Cleanup releases must reach the consumer after new input admission closes.
        return !_disposed && (_active || controlEvent.Edge == OemControlEdge.Released)
            ? Observer?.OemEventAsync(controlEvent, token) ?? ValueTask.CompletedTask
            : ValueTask.CompletedTask;
    }

    void IDeviceSink.ReportFault(string scope, string message)
    {
        _active = false;
        Observer?.Faulted(scope, message);
    }

    /// <summary>Captures Windows registry identity, processor facts and read-only USB interface observations once.</summary>
    /// <remarks>
    ///     No firmware commands, feature-report probes or privileged drivers are used; absent EC and MCU facts remain
    ///     unknown.
    /// </remarks>
    public static DeviceIdentitySnapshot ReadIdentity()
    {
        return WindowsMachineIdentity.Read();
    }

    /// <summary>Matches supplied observations without opening a transport or reading the machine again.</summary>
    public static HandheldDefinition? Detect(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return DeviceFamilies.Detect(identity);
    }

    /// <summary>Creates services for a matched exact model; acquisition begins at StartAsync.</summary>
    public static HandheldDevice Create(HandheldDefinition definition, DeviceIdentitySnapshot identity,
        string stateDirectory, Action<DeviceLogEntry>? diagnostics = null, HandheldResources? resources = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        if (Detect(identity) != definition)
        {
            throw new ArgumentException("The definition does not match the supplied machine identity.",
                nameof(definition));
        }

        return new HandheldDevice(definition, identity, stateDirectory, diagnostics, DeviceFamilies.Create(definition),
            resources);
    }

    /// <summary>Acquires native services; physical controller ownership is independently optional.</summary>
    public async ValueTask<DeviceStartResult> StartAsync(bool controllerManagementEnabled = true,
        CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("This device has already started.");
            }

            DeviceTrace.Install(_diagnostics);
            _active = true;
            _started = true;
            var result = await _engine.StartAsync(new EngineStartContext
            {
                Host = this, Identity = _identity, Resources = _resources, DeviceDefinitionId = Definition.Id,
                StateDirectory = _stateDirectory,
                Journal = await DeviceRecoveryJournal.OpenAsync(_stateDirectory, cancellationToken)
                    .ConfigureAwait(false),
                ControllerManagementEnabled = controllerManagementEnabled && Definition.HasController
            }, cancellationToken).ConfigureAwait(false);
            return new DeviceStartResult((DeviceOperationalState)result.State, result.Reason);
        }
        catch
        {
            _active = false;
            throw;
        }
        finally
        {
            _owner.Release();
        }
    }

    /// <summary>Quiesces native services for suspend.</summary>
    public async ValueTask SuspendAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_active || _suspended)
            {
                return;
            }

            _active = false;
            await _engine.SuspendAsync(new EngineQuiesceContext(deadline), cancellationToken).ConfigureAwait(false);
            _suspended = true;
            ClearControls();
        }
        finally
        {
            _owner.Release();
        }
    }

    /// <summary>Reacquires services using the original exact identity snapshot.</summary>
    public async ValueTask<DeviceStartResult> ResumeAsync(Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_suspended)
            {
                throw new InvalidOperationException("Only a suspended device can resume.");
            }

            _active = true;
            var result = await _engine.ResumeAsync(new EngineResumeContext(deadline), cancellationToken)
                .ConfigureAwait(false);
            _suspended = false;
            return new DeviceStartResult((DeviceOperationalState)result.State, result.Reason);
        }
        catch
        {
            _active = false;
            throw;
        }
        finally
        {
            _owner.Release();
        }
    }

    /// <summary>Executes a typed vendor control without an application-specific adapter.</summary>
    public async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "Cancelled before native dispatch.");
        }

        try
        {
            return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _owner.Release();
        }
    }

    private ValueTask<CapabilityCommandResult> ExecuteCoreAsync(CapabilityCommand command, CancellationToken token)
    {
        if (!_active || _disposed || token.IsCancellationRequested)
        {
            return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "The device is inactive or the request was cancelled before dispatch."));
        }

        return _engine.ExecuteCommandAsync(command, token);
    }

    /// <summary>Changes physical input ownership while other hardware services continue.</summary>
    public async ValueTask SetControllerManagementAsync(bool enabled, Deadline deadline,
        CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active)
            {
                await _engine.SetControllerManagementAsync(new EngineControllerManagementContext(enabled, deadline),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _owner.Release();
        }
    }

    /// <summary>Releases physical input and restores the native controller baseline.</summary>
    public ValueTask ReleaseControllerAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        return SetControllerManagementAsync(false, deadline, cancellationToken);
    }

    /// <summary>Delivers complete motor state; zero requests an explicit stop.</summary>
    public ValueTask ApplyHapticsAsync(HapticOutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (!_active || _disposed)
        {
            return ValueTask.CompletedTask;
        }

        if (frame.NativePulse is { } pulse)
        {
            if (_hapticOutputCapabilities?.NativePulse != OutputChannelSupport.Native)
            {
                throw new NotSupportedException("This handheld does not support native actuator pulses.");
            }

            if (pulse.Side is not (HapticActuatorSide.Left or HapticActuatorSide.Right)
                || pulse.Style is not (HapticPulseStyle.Disabled or HapticPulseStyle.Weak or HapticPulseStyle.Strong))
            {
                throw new ArgumentOutOfRangeException(nameof(frame));
            }
        }

        return _engine.ApplyHapticOutputAsync(frame, cancellationToken);
    }

    /// <summary>Delivers a native button pulse without replacing ongoing virtual-controller rumble.</summary>
    /// <remarks>Call only when the published output capabilities declare native pulse support.</remarks>
    public ValueTask ApplyButtonHapticsAsync(CanonicalButtons button, ButtonHapticStrength strength, bool released,
        CancellationToken cancellationToken = default)
    {
        return ApplyHapticsAsync(new HapticOutputFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            NativePulse = NativeHapticPulse.ForButton(button, strength, released)
        }, cancellationToken);
    }

    /// <summary>Returns current native diagnostics.</summary>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _engine.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _owner.Release();
        }
    }

    /// <summary>Restores captured originals and releases native ownership.</summary>
    public async ValueTask<DeviceStopResult> StopAsync(Deadline deadline, CancellationToken cancellationToken = default)
    {
        await _owner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopResult is not null)
            {
                return _stopResult;
            }

            _active = false;
            var result = await _engine.StopAsync(new EngineStopContext(deadline), cancellationToken)
                .ConfigureAwait(false);
            _stopResult = new DeviceStopResult((DeviceStopStatus)result.Status, result.Reason);
            ClearControls();
            return _stopResult;
        }
        finally
        {
            _owner.Release();
        }
    }

    private void ClearControls()
    {
        lock (_operations)
        {
            _descriptors = null;
            _states.Clear();
            _controlIds.Clear();
        }
    }
}
