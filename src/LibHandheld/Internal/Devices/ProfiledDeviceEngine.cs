using System.Collections.Concurrent;
using LibHandheld.Contracts;
using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Devices;

/// <summary>A model profile selects codecs; this engine owns the common lifecycle and semantic operations.</summary>
internal sealed partial class ProfiledDeviceEngine : NativeEngine
{
    private readonly ConcurrentDictionary<string, bool> _accessoryChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HidCollection> _accessoryCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceCommandSerializer _commands;
    private readonly Lock _inputGate = new();
    private readonly List<ProfiledFeatureService> _nativeServices = [];
    private readonly Dictionary<string, CanonicalButtons> _oemHeld = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _oemPressIds = new(StringComparer.Ordinal);
    private readonly Func<EcFanProfile, IEmbeddedController> _openEc;
    private readonly Func<ICpuPowerTransport>? _openPower;
    private readonly ProfiledDeviceModel _profile;
    private readonly List<DeviceService<ProfiledCycleIdentity>> _services = [];
    private HidInterfaceNotifications? _accessoryNotifications;
    private CanonicalButtons _buttons;
    private ProfiledControllerService? _controller;
    private ProfiledOemService? _oem;
    private ProfiledCycleIdentity _cycleIdentity = new("unknown");
    private ProfiledFanService? _fan;
    private ProfiledFeatureContext? _featureContext;
    private DeviceIdentitySnapshot? _identity;
    private DeviceRecoveryJournal? _journal;
    private bool _manageController;
    private StandardMotionSource? _motion;
    private Task _oemPublication = Task.CompletedTask;
    private long _oemSequence;
    private ProfiledPowerService? _power;
    private volatile bool _quiescing = true;
    private IDeviceSink? _sink;
    private CapabilityDescriptorSet? _surface;
    private StandardControllerTouchpad? _touchpad;

    internal ProfiledDeviceEngine(ProfiledDeviceModel profile)
        : this(profile, null,
            fan => PawnIoEmbeddedController.OpenIndexed(fan.IndexPort, fan.DataPort))
    {
    }

    internal ProfiledDeviceEngine(ProfiledDeviceModel profile, Func<ICpuPowerTransport>? openPower,
        Func<EcFanProfile, IEmbeddedController> openEc)
    {
        _profile = profile;
        _openPower = openPower;
        _openEc = openEc;
        _commands = new DeviceCommandSerializer(profile.Definition.Name, () => _quiescing ? null : _surface,
            RefreshAsync, PublishAsync, RefreshTelemetryAsync);
    }


    public ValueTask<EngineStartResult> StartAsync(EngineStartContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            _identity = context.Identity;
            if (context.DeviceDefinitionId != _profile.Definition.Id)
            {
                throw new InvalidOperationException(
                    "An exact device identity must be selected before native acquisition.");
            }

            _sink = context.Host;
            _services.Clear();

            _manageController = context.ControllerManagementEnabled;
            lock (_inputGate)
            {
                _buttons = CanonicalButtons.None;
                _oemHeld.Clear();
            }

            _cycleIdentity = new ProfiledCycleIdentity(string.Join("|", _profile.Definition.Id,
                _identity.BiosVersion ?? "unknown-bios", _identity.EcFirmwareVersion ?? "unknown-ec"));
            _journal = context.Journal;
            await _journal.BeginCycleAsync(token).ConfigureAwait(false);
            var featureContext =
                new ProfiledFeatureContext(context.StateDirectory, _cycleIdentity.Firmware, context.Host)
                {
                    Journal = context.Journal, Resources = context.Resources,
                    ControllerManagementEnabled = _manageController, Identity = context.Identity,
                    DeviceDefinitionId = context.DeviceDefinitionId
                };
            _featureContext = featureContext;
            try
            {
                _accessoryNotifications = new HidInterfaceNotifications(AsusAuraProtocol.Vendor,
                    AsusAuraTransport.Products, (path, arrived) =>
                    {
                        _accessoryChanges[path] = arrived;
                        _commands.RequestObservation();
                    });
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn("asus-accessories", exception.Message);
            }

            if (_profile.UseStandardServices)
            {
                _power = new ProfiledPowerService(() => OpenPower(featureContext), _journal, _profile,
                    featureContext.Resources.IntelKxPath);
                _services.Add(_power);
                if (_profile.Fan is { } fan)
                {
                    _fan = new ProfiledFanService(fan, () => _openEc(fan), _journal);
                    _services.Add(_fan);
                }
            }

            _nativeServices.Clear();
            var processorClocks = ProcessorClockLimits.For(context.DeviceDefinitionId, context.Identity.ProcessorName);
            if (_profile.UseStandardServices || !string.IsNullOrWhiteSpace(context.Identity.ProcessorName))
            {
                var processor = new ProfiledProcessorService(featureContext, _profile.Cpu,
                    processorClocks.Minimum, processorClocks.Maximum);
                _nativeServices.Add(processor);
                _services.Add(processor);
            }

            foreach (var factory in _profile.ServiceFactories)
            {
                var service = factory(featureContext);
                _nativeServices.Add(service);
                _services.Add(service);
            }

            _accessoryCollections.Clear();
            if (GenericAsusService.MatchesAccessoryIdentity(context.Identity))
            {
                foreach (var collection in AsusAuraTransport.DiscoverCollections())
                {
                    _accessoryCollections[collection.DevicePath] = collection;
                }
            }

            foreach (var asus in _nativeServices.OfType<GenericAsusService>())
            {
                asus.SetAccessoryCollections(_accessoryCollections.Values.ToArray());
            }

            if (!_nativeServices.Any(service => service is GenericAsusService)
                && GenericAsusService.MatchesAccessoryIdentity(context.Identity))
            {
                var accessories = new GenericAsusService(featureContext, firmwareControls: false);
                accessories.SetAccessoryCollections(_accessoryCollections.Values.ToArray());
                _nativeServices.Add(accessories);
                _services.Add(accessories);
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
                _oem = new ProfiledOemService(_profile, OnOem, () => _manageController);
                _services.Add(_oem);
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
                    }, () => _motion?.Latest, () => _touchpad?.Latest ?? default, () => _oem?.HidButtons);
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


            if (!DeviceWriteBudget.IsAvailable(command.Deadline) || cancellation.IsCancellationRequested)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                    "The device command has expired.", true);
            }

            if (CommandAdmission.Validate(command, descriptor, _surface!.Descriptors) is { } invalid)
            {
                return CommandResults.Rejected(command, invalid);
            }

            return _controlOwners.TryGetValue((command.CapabilityId, command.InstanceId), out var owner)
                ? await owner.ExecuteAsync(command, _cycleIdentity.Firmware, cancellation).ConfigureAwait(false)
                : CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "The native service no longer owns this control.");
        }, token);
    }

    public ValueTask SuspendAsync(EngineQuiesceContext context, CancellationToken token)
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

    public ValueTask<EngineStartResult> ResumeAsync(EngineResumeContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            await _journal!.BeginCycleAsync(token).ConfigureAwait(false);
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

    public ValueTask ReleaseControllerAsync(EngineControllerReleaseContext context, CancellationToken token)
    {
        return SetControllerManagementAsync(new EngineControllerManagementContext(false, context.Deadline), token);
    }

    public ValueTask SetControllerManagementAsync(EngineControllerManagementContext context, CancellationToken token)
    {
        return _commands.RunAsync(async () =>
        {
            if (_manageController == context.Enabled)
            {
                if (context.Enabled)
                {
                    foreach (var native in _nativeServices)
                    {
                        await native.SetControllerManagementAsync(true, token).ConfigureAwait(false);
                    }

                    foreach (var service in WantedServices().Where(service =>
                                 service.RequiresControllerOwnership && service.State != DeviceServiceState.Owned))
                    {
                        await DeviceServiceLifecycle.ReleaseAsync(service, Context(context.Deadline), token)
                            .ConfigureAwait(false);
                        if (service.State != DeviceServiceState.Idle)
                        {
                            throw new InvalidOperationException(
                                "Physical input ownership has not been released; a second reader cannot be acquired.");
                        }

                        await DeviceServiceLifecycle.AcquireAsync(service, Context(context.Deadline), token)
                            .ConfigureAwait(false);
                    }

                    await PublishAsync(token).ConfigureAwait(false);
                }

                return;
            }

            foreach (var service in _services
                         .Where(service => service.RequiresControllerOwnership || service is ProfiledOemService)
                         .Reverse())
            {
                await DeviceServiceLifecycle.ReleaseAsync(service, Context(context.Deadline), token)
                    .ConfigureAwait(false);
            }

            if (_services.Any(service =>
                    (service.RequiresControllerOwnership || service is ProfiledOemService) &&
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

            foreach (var native in _nativeServices)
            {
                await native.SetControllerManagementAsync(context.Enabled, token).ConfigureAwait(false);
            }

            foreach (var service in WantedServices()
                         .Where(service => service.RequiresControllerOwnership || service is ProfiledOemService))
            {
                await DeviceServiceLifecycle.AcquireAsync(service, Context(context.Deadline), token)
                    .ConfigureAwait(false);
            }

            await PublishOemDescriptorsAsync(token).ConfigureAwait(false);
            await PublishAsync(token).ConfigureAwait(false);
        }, token);
    }

    public async ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_quiescing && _manageController)
        {
            foreach (var service in _services)
            {
                await service.ApplyHapticOutputAsync(frame, token).ConfigureAwait(false);
            }
        }
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(_services.ToDictionary(
            service => service.ServiceId, service => service.State.ToString(), StringComparer.Ordinal));
    }

    public ValueTask<EngineStopResult> StopAsync(EngineStopContext context, CancellationToken token)
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

            _surface = null;
            return new EngineStopResult
            {
                Status = failed ? EngineStopStatus.Failed :
                    unverified ? EngineStopStatus.Unverified : EngineStopStatus.Clean
            };
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        _quiescing = true;
        _accessoryNotifications?.Dispose();
        _accessoryNotifications = null;
        _commands.StopObservation();
        await _commands.RunAsync(async () =>
        {
            foreach (var service in _services.AsEnumerable().Reverse())
            {
                await service.DisposeAsync().ConfigureAwait(false);
            }

            await _oemPublication.ConfigureAwait(false);
            _services.Clear();
        }, CancellationToken.None).ConfigureAwait(false);
        _commands.Dispose();
    }

    private ICpuPowerTransport OpenPower(ProfiledFeatureContext context)
    {
        if (_openPower is not null)
        {
            return _openPower();
        }

        if (_profile.OpenPower is not null)
        {
            return _profile.OpenPower(context);
        }

        if (_profile.Cpu == CpuPowerKind.Amd)
        {
            return AmdSmuTransport.Open();
        }

        return IntelMsrPowerTransport.Open();
    }

    private DeviceCycleContext<ProfiledCycleIdentity> Context(Deadline deadline)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(deadline, _cycleIdentity);
    }

    private IEnumerable<DeviceService<ProfiledCycleIdentity>> WantedServices()
    {
        return _services.Where(IsWanted);
    }

    private bool IsWanted(DeviceServiceStatus service)
    {
        return _manageController || service is not DeviceService<ProfiledCycleIdentity>
        {
            RequiresControllerOwnership: true
        };
    }

    private EngineStartResult Result()
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
                pressId = $"{id}:{++_oemSequence}";
                _oemPressIds[id] = pressId;
            }
        }

        if (_sink is { } sink && (!_quiescing || !down))
        {
            var input = new OemControlEvent(id, OemPressKind.Short, DateTimeOffset.UtcNow,
                pressId, down ? OemControlEdge.Pressed : OemControlEdge.Released);
            lock (_inputGate)
            {
                if (!_oemPublication.IsCompletedSuccessfully)
                {
                    _oemPublication = PublishOemAfterAsync(_oemPublication, sink, input);
                    return;
                }

                var publication = sink.PublishOemEventAsync(input, CancellationToken.None);
                if (publication.IsCompletedSuccessfully)
                {
                    publication.GetAwaiter().GetResult();
                }
                else
                {
                    _oemPublication = FinishOemPublicationAsync(publication);
                }
            }
        }
    }

    private static async Task PublishOemAfterAsync(Task previous, IDeviceSink sink, OemControlEvent input)
    {
        await previous.ConfigureAwait(false);
        await FinishOemPublicationAsync(sink.PublishOemEventAsync(input, CancellationToken.None)).ConfigureAwait(false);
    }

    private static async Task FinishOemPublicationAsync(ValueTask publication)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Change("oem", "publication", exception.Message, DeviceTraceLevel.Warn);
        }
    }

    private async ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_fan is { State: DeviceServiceState.Owned })
        {
            // Only explicitly declared sensors are sampled; accepted control intent is retained.
            await _fan.RefreshAsync(token).ConfigureAwait(false);
        }

        foreach (var service in _nativeServices)
        {
            if (service.State is DeviceServiceState.Owned or DeviceServiceState.Degraded)
            {
                await service.RefreshTelemetryAsync(token).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask RefreshAsync(string? id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await ObserveAccessoriesAsync(token).ConfigureAwait(false);
        foreach (var service in _services)
        {
            if (id is not null && !service.Descriptors.Any(descriptor => descriptor.CapabilityId == id))
            {
                continue;
            }

            if (service is ProfiledFeatureService feature)
            {
                await feature.RefreshAsync(id, token).ConfigureAwait(false);
            }
            else
            {
                await service.RefreshAsync(token).ConfigureAwait(false);
            }
        }

        if (_nativeServices.Any(service => service.DescriptorsChanged))
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

    private async ValueTask ObserveAccessoriesAsync(CancellationToken token)
    {
        if (_accessoryChanges.IsEmpty || _featureContext is null)
        {
            return;
        }

        foreach (var change in _accessoryChanges)
        {
            if (!_accessoryChanges.TryRemove(change.Key, out var arrived))
            {
                continue;
            }

            if (!arrived)
            {
                _accessoryCollections.Remove(change.Key);
                continue;
            }

            var collection = HidDevices.DescribePath(change.Key);
            if (collection is not null && collection.VendorId == AsusAuraProtocol.Vendor
                                       && AsusAuraTransport.Products.Contains(collection.ProductId)
                                       && !string.IsNullOrWhiteSpace(collection.PhysicalLocation))
            {
                _accessoryCollections[change.Key] = collection;
            }
        }

        var asus = _nativeServices.OfType<GenericAsusService>().SingleOrDefault();
        if (asus is null && _accessoryCollections.Count > 0)
        {
            asus = new GenericAsusService(_featureContext, firmwareControls: false);
            _nativeServices.Add(asus);
            _services.Add(asus);
            asus.SetAccessoryCollections(_accessoryCollections.Values.ToArray());
            await DeviceServiceLifecycle.AcquireAsync(asus, Context(Deadline.After(TimeSpan.FromSeconds(2))), token)
                .ConfigureAwait(false);
            BuildSurface();
            await _sink!.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
        }
        else
        {
            asus?.SetAccessoryCollections(_accessoryCollections.Values.ToArray());
        }
    }

    private ValueTask PublishOemDescriptorsAsync(CancellationToken token)
    {
        return _sink!.PublishOemControlsAsync(
            Array.AsReadOnly(_profile.OemControls
                .Where(control => _manageController || !control.RequiresControllerAcquisition)
                .Concat(_nativeServices.SelectMany(service => service.OemControls))
                .Concat(_profile.KeyboardChords.Where(chord => _manageController || !chord.RequiresController)
                    .GroupBy(chord => chord.Id, StringComparer.Ordinal).Select(group => group.First()).Select(chord =>
                        new OemControlDescriptor
                        {
                            ControlId = chord.Id,
                            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = chord.Label },
                            Placement = chord.Button is CanonicalButtons.RearPaddle1 or CanonicalButtons.RearPaddle2
                                or CanonicalButtons.RearPaddle3 or CanonicalButtons.RearPaddle4
                                ? OemControlPlacement.Rear
                                : OemControlPlacement.Front,
                            RequiresControllerAcquisition = chord.RequiresController,
                            ControllerButton = chord.Button & (CanonicalButtons.Guide | CanonicalButtons.QuickAccess)
                        })).GroupBy(control => control.ControlId, StringComparer.Ordinal).Select(group =>
                {
                    var control = group.First();
                    return control.ControllerButton != CanonicalButtons.None
                        ? control
                        : control with
                        {
                            ControllerButton = group.Select(item => item.ControllerButton)
                                .FirstOrDefault(button => button != CanonicalButtons.None)
                        };
                })
                .ToArray()), token);
    }
}
