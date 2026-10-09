using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Devices;

/// <summary>A model profile selects codecs; this engine owns the common lifecycle and semantic operations.</summary>
internal sealed partial class ProfiledDeviceEngine : IDeviceEngine
{
    private readonly DeviceCommandSerializer _commands;
    private readonly Lock _inputGate = new();
    private readonly Dictionary<string, CanonicalButtons> _oemHeld = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _oemPressIds = new(StringComparer.Ordinal);
    private readonly Func<EcFanProfile, IEmbeddedController> _openEc;
    private readonly Func<ICpuPowerTransport> _openPower;
    private readonly ProfiledDeviceModel _profile;
    private readonly List<DeviceService<ProfiledCycleIdentity>> _services = [];
    private CanonicalButtons _buttons;
    private ProfiledControllerService? _controller;
    private long _cycle;
    private ProfiledCycleIdentity _cycleIdentity = new("unknown");
    private ProfiledFanService? _fan;
    private long _generation;
    private DeviceIdentitySnapshot? _identity;
    private ProfiledRecoveryJournal? _journal;
    private bool _manageController;
    private StandardMotionSource? _motion;
    private ProfiledFeatureService? _native;
    private long _nativeDescriptorGeneration;
    private long _oemSequence;
    private ProfiledPowerService? _power;
    private volatile bool _quiescing = true;
    private IDeviceSink? _sink;
    private CapabilityDescriptorSet? _surface;
    private StandardControllerTouchpad? _touchpad;

    internal ProfiledDeviceEngine(ProfiledDeviceModel profile)
        : this(profile, () => profile.Cpu == CpuPowerKind.Amd ? AmdSmuTransport.Open() : IntelPowerTransport.Open(),
            fan => PawnIoEmbeddedController.OpenIndexed(fan.IndexPort, fan.DataPort))
    {
    }

    internal ProfiledDeviceEngine(ProfiledDeviceModel profile, Func<ICpuPowerTransport> openPower,
        Func<EcFanProfile, IEmbeddedController> openEc)
    {
        _profile = profile;
        _openPower = openPower;
        _openEc = openEc;
        _commands = new DeviceCommandSerializer(profile.Definition.Name, () => _quiescing ? null : _surface,
            RefreshAsync, PublishAsync);
    }

    public string PackageId => _profile.Definition.FamilyId;

    public ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var matched = _profile.Matches(context.Identity);
        _identity = matched ? context.Identity : null;
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = matched,
            DeviceDefinitionId = matched ? _profile.Definition.Id : null
        });
    }

    public ValueTask<PluginStartResult> StartAsync(PluginStartContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            if (_identity is null || context.DeviceDefinitionId != _profile.Definition.Id)
            {
                throw new InvalidOperationException(
                    "An exact device identity must be selected before native acquisition.");
            }

            _sink = context.Host;
            _cycle = context.CycleGeneration;
            _manageController = context.ControllerManagementEnabled;
            lock (_inputGate)
            {
                _buttons = CanonicalButtons.None;
                _oemHeld.Clear();
            }

            _cycleIdentity = new ProfiledCycleIdentity(string.Join("|", _profile.Definition.Id,
                _identity.BiosVersion ?? "unknown-bios", _identity.EcFirmwareVersion ?? "unknown-ec"));
            _journal = await ProfiledRecoveryJournal.Open(context.StateDirectory, token).ConfigureAwait(false);
            _power = new ProfiledPowerService(_openPower, _journal);
            _services.Add(_power);
            if (_profile.Fan is { } fan)
            {
                _fan = new ProfiledFanService(fan, () => _openEc(fan), _journal);
                _services.Add(_fan);
            }

            if (_profile.CreateNativeService is { } native)
            {
                _native = native(
                    new ProfiledFeatureContext(context.StateDirectory, _cycleIdentity.Firmware, context.Host)
                        { ControllerManagementEnabled = _manageController });
                _services.Add(_native);
            }

            if (_profile.Motion is { } motion)
            {
                _motion = new StandardMotionSource(motion);
                _services.Add(new ProfiledMotionService(_motion));
            }

            if (_profile.Touchpad is { } touch)
            {
                _touchpad = new StandardControllerTouchpad(touch);
                _services.Add(new ProfiledTouchService(_touchpad));
            }

            if (_profile.KeyboardChords.Count != 0 || _profile.CreateOemSource is not null)
            {
                _services.Add(new ProfiledOemService(_profile, OnOem, () => _manageController));
            }

            if (_profile.Controller is { } controller)
            {
                _controller = new ProfiledControllerService(new StandardControllerSource(controller), context.Host,
                    () =>
                    {
                        lock (_inputGate)
                        {
                            return _buttons;
                        }
                    }, () => _motion?.Latest, () => _touchpad?.Latest ?? default);
                _services.Add(_controller);
            }

            try
            {
                await DeviceServiceLifecycle.AcquireAllAsync(WantedServices(), Context(Deadline.Never), token)
                    .ConfigureAwait(false);
                _quiescing = false;
                BuildSurface();
                await context.Host.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
                await PublishOemDescriptorsAsync(token).ConfigureAwait(false);
                await PublishAsync(token).ConfigureAwait(false);
                _commands.StartObservation();
                return Result();
            }
            catch
            {
                _quiescing = true;
                await DeviceServiceLifecycle
                    .RollBackStartAsync(_services, Context(Deadline.After(TimeSpan.FromSeconds(5))), _sink, _surface)
                    .ConfigureAwait(false);
                _services.Clear();
                throw;
            }
        }, token);
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        return _commands.ExecuteAsync(command, async cancellation =>
        {
            var descriptor = _surface?.Descriptors.SingleOrDefault(item =>
                item.CapabilityId == command.CapabilityId && item.InstanceId == command.InstanceId);
            if (descriptor is null || (!descriptor.SupportsWrite && !descriptor.SupportsAction))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "This model has no writable capability with that identity.");
            }

            if (command.ExpectedCycleGeneration != _cycle ||
                command.ExpectedDescriptorGeneration != _surface!.Generation)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.GenerationChanged,
                    "The device generation changed.");
            }

            if (!DeviceWriteBudget.IsAvailable(command.Deadline) || cancellation.IsCancellationRequested)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                    "The device command has expired.");
            }

            var value = command.RequestedValue;
            if (value is null
                    ? !descriptor.SupportsAction
                    : !descriptor.SupportsWrite || !CapabilityValueValidation.ValueMatches(value, descriptor, out _))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    "The value does not fit the selected model's capability.");
            }

            if (_native is not null && _native.Descriptors.Any(item =>
                    item.CapabilityId == command.CapabilityId && item.InstanceId == command.InstanceId))
            {
                return await _native.ExecuteAsync(command, _cycleIdentity.Firmware, cancellation).ConfigureAwait(false);
            }

            if (value is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "This action is handled by the physical output stream.");
            }

            var dispatched = false;
            try
            {
                switch (descriptor.Role)
                {
                    case CapabilityRole.PowerSustainedLimit:
                    case CapabilityRole.PowerSlowLimit:
                        if (_power?.State != DeviceServiceState.Owned)
                        {
                            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                                "CPU power access is unavailable.");
                        }

                        if (!DevicePowerPair.TryResolve(command, _surface.Descriptors, out var sustained, out var boost,
                                out var error))
                        {
                            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                        }

                        await _power.WriteAsync(new CpuPowerLimits(sustained, boost, boost), _cycleIdentity.Firmware,
                            cancellation,
                            () => dispatched = true).ConfigureAwait(false);
                        break;
                    case CapabilityRole.FanMode:
                    case CapabilityRole.FanDuty:
                        if (_fan?.State != DeviceServiceState.Owned)
                        {
                            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                                "The model's EC fan access is unavailable.");
                        }

                        await _fan.WriteAsync(descriptor.Role == CapabilityRole.FanMode ? value.ChoiceValue! : "custom",
                            descriptor.Role == CapabilityRole.FanDuty ? value.IntegerValue : null,
                            _cycleIdentity.Firmware, cancellation,
                            () => dispatched = true).ConfigureAwait(false);
                        break;
                    default:
                        return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                            "The capability is observed through its native service.");
                }

                return CommandResults.Unverified(command);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return dispatched
                    ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                        exception.Message, RollbackResult.NotRequired)
                    : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
            }
        }, token);
    }

    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken token)
    {
        _quiescing = true;
        _commands.StopObservation();
        return _commands.RunAsync(async () =>
        {
            await DeviceServiceLifecycle.SuspendAllAsync(_services, Context(context.Deadline), token)
                .ConfigureAwait(false);
            lock (_inputGate)
            {
                _buttons = CanonicalButtons.None;
                _oemHeld.Clear();
            }
        }, token);
    }

    public ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            _cycle = context.CycleGeneration;
            await DeviceServiceLifecycle.AcquireAllAsync(WantedServices(), Context(context.Deadline), token)
                .ConfigureAwait(false);
            _quiescing = false;
            BuildSurface();
            await _sink!.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
            await PublishOemDescriptorsAsync(token).ConfigureAwait(false);
            await PublishAsync(token).ConfigureAwait(false);
            _commands.StartObservation();
            return Result();
        }, token);
    }

    public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken token)
    {
        return SetControllerManagementAsync(new PluginControllerManagementContext(false, context.Deadline), token);
    }

    public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            if (_manageController == context.Enabled)
            {
                return;
            }

            foreach (var service in _services.Where(service => service.ServiceId is "controller" or "oem" or "touchpad")
                         .Reverse())
            {
                await DeviceServiceLifecycle.ReleaseAsync(service, Context(context.Deadline), token)
                    .ConfigureAwait(false);
            }

            if (_services.Any(service =>
                    (service.ServiceId is "controller" or "oem" or "touchpad") &&
                    service.State != DeviceServiceState.Idle))
            {
                throw new InvalidOperationException(
                    "Physical input ownership has not been released; a second reader cannot be acquired.");
            }

            _manageController = context.Enabled;
            lock (_inputGate)
            {
                _buttons = CanonicalButtons.None;
                _oemHeld.Clear();
            }

            if (_native is not null)
            {
                await _native.SetControllerManagementAsync(context.Enabled, token).ConfigureAwait(false);
            }

            foreach (var service in WantedServices()
                         .Where(service => service.ServiceId is "controller" or "oem" or "touchpad"))
            {
                await DeviceServiceLifecycle.AcquireAsync(service, Context(context.Deadline), token)
                    .ConfigureAwait(false);
            }

            await PublishOemDescriptorsAsync(token).ConfigureAwait(false);
            await PublishAsync(token).ConfigureAwait(false);
        }, token);
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_quiescing && _manageController)
        {
            _controller?.Rumble(frame);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = _services.ToDictionary(service => service.ServiceId,
                service => service.State.ToString(), StringComparer.Ordinal)
        });
    }

    public ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken token)
    {
        _quiescing = true;
        _commands.StopObservation();
        return _commands.RunAsync(async () =>
        {
            await DeviceServiceLifecycle.ReleaseAllAsync(_services, Context(context.Deadline), token)
                .ConfigureAwait(false);
            lock (_inputGate)
            {
                _buttons = CanonicalButtons.None;
                _oemHeld.Clear();
            }

            var failed = _services.Any(service => service.State == DeviceServiceState.Faulted);
            var unverified = _services.Any(service => service.State == DeviceServiceState.ReleasedUnverified);
            unverified |= _journal?.OutstandingEntries.Count > 0;
            if (_sink is not null)
            {
                await _sink.PublishOemControlsAsync([], CancellationToken.None).ConfigureAwait(false);
            }

            if (!failed && !unverified)
            {
                _services.Clear();
            }

            _surface = null;
            return new PluginStopResult
            {
                Status = failed ? PluginStopStatus.Failed :
                    unverified ? PluginStopStatus.Unverified : PluginStopStatus.Clean
            };
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never), CancellationToken.None)
            .ConfigureAwait(false);
        _commands.Dispose();
    }

    private DeviceCycleContext<ProfiledCycleIdentity> Context(Deadline deadline)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(_cycle, deadline, _cycleIdentity);
    }

    private IEnumerable<DeviceService<ProfiledCycleIdentity>> WantedServices()
    {
        return _services.Where(IsWanted);
    }

    private bool IsWanted(DeviceServiceStatus service)
    {
        return _manageController || (service.ServiceId switch
        {
            "controller" or "touchpad" => false,
            "oem" => _profile.KeyboardChords.Any(chord => !chord.RequiresController) ||
                     !_profile.NativeOemRequiresController,
            _ => true
        });
    }

    private PluginStartResult Result()
    {
        return DeviceServiceLifecycle.StartResult(_services,
            IsWanted, "No model service could be acquired.", _sink);
    }

    private void OnOem(string id, CanonicalButtons button, bool down)
    {
        string pressId;
        lock (_inputGate)
        {
            if (down)
            {
                _oemHeld[id] = button;
            }
            else
            {
                _oemHeld.Remove(id);
            }

            _buttons = CanonicalButtons.None;
            foreach (var held in _oemHeld.Values)
            {
                _buttons |= held;
            }

            if (down || !_oemPressIds.TryGetValue(id, out pressId!))
            {
                pressId = $"{_cycle}:{id}:{++_oemSequence}";
                _oemPressIds[id] = pressId;
            }
        }

        if (_sink is { } sink && !_quiescing)
        {
            sink.PublishOemEventAsync(new OemControlEvent(id, OemPressKind.Short, DateTimeOffset.UtcNow,
                    pressId, down ? OemControlEdge.Pressed : OemControlEdge.Released), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private async ValueTask RefreshAsync(string? id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _power?.Refresh();
        _fan?.Refresh();
        if (_native is not null)
        {
            await _native.RefreshAsync(token).ConfigureAwait(false);
        }

        if (_native is not null && _nativeDescriptorGeneration != _native.DescriptorGeneration)
        {
            BuildSurface();
            await _sink!.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
        }

        foreach (var service in WantedServices().Where(service =>
                     service is ProfiledTouchService { NeedsReconnect: true }
                         or ProfiledOemService { NeedsReconnect: true }).ToArray())
        {
            await DeviceServiceLifecycle.ReleaseAsync(service, Context(Deadline.After(TimeSpan.FromSeconds(2))), token)
                .ConfigureAwait(false);
            if (service.State == DeviceServiceState.Idle)
            {
                await DeviceServiceLifecycle
                    .AcquireAsync(service, Context(Deadline.After(TimeSpan.FromSeconds(2))), token)
                    .ConfigureAwait(false);
            }
        }
    }

    private ValueTask PublishOemDescriptorsAsync(CancellationToken token)
    {
        return _sink!.PublishOemControlsAsync(
            Array.AsReadOnly(_profile.OemControls
                .Where(control => _manageController || !control.RequiresControllerAcquisition)
                .Concat(_profile.KeyboardChords.Where(chord => _manageController || !chord.RequiresController)
                    .GroupBy(chord => chord.Id, StringComparer.Ordinal).Select(group => group.First()).Select(chord =>
                        new OemControlDescriptor
                        {
                            ControlId = chord.Id,
                            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = chord.Label },
                            Placement = chord.Button is CanonicalButtons.RearPaddle1 or CanonicalButtons.RearPaddle2
                                or CanonicalButtons.RearPaddle3 or CanonicalButtons.RearPaddle4
                                ? OemControlPlacement.Rear
                                : OemControlPlacement.Front,
                            RequiresControllerAcquisition = chord.RequiresController
                        })).GroupBy(control => control.ControlId, StringComparer.Ordinal).Select(group => group.First())
                .ToArray()), token);
    }
}
