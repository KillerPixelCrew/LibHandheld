using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>The hardware transports one plugin instance uses; replaced by fakes in tests.</summary>
/// <param name="Identity">Side-effect-free machine identity reader used for admission and firmware binding.</param>
/// <param name="Acpi">Shared serialized ATKACPI transport owned by the plugin.</param>
/// <param name="Vendor">Factory for the exact model's vendor HID transport.</param>
/// <param name="Aura">Factory for the exact model's lighting transport.</param>
/// <param name="Controller">Factory combining model identity and shared OEM buttons into the physical controller source.</param>
/// <param name="Motion">Factory for a model-mapped motion source.</param>
/// <param name="Keyboard">Shared OEM-key hook owned by the plugin.</param>
/// <param name="Delay">Cancellable timing dependency used for hardware write spacing.</param>
/// <param name="OnAcPower">Optional read-only power-source callback for an injected hardware fixture.</param>
internal sealed record AllyHardwareServices(
    IAllyIdentityReader Identity,
    IAsusAcpi Acpi,
    Func<AllyModel, IAllyVendorHid> Vendor,
    Func<AllyModel, IAllyAuraHid> Aura,
    Func<AllyModel, AllyOemButtonState, IAllyControllerSource> Controller,
    Func<AllyModel, IAllyMotionSource> Motion,
    IAllyKeyboardSource Keyboard,
    Func<TimeSpan, CancellationToken, Task> Delay,
    Func<bool>? OnAcPower = null);

/// <summary>The device plugin for the ASUS ROG Ally, Ally X, Xbox Ally and Xbox Ally X.</summary>
/// <remarks>
///     Built from Handheld Companion 1.3.1.6 and HHD without hardware. Each model's facts live in
///     <c>AllyModels</c>; PROVENANCE.md cites the source of every one and lists what attended evidence
///     must confirm. The plugin owns ACPI, HID, controller and motion resources; WSGM owns profiles,
///     virtual targets and HidHide. Services degrade independently. Command and observation work is
///     serialized, and accepted writes remain observed when readback is unavailable.
/// </remarks>
internal sealed partial class AllyDeviceEngine : ProfiledFeatureService
{
    private readonly ProfiledFeatureContext _context;
    private readonly AllyHardwareServices _hardware;

    /// <summary>Values this cycle wrote, keyed by capability and instance, for firmware without readback.</summary>
    private readonly ObservedValues _observedValues = new();

    private readonly ServicePublications _publications;

    private bool _active;
    private IAllyAuraHid? _aura;
    private AllyOemButtonState? _buttons;
    private ChargeLimitService? _charge;
    private ControllerService? _controller;
    private AllyIdentityState? _cycleIdentity;
    private CapabilityDescriptorSet? _descriptorSet;
    private bool _disposed;
    private FanService? _fans;
    private IDeviceSink? _host;
    private bool _initialized;
    private DeviceRecoveryJournal? _journal;
    private KeyboardOemService? _keyboard;
    private LightingService? _lighting;
    private AllyModel? _model;
    private AllyMotionService? _motion;
    private IAllyMotionSource? _motionSource;
    private PowerService? _power;
    private bool _quiescing;
    private IReadOnlyList<DeviceService<AllyIdentityState>> _services = [];
    private IAllyControllerSource? _source;
    private IAllyVendorHid? _vendor;
    private VendorEventService? _vendorEvents;

    /// <summary>Creates the production plugin with Windows transports.</summary>
    // ReSharper disable once UnusedMember.Global
    internal AllyDeviceEngine(ProfiledFeatureContext context) : this(context, CreateWindowsServices())
    {
    }

    internal AllyDeviceEngine(ProfiledFeatureContext context, AllyHardwareServices services) : base("rogally-native")
    {
        _context = context;
        _publications = new ServicePublications(context.Sink);
        _hardware = services;
    }


    public override bool Suspendable => true;
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptorSet?.Descriptors ?? [];
    internal override IReadOnlyList<CapabilityState> States => _publications.States;
    internal override IReadOnlyList<OemControlDescriptor> OemControls => _publications.OemControls;
    internal override bool DescriptorsChanged => _publications.DescriptorsChanged;


    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    private async ValueTask<EngineStartResult> InitializeAsync(
        EngineStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            throw new InvalidOperationException("The plugin already owns a device cycle.");
        }

        if (AllyModels.ById(context.DeviceDefinitionId) is not { } definition)
        {
            throw new InvalidOperationException("WSGM supplied a device definition this package does not own.");
        }


        DeviceTrace.Info("lifecycle", $"start: definition={definition.DefinitionId}.");

        var identity = new AllyIdentityState
        {
            Snapshot = context.Identity, Model = definition,
            OnAcPower = _hardware.OnAcPower?.Invoke() ?? WindowsMachineIdentity.OnAcPower()
        };
        if (identity.Model != definition)
        {
            DeviceTrace.Error("lifecycle",
                $"live identity {identity.Snapshot.BaseboardProduct ?? "<none>"} is not {definition.DefinitionId}.");
            throw new InvalidOperationException("The live SMBIOS identity no longer matches the detected Ally model.");
        }

        _host = context.Host;
        _model = definition;

        _observedValues.Clear();
        _cycleIdentity = identity;
        _quiescing = false;
        try
        {
            _journal = context.Journal;
            _buttons = new AllyOemButtonState();
            _vendor = _hardware.Vendor(definition);
            _aura = _hardware.Aura(definition);
            _source = _hardware.Controller(definition, _buttons);
            _motionSource = _hardware.Motion(definition);
            _vendorEvents = new VendorEventService(_vendor, context.Host, _buttons);
            _keyboard = new KeyboardOemService(_hardware.Keyboard, context.Host, _buttons);
            _power = new PowerService(_hardware.Acpi, _hardware.Delay, _journal);
            _fans = new FanService(_hardware.Acpi, _hardware.Delay, _journal);
            _charge = new ChargeLimitService(_hardware.Acpi);
            _lighting = new LightingService(_aura);
            _motion = new AllyMotionService(_motionSource);
            _controller = new ControllerService(_source, _vendor, _motion, _keyboard, _buttons, context.Host,
                _journal)
            {
                Enabled = context.ControllerManagementEnabled
            };

            // Acquired in this order and released in reverse. The keyboard service precedes the
            // controller, which enables its rear keys, and motion precedes the controller it rides.
            _services = [_vendorEvents, _keyboard, _power, _fans, _charge, _lighting, _motion, _controller];
            BuildCapabilitySurface();

            {
                await ReconcileOutstandingAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            await context.Host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken).ConfigureAwait(false);
            await context.Host.PublishOemControlsAsync(AllyModels.OemControls(definition), cancellationToken)
                .ConfigureAwait(false);
            await DeviceServiceLifecycle.AcquireAllAsync(_services, Context(Deadline.After(TimeSpan.FromSeconds(15))),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
            return CurrentStartResult();
        }
        catch
        {
            _quiescing = true;
            await RollBackFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    internal override ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        return _controller is null || _quiescing
            ? ValueTask.CompletedTask
            : _controller.ApplyHapticsAsync(frame, cancellationToken);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
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
        await Step("controller source", () => _source?.DisposeAsync() ?? ValueTask.CompletedTask)
            .ConfigureAwait(false);
        await Step("motion source", () => _motionSource?.DisposeAsync() ?? ValueTask.CompletedTask)
            .ConfigureAwait(false);
        await Step("vendor HID", () => _vendor?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("Aura HID", () => _aura?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        _source = null;
        _motionSource = null;
        _vendor = null;
        _aura = null;
        await Step("keyboard", _hardware.Keyboard.DisposeAsync).ConfigureAwait(false);
        await Step("ACPI", () =>
        {
            _hardware.Acpi.Dispose();
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        if (failures.Count != 0)
        {
            throw new AggregateException("Ally plugin disposal was not complete.", failures);
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

    private async ValueTask DisposeCycleTransportsAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            _source = null;
        }

        if (_motionSource is not null)
        {
            await _motionSource.DisposeAsync().ConfigureAwait(false);
            _motionSource = null;
        }

        if (_vendor is not null)
        {
            await _vendor.DisposeAsync().ConfigureAwait(false);
            _vendor = null;
        }

        if (_aura is not null)
        {
            await _aura.DisposeAsync().ConfigureAwait(false);
            _aura = null;
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
            await InitializeAsync(new EngineStartContext
            {
                Host = _publications, Identity = _context.Identity, DeviceDefinitionId = _context.DeviceDefinitionId,
                StateDirectory = _context.StateDirectory, Journal = _context.Journal, Resources = _context.Resources,
                ControllerManagementEnabled = _context.ControllerManagementEnabled
            }, token).ConfigureAwait(false);
            _initialized = true;
        }
        else
        {
            await DeviceServiceLifecycle.AcquireAllAsync(_services, Context(cycle.Deadline), token)
                .ConfigureAwait(false);
            BuildCapabilitySurface();
            await _publications.PublishDescriptorsAsync(_descriptorSet!, token).ConfigureAwait(false);
            await PublishStatesAsync(token).ConfigureAwait(false);
            _active = true;
        }

        var state = CurrentStartResult();
        return Set(state.State == EngineOperationalState.Active ? DeviceServiceState.Owned :
            state.State == EngineOperationalState.Degraded ? DeviceServiceState.Degraded : DeviceServiceState.Passive,
            state.Reason);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        _quiescing = true;
        await DeviceServiceLifecycle.ReleaseAllAsync(_services, Context(cycle.Deadline), token).ConfigureAwait(false);
        _active = false;
        _observedValues.Clear();
        var result = DeviceServiceLifecycle.StopResult(_services);
        return Set(result.Status == EngineStopStatus.Failed ? DeviceServiceState.Faulted :
            result.Status == EngineStopStatus.Unverified ? DeviceServiceState.ReleasedUnverified :
            DeviceServiceState.Idle, result.Reason);
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
            await DeviceServiceLifecycle.AcquireAsync(_controller, Context(deadline), token).ConfigureAwait(false);
        }
        else
        {
            await ReleaseControllerCoreAsync(deadline, token).ConfigureAwait(false);
        }

        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    private async ValueTask ReleaseControllerCoreAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        if (_controller is not null)
        {
            await _controller.ReleaseControllerAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RollBackFailedStartAsync()
    {
        await DeviceServiceLifecycle.RollBackStartAsync(
            _services,
            Context(Deadline.After(TimeSpan.FromSeconds(12))),
            _host,
            _descriptorSet).ConfigureAwait(false);
        await DisposeCycleTransportsAsync().ConfigureAwait(false);
        _journal = null;
        _active = false;
        _descriptorSet = null;
        _services = [];
    }

    private DeviceCycleContext<AllyIdentityState> Context(Deadline deadline)
    {
        return new DeviceCycleContext<AllyIdentityState>(deadline,
            _cycleIdentity ?? throw new InvalidOperationException("No cycle identity is available."));
    }

    private EngineStartResult CurrentStartResult()
    {
        return DeviceServiceLifecycle.StartResult(
            _services,
            service => service != _controller || _controller.Enabled,
            "No Ally hardware service could be acquired.",
            _host);
    }

    private static AllyHardwareServices CreateWindowsServices()
    {
        return new AllyHardwareServices(
            new WindowsAllyIdentityReader(),
            new WindowsAsusAcpi(),
            model => new WindowsAllyVendorHid(model.ControllerProductIds),
            model => new WindowsAllyAuraHid(model.ControllerProductIds),
            (model, buttons) => new WindowsAllyControllerSource(model, buttons),
            model => new WindowsAllyMotionSource(model),
            new WindowsAllyKeyboardHook(),
            Task.Delay);
    }
}
