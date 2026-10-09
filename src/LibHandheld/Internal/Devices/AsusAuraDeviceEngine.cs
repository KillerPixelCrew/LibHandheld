using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Devices;

/// <summary>Adds actual ASUS USB lighting accessories without replacing the machine's own backend.</summary>
internal sealed class AsusAuraDeviceEngine(
    IDeviceEngine device,
    Func<ProfiledFeatureContext, ProfiledFeatureService>? accessoryFactory = null) : IDeviceEngine, IDeviceSink
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Dictionary<(string, string?), CapabilityState> _deviceStates = [];
    private readonly SemaphoreSlim _publications = new(1, 1);
    private ProfiledFeatureService? _accessory;
    private long _accessoryGeneration;
    private volatile bool _active;
    private CapabilityDescriptorSet? _deviceSurface;
    private long _generation;
    private IDeviceSink? _host;
    private ProfiledCycleIdentity _identity = new("unknown");
    private Task? _observation;
    private CancellationTokenSource? _observationCancellation;
    private CapabilityDescriptorSet? _surface;

    public string PackageId => device.PackageId;

    public async ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context, CancellationToken token)
    {
        var result = await device.DetectAsync(context, token).ConfigureAwait(false);
        _identity = new ProfiledCycleIdentity(string.Join("|", context.Identity.BiosVersion ?? "unknown-bios",
            context.Identity.EcFirmwareVersion ?? "unknown-ec"));
        return result;
    }

    public async ValueTask<PluginStartResult> StartAsync(PluginStartContext context, CancellationToken token)
    {
        _host = context.Host;
        var accessoryContext = new ProfiledFeatureContext(
            Path.Combine(context.StateDirectory, "usb-accessories"), _identity.Firmware, this);
        _accessory = accessoryFactory?.Invoke(accessoryContext)
                     ?? new GenericAsusService(accessoryContext, firmwareControls: false);
        try
        {
            var result = await device.StartAsync(context with { Host = this }, token).ConfigureAwait(false);
            await DeviceServiceLifecycle.AcquireAsync(_accessory, Context(Deadline.Never), token).ConfigureAwait(false);
            _active = true;
            await PublishMergedAsync(token).ConfigureAwait(false);
            StartObservation();
            return Combine(result);
        }
        catch
        {
            await StopAsync(new PluginStopContext(PluginStopReason.StartFailed,
                Deadline.After(TimeSpan.FromSeconds(5))), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken token)
    {
        using var budget = command.Deadline.CreateCancellationSource(token);
        await _commands.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            CapabilityDescriptor? descriptor;
            long deviceGeneration;
            await _publications.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                var surface = _surface;
                if (!_active || surface is null || command.ExpectedCycleGeneration != CycleGeneration
                    || command.ExpectedDescriptorGeneration != surface.Generation)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.GenerationChanged,
                        "The device or accessory generation changed.");
                }

                descriptor = surface.Descriptors.SingleOrDefault(item => item.CapabilityId == command.CapabilityId
                                                                         && item.InstanceId == command.InstanceId);
                deviceGeneration = _deviceSurface!.Generation;
            }
            finally
            {
                _publications.Release();
            }

            if (descriptor is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "The control is no longer declared.");
            }

            if (_accessory?.Descriptors.Any(item => item.CapabilityId == descriptor.CapabilityId
                                                    && item.InstanceId == descriptor.InstanceId) == true)
            {
                if (command.RequestedValue is { } value
                        ? !descriptor.SupportsWrite || !CapabilityValueValidation.ValueMatches(value, descriptor, out _)
                        : !descriptor.SupportsAction)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                        "The value does not fit this accessory control.");
                }

                var result = await _accessory.ExecuteAsync(command, _identity.Firmware, budget.Token)
                    .ConfigureAwait(false);
                await PublishAccessoryStatesAsync(CancellationToken.None).ConfigureAwait(false);
                return result;
            }

            return await device.ExecuteCommandAsync(command with
            {
                ExpectedDescriptorGeneration = deviceGeneration
            }, budget.Token).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken token)
    {
        _active = false;
        await StopObservationAsync().ConfigureAwait(false);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try
            {
                if (_accessory is not null)
                {
                    await DeviceServiceLifecycle.SuspendAsync(_accessory, Context(context.Deadline), token)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await device.SuspendAsync(context, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _commands.Release();
        }
    }

    public async ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context, CancellationToken token)
    {
        try
        {
            var result = await device.ResumeAsync(context, token).ConfigureAwait(false);
            if (_accessory is not null)
            {
                await DeviceServiceLifecycle.AcquireAsync(_accessory, Context(context.Deadline), token)
                    .ConfigureAwait(false);
            }

            _active = true;
            await PublishMergedAsync(token).ConfigureAwait(false);
            StartObservation();
            return Combine(result);
        }
        catch
        {
            await StopAsync(new PluginStopContext(PluginStopReason.RuntimeFault,
                Deadline.After(TimeSpan.FromSeconds(5))), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken token)
    {
        _active = false;
        await StopObservationAsync().ConfigureAwait(false);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PluginStopResult result;
            try
            {
                result = await device.StopAsync(context, token).ConfigureAwait(false);
            }
            finally
            {
                if (_accessory is not null)
                {
                    await DeviceServiceLifecycle
                        .ReleaseAsync(_accessory, Context(context.Deadline), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            if (_accessory?.State == DeviceServiceState.Faulted)
            {
                return new PluginStopResult { Status = PluginStopStatus.Failed, Reason = _accessory.Reason };
            }

            if (result.Status == PluginStopStatus.Clean && _accessory?.State == DeviceServiceState.ReleasedUnverified)
            {
                return new PluginStopResult { Status = PluginStopStatus.Unverified, Reason = _accessory.Reason };
            }

            return result;
        }
        finally
        {
            _commands.Release();
        }
    }

    public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken token)
    {
        return device.ReleaseControllerAsync(context, token);
    }

    public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context, CancellationToken token)
    {
        return device.SetControllerManagementAsync(context, token);
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return device.ApplyHapticOutputAsync(frame, token);
    }

    public ValueTask ApplySettingsAsync(IReadOnlyList<DeviceSettingValue> values, CancellationToken token)
    {
        return device.ApplySettingsAsync(values, token);
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken token)
    {
        return device.GetDiagnosticsAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await device.DisposeAsync().ConfigureAwait(false);
            _commands.Dispose();
            _publications.Dispose();
        }
    }

    public long CycleGeneration => _host?.CycleGeneration ?? 0;

    public async ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken token)
    {
        await _publications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _deviceSurface = descriptors;
            _deviceStates.Clear();
            await PublishMergedUnderGateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _publications.Release();
        }
    }

    public async ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken token)
    {
        await _publications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (state.CycleGeneration != CycleGeneration || state.DescriptorGeneration != _deviceSurface?.Generation)
            {
                return;
            }

            _deviceStates[(state.CapabilityId, state.InstanceId)] = state;
            if (_surface is { } surface)
            {
                await _host!
                    .PublishCapabilityStateAsync(state with { DescriptorGeneration = surface.Generation }, token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _publications.Release();
        }
    }

    public ValueTask PublishPhysicalDevicesAsync(IReadOnlyList<PhysicalDeviceIdentity> identities,
        HapticCapabilities? output, CancellationToken token)
    {
        return _host!.PublishPhysicalDevicesAsync(identities, output, token);
    }

    public ValueTask PublishControllerSampleAsync(CanonicalControllerSample sample, CancellationToken token)
    {
        return _host!.PublishControllerSampleAsync(sample, token);
    }

    public ValueTask PublishOemControlsAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken token)
    {
        return _host!.PublishOemControlsAsync(controls, token);
    }

    public ValueTask PublishOemEventAsync(OemControlEvent control, CancellationToken token)
    {
        return _host!.PublishOemEventAsync(control, token);
    }

    public ValueTask PublishSettingsManifestAsync(PluginSettingsManifest manifest, CancellationToken token)
    {
        return _host!.PublishSettingsManifestAsync(manifest, token);
    }

    public void Trace(DeviceTraceLevel level, string scope, string message)
    {
        _host?.Trace(level, scope, message);
    }

    public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
    {
        _host?.TraceChange(level, scope, key, message);
    }

    public void ReportFault(string scope, string message)
    {
        _host?.ReportFault(scope, message);
    }

    private async ValueTask PublishMergedAsync(CancellationToken token)
    {
        await _publications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PublishMergedUnderGateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _publications.Release();
        }
    }

    private async ValueTask PublishMergedUnderGateAsync(CancellationToken token)
    {
        if (_deviceSurface is null || _host is null)
        {
            return;
        }

        var descriptors = _deviceSurface.Descriptors.Concat(_accessory?.Descriptors ?? []).ToArray();
        if (descriptors.GroupBy(item => (item.CapabilityId, item.InstanceId)).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("The machine and accessory declared the same control identity.");
        }

        _surface = new CapabilityDescriptorSet
        {
            CycleGeneration = CycleGeneration, Generation = ++_generation,
            Descriptors = Array.AsReadOnly(descriptors)
        };
        _accessoryGeneration = _accessory?.DescriptorGeneration ?? 0;
        await _host.PublishDescriptorsAsync(_surface, token).ConfigureAwait(false);
        foreach (var state in _deviceStates.Values)
        {
            await _host.PublishCapabilityStateAsync(state with
            {
                DescriptorGeneration = _surface.Generation,
                CycleGeneration = CycleGeneration
            }, token).ConfigureAwait(false);
        }

        await PublishAccessoryStatesUnderGateAsync(token).ConfigureAwait(false);
    }

    private async ValueTask PublishAccessoryStatesAsync(CancellationToken token)
    {
        await _publications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PublishAccessoryStatesUnderGateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _publications.Release();
        }
    }

    private async ValueTask PublishAccessoryStatesUnderGateAsync(CancellationToken token)
    {
        if (_surface is null || _accessory is null)
        {
            return;
        }

        foreach (var state in _accessory.States)
        {
            await _host!.PublishCapabilityStateAsync(state with
            {
                DescriptorGeneration = _surface.Generation,
                CycleGeneration = CycleGeneration
            }, token).ConfigureAwait(false);
        }
    }

    private void StartObservation()
    {
        _observationCancellation = new CancellationTokenSource();
        var token = _observationCancellation.Token;
        _observation = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await _commands.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!_active || _accessory is null)
                        {
                            continue;
                        }

                        await _accessory.RefreshAsync(token).ConfigureAwait(false);
                        if (_accessoryGeneration != _accessory.DescriptorGeneration)
                        {
                            await PublishMergedAsync(token).ConfigureAwait(false);
                        }
                        else
                        {
                            await PublishAccessoryStatesAsync(token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException &&
                                                      !token.IsCancellationRequested)
                    {
                        _host?.TraceChange(DeviceTraceLevel.Warn, "asus-usb", "observation", exception.Message);
                    }
                    finally
                    {
                        _commands.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
    }

    private async ValueTask StopObservationAsync()
    {
        if (_observationCancellation is not { } cancellation)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (_observation is { } observation)
        {
            await observation.ConfigureAwait(false);
        }

        cancellation.Dispose();
        _observationCancellation = null;
        _observation = null;
    }

    private PluginStartResult Combine(PluginStartResult result)
    {
        if (_accessory is not { Descriptors.Count: > 0 })
        {
            return result;
        }

        if (_accessory.State is DeviceServiceState.Faulted or DeviceServiceState.Degraded
            || (result.State == PluginOperationalState.Passive && _accessory.State == DeviceServiceState.Owned))
        {
            return new PluginStartResult
            {
                State = PluginOperationalState.Degraded,
                Reason = _accessory.Reason ?? result.Reason
            };
        }

        return result;
    }

    private DeviceCycleContext<ProfiledCycleIdentity> Context(Deadline deadline)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(CycleGeneration, deadline, _identity);
    }
}
