using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

/// <summary>The device plugin for the MSI Claw family; <see cref="ClawModels" /> lists every supported board.</summary>
/// <remarks>
///     Owns independently degradable hardware services and a serialized command/observation lane.
///     Detection uses only the supplied identity; start and resume acquire services and replace published sets.
///     Controller management changes controller ownership without stopping power, lighting or motion.
///     Successful writes remain observed when readback cannot confirm them; uncertain writes are not retried.
/// </remarks>
internal sealed partial class ClawDeviceEngine : ProfiledFeatureService
{
    private readonly ProfiledFeatureContext _context;

    /// <summary>Values this cycle wrote or read, keyed by capability and instance; nothing reads controls back.</summary>
    private readonly ObservedValues _observedValues = new();

    private readonly ServicePublications _publications;

    /// <summary>Orders state publication between serialized turns and transport callbacks.</summary>
    private readonly Lock _publishGate = new();

    private readonly ClawHardwareServices _services;
    private ChargeLimitService? _chargeLimit;
    private ClawChargeLimitCapability? _chargeLimitCapability;
    private ControllerService? _controller;
    private ClawIdentityState? _cycleIdentity;
    private ClawModel? _cycleModel;
    private CapabilityDescriptorSet? _descriptorSet;
    private bool _disposed;
    private ClawFanCapability? _fanCapability;
    private FanService? _fans;
    private bool _initialized;
    private DeviceRecoveryJournal? _journal;
    private LightingService? _lighting;
    private ClawLightingCapability? _lightingCapability;
    private MotionService? _motion;
    private OemEventService? _oem;
    private PowerService? _power;
    private ClawPowerCapability? _powerCapability;
    private bool _quiescing;
    private ChordSuppressorService? _suppressor;
    private TelemetryService? _telemetry;

    /// <summary>Creates the production plugin with Windows hardware transports.</summary>
    // ReSharper disable once UnusedMember.Global
    internal ClawDeviceEngine(ProfiledFeatureContext context) : this(context, CreateWindowsServices())
    {
    }

    internal ClawDeviceEngine(ProfiledFeatureContext context, ClawHardwareServices services) : base("msiclaw-native")
    {
        _context = context;
        _publications = new ServicePublications(context.Sink);
        _services = services;
    }

    /// <summary>The model this cycle started on. Only a started cycle has one; nothing falls back to a default.</summary>
    private ClawModel Model => _cycleModel ?? throw new InvalidOperationException("No device cycle is active.");

    /// <summary>The cycle's services in start order; stop releases them in reverse.</summary>
    internal IReadOnlyList<DeviceService<ClawIdentityState>> Services { get; private set; } = [];

    public override bool Suspendable => true;
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptorSet?.Descriptors ?? [];
    internal override IReadOnlyList<CapabilityState> States => _publications.States;
    internal override IReadOnlyList<OemControlDescriptor> OemControls => _publications.OemControls;
    internal override bool DescriptorsChanged => _publications.DescriptorsChanged;

    /// <summary>Starts the first cycle from the plugin context and publishes through <see cref="_publications" />.</summary>
    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var definedModel = ClawModels.FindByDefinition(_context.DeviceDefinitionId)
                           ?? throw new InvalidOperationException("WSGM supplied a different device definition.");

        // Installed before the first hardware read, because startup is when the failures worth
        // tracing happen and an ambient sink installed late traces nothing that mattered.
        DeviceTrace.Info(
            "lifecycle",
            $"start: definition={_context.DeviceDefinitionId}.");

        var identity = new ClawIdentityState
        {
            Snapshot = _context.Identity, OnAcPower = OnAcPower()
        };

        DeviceTrace.Info(
            "lifecycle",
            $"model: {definedModel.DisplayName} ({definedModel.BoardProduct}, HC {definedModel.HcClass}, "
            + (definedModel.HardwareVerified ? "hardware-verified" : "built from HC, no hardware pass")
            + $"); sku={identity.Snapshot.SystemSku ?? "<none>"}, ec={identity.Snapshot.EcFirmwareVersion ?? "<none>"}, "
            + $"mcu={identity.Snapshot.McuFirmwareVersion ?? "<none>"}.");

        _observedValues.Clear();
        _cycleIdentity = identity;
        _quiescing = false;

        try
        {
            _cycleModel = definedModel;
            var powerCapability = new ClawPowerCapability(_services.Wmi, definedModel, _services.Delay,
                _services.Time);
            var chargeLimitCapability = new ClawChargeLimitCapability(_services.Wmi);
            var fanCapability = new ClawFanCapability(_services.Wmi);
            var lightingCapability = new ClawLightingCapability(
                _services.Mcu,
                ClawModels.LightingProfileAddress(identity.Snapshot.McuFirmwareVersion));
            _powerCapability = powerCapability;
            _chargeLimitCapability = chargeLimitCapability;
            _fanCapability = fanCapability;
            _lightingCapability = lightingCapability;
            _journal = _context.Journal;
            _oem = new OemEventService(_services.OemEvents, _publications, _services.OemButtons);
            _power = new PowerService(powerCapability, _journal);
            _chargeLimit = new ChargeLimitService();
            _fans = new FanService(fanCapability, _journal);
            _telemetry = new TelemetryService(fanCapability);
            _lighting = new LightingService(_services.Mcu);
            _motion = new MotionService(_services.Motion, definedModel);

            _controller = new ControllerService(
                _services.Mcu,
                _services.Controller,
                _motion,
                _publications,
                _journal,
                definedModel,
                _services.Delay,
                PublishServiceStates)
            {
                Enabled = _context.ControllerManagementEnabled
            };
            _suppressor = new ChordSuppressorService(
                _services.ChordSuppressor,
                _oem);

            // Start and resume acquire in this order, and stop releases in reverse. Chord suppression
            // follows the OEM event source it requires, and the controller follows the motion service
            // it was built with. ClawDeviceEngineTests pins both orders.
            Services =
            [
                _oem,
                _power,
                _chargeLimit,
                _fans,
                _telemetry,
                _lighting,
                _motion,
                _controller,
                _suppressor
            ];
            BuildCapabilitySurface();
            await ReconcileOutstandingAsync(identity, cancellationToken).ConfigureAwait(false);
            await _publications.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                .ConfigureAwait(false);
            await _publications.PublishOemControlsAsync(CreateOemControls(), cancellationToken)
                .ConfigureAwait(false);

            // HC repairs a missing MSI_Event class once, when it opens the device. A resume only subscribes
            // again: a repair that did not help would restart the ACPI device power and fans go through.
            await _services.OemEvents.EnsureEventClassAsync(cancellationToken).ConfigureAwait(false);
            await DeviceServiceLifecycle.AcquireAllAsync(Services,
                OperationContext(Deadline.After(TimeSpan.FromSeconds(15))), cancellationToken).ConfigureAwait(false);
            ObserveTelemetry();
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _quiescing = true;
            await RollBackFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    internal override ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        return _controller is null || _quiescing
            ? ValueTask.CompletedTask
            : _controller.ApplyHapticsAsync(frame, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The engine awaits <see cref="ReleaseAsync" /> first. Disposal only releases handles and never writes the
    ///     hardware: a cycle that was not stopped is restored from the journal at the next start. Every owner is
    ///     disposed even when an earlier one throws, and the failures are thrown together at the end.
    /// </remarks>
    internal override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> failures = [];
        await Step("chord suppressor", _services.ChordSuppressor.DisposeAsync).ConfigureAwait(false);
        await Step("motion", _services.Motion.DisposeAsync).ConfigureAwait(false);
        await Step("controller", _services.Controller.DisposeAsync).ConfigureAwait(false);
        await Step("MCU", _services.Mcu.DisposeAsync).ConfigureAwait(false);
        await Step("OEM events", _services.OemEvents.DisposeAsync).ConfigureAwait(false);
        await Step("WMI", _services.Wmi.DisposeAsync).ConfigureAwait(false);
        if (failures.Count != 0)
        {
            throw new AggregateException("Claw plugin disposal was not complete.", failures);
        }

        async ValueTask Step(string name, Func<ValueTask> dispose)
        {
            try
            {
                await dispose().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Failure("plugin", name + " disposal failed", ex);
                failures.Add(ex);
            }
        }
    }

    internal override void AcknowledgeDescriptors()
    {
        _publications.AcknowledgeDescriptors();
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        _quiescing = false;
        if (!_initialized)
        {
            await InitializeAsync(token).ConfigureAwait(false);
            _initialized = true;
        }
        else
        {
            await DeviceServiceLifecycle.AcquireAllAsync(Services, OperationContext(cycle.Deadline), token)
                .ConfigureAwait(false);
            ObserveTelemetry();
            BuildCapabilitySurface();
            await _publications.PublishDescriptorsAsync(_descriptorSet!, token).ConfigureAwait(false);
            await PublishStatesAsync(token).ConfigureAwait(false);
        }

        var state = CurrentStartResult();
        return Set(DeviceServiceLifecycle.ServiceState(state), state.Reason);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        _quiescing = true;
        await DeviceServiceLifecycle.ReleaseAllAsync(Services, OperationContext(cycle.Deadline), token)
            .ConfigureAwait(false);
        _observedValues.Clear();
        var result = DeviceServiceLifecycle.StopResult(Services);
        return Set(DeviceServiceLifecycle.ServiceState(result), result.Reason);
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return await ExecuteBoundCommandAsync(command, token).ConfigureAwait(false);
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        return RefreshAsync(null, token);
    }

    internal override async ValueTask RefreshAsync(string? capabilityId, CancellationToken token)
    {
        await RefreshObservedForCapabilityAsync(capabilityId, token).ConfigureAwait(false);
        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        if (_controller is null)
        {
            return;
        }

        _controller.Enabled = enabled;
        var deadline = Deadline.After(TimeSpan.FromSeconds(6));
        if (enabled)
        {
            await DeviceServiceLifecycle.AcquireAsync(_controller, OperationContext(deadline), token)
                .ConfigureAwait(false);
        }
        else
        {
            await _controller.ReleaseControllerAsync(deadline, token).ConfigureAwait(false);
        }

        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    private async ValueTask RollBackFailedStartAsync()
    {
        await DeviceServiceLifecycle.RollBackStartAsync(
            Services,
            OperationContext(Deadline.After(TimeSpan.FromSeconds(12))),
            _publications,
            _descriptorSet).ConfigureAwait(false);

        _journal = null;
        _descriptorSet = null;
        _observedValues.Clear();
        Services = [];
    }

    private DeviceCycleContext<ClawIdentityState> OperationContext(Deadline deadline)
    {
        return new DeviceCycleContext<ClawIdentityState>(
            deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private EngineStartResult CurrentStartResult()
    {
        // A controller WSGM asked to keep off is not a service that failed.
        return DeviceServiceLifecycle.StartResult(
            Services,
            service => service != _controller || _controller.Enabled,
            "No Claw hardware service could be acquired.",
            _publications);
    }

    private bool OnAcPower()
    {
        return WindowsMachineIdentity.OnAcPower(_services.OnAcPower);
    }

    private static ClawHardwareServices CreateWindowsServices()
    {
        MsiWmiPlatform wmi = new();

        // One latch, shared by the two services that need it: the OEM event source latches a press
        // and the controller reader merges it into the next samples. The buttons are physical
        // controller buttons that the firmware happens to deliver out of band.
        OemButtonLatch oemButtons = new();
        return new ClawHardwareServices(
            wmi,
            new MsiOemEventSource(),
            new WindowsClawMcuTransport(),
            new WindowsClawControllerSource(oemButtons),
            new WindowsClawMotionSource(),
            new FirmwareChordSuppressor(),
            oemButtons,
            Task.Delay);
    }
}
