using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.LegionGo;

internal sealed record LegionHardware(
    Func<DeviceIdentitySnapshot> Identity,
    Func<ILegionWmi> Wmi,
    Func<ILegionHid> Hid,
    Func<IEmbeddedController> Ec);

/// <summary>One reusable Lenovo lifecycle for every exact model; capabilities degrade by transport.</summary>
internal sealed partial class LegionGoDeviceEngine : ProfiledFeatureService
{
    private readonly ProfiledFeatureContext _context;
    private readonly LegionHardware _hardware;
    private readonly ObservedValues _observed = new();
    private readonly ServicePublications _publications;
    private int _brightness = 100;
    private string? _controllerBinding;
    private bool _controllerWanted;
    private IEmbeddedController? _ec;
    private bool _ecFaulted;
    private string _effect = "solid";
    private string _fanMode = "automatic";
    private bool _fansChanged;
    private ILegionHid? _hid;
    private bool _hidFaulted;
    private IDeviceSink? _host;
    private DeviceIdentitySnapshot? _identity;
    private bool _initialized;
    private DeviceRecoveryJournal? _journal;
    private int _leftColor = 0xFFFFFF;
    private bool _lightingEnabled = true;
    private LegionGoModel? _model;
    private string? _oemBinding;
    private bool _quiescing;
    private int _rightColor = 0xFFFFFF;
    private int _speed = 50;
    private CapabilityDescriptorSet? _surface;
    private ILegionWmi? _wmi;

    internal LegionGoDeviceEngine(ProfiledFeatureContext context) : this(context,
        new LegionHardware(() => context.Identity, () => new LegionWmi(), () => new WindowsLegionHid(),
            () => PawnIoEmbeddedController.OpenIndexed()))
    {
    }

    internal LegionGoDeviceEngine(ProfiledFeatureContext context, LegionHardware hardware) : base("legion-native")
    {
        _context = context;
        _publications = new ServicePublications(context.Sink);
        _hardware = hardware;
    }


    public override bool Suspendable => true;
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _surface?.Descriptors ?? [];
    internal override IReadOnlyList<CapabilityState> States => _publications.States;
    internal override IReadOnlyList<OemControlDescriptor> OemControls => _publications.OemControls;
    internal override bool DescriptorsChanged => _publications.DescriptorsChanged;

    internal override void AcknowledgeDescriptors()
    {
        _publications.AcknowledgeDescriptors();
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        if (!_initialized)
        {
            _model = LegionGoModels.ById(_context.DeviceDefinitionId) ??
                     throw new ArgumentException("Unknown Lenovo model.");
            _identity = _context.Identity;
            _host = _publications;
            _journal = _context.Journal;
            _controllerWanted = _context.ControllerManagementEnabled;
            _wmi = _hardware.Wmi();
            _hid = _hardware.Hid();
            _hid.Disconnected += BeginReconnect;
            _initialized = true;
        }

        _quiescing = false;
        await OpenTransportsAsync(token).ConfigureAwait(false);
        await RestoreAsync(token, cycle.Deadline).ConfigureAwait(false);
        if (_controllerWanted)
        {
            await AcquireControllerAsync(token).ConfigureAwait(false);
        }

        BuildSurface();
        await PublishSurfaceAsync(token).ConfigureAwait(false);
        var result = StartResult();
        return Set(
            result.State == EngineOperationalState.Active ? DeviceServiceState.Owned : DeviceServiceState.Degraded,
            result.Reason);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        _quiescing = true;
        await ReleaseControllerCoreAsync(token).ConfigureAwait(false);
        await RestoreAsync(token, cycle.Deadline).ConfigureAwait(false);
        await ReleaseOemAsync().ConfigureAwait(false);
        if (_hid is not null)
        {
            await _hid.CloseAsync(token).ConfigureAwait(false);
        }

        _ec?.Dispose();
        _ec = null;
        return Set(_journal?.OutstandingEntries.Count > 0 || _hidFaulted
            ? DeviceServiceState.ReleasedUnverified
            : DeviceServiceState.Idle);
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ExecuteCoreAsync(command, token);
    }

    internal override async ValueTask RefreshAsync(CancellationToken token)
    {
        await RefreshObservedAsync(token).ConfigureAwait(false);
        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    internal override ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return _hid?.Reading == true ? _hid.RumbleAsync(frame, token) : ValueTask.CompletedTask;
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        _controllerWanted = enabled;
        if (enabled)
        {
            _hidFaulted = false;
            if (_hid?.Connected == true && !_hid.Listening)
            {
                await AcquireOemAsync(token).ConfigureAwait(false);
            }

            await AcquireControllerAsync(token).ConfigureAwait(false);
        }
        else
        {
            await ReleaseControllerCoreAsync(token).ConfigureAwait(false);
        }

        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    internal override async ValueTask DisposeAsync()
    {
        _quiescing = true;
        if (_hid is not null)
        {
            _hid.Disconnected -= BeginReconnect;
            await _hid.DisposeAsync().ConfigureAwait(false);
        }

        _ec?.Dispose();
        _wmi?.Dispose();
        _ec = null;
        _wmi = null;
        _hid = null;
    }

    private async ValueTask OpenTransportsAsync(CancellationToken token)
    {
        _hidFaulted = false;
        _ecFaulted = false;
        try
        {
            if (await _hid!.OpenAsync(_model!, token).ConfigureAwait(false))
            {
                await AcquireOemAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _hidFaulted = true;
            DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-hid", ex.Message);
        }

        if (_model!.EcFan)
        {
            try
            {
                _ec = _hardware.Ec();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _ecFaulted = true;
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-ec", ex.Message);
            }
        }
    }

    private async ValueTask AcquireControllerAsync(CancellationToken token)
    {
        if (_hid?.Connected != true || _hid.Reading)
        {
            return;
        }

        try
        {
            _controllerBinding = _hid.Binding;
            foreach (var report in LegionProtocol.Motion(_model!, true))
            {
                await _hid.WriteAsync(report, token).ConfigureAwait(false);
            }

            foreach (var report in LegionProtocol.Touchpad(_model!, _hid.NativeTouchpad))
            {
                await _hid.WriteAsync(report, token).ConfigureAwait(false);
            }

            await _hid.StartAsync(_host!, token).ConfigureAwait(false);
            _hidFaulted = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _hidFaulted = true;
            DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
            await ReleaseControllerCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseControllerCoreAsync(CancellationToken token)
    {
        if (_hid is null)
        {
            return;
        }

        await _hid.StopAsync(token).ConfigureAwait(false);
        if (_controllerBinding is not null && _controllerBinding == _hid.Binding && _hid.Connected)
        {
            var baseline = LegionProtocol.Motion(_model!, false).Concat(LegionProtocol.Touchpad(_model!, false));
            foreach (var report in baseline)
            {
                try
                {
                    await _hid.WriteAsync(report, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _hidFaulted = true;
                    DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller",
                        $"Native controller restoration unconfirmed: {ex.Message}");
                }
            }
        }

        _controllerBinding = null;
        if (_host is not null)
        {
            await _host.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await _host.PublishPhysicalDevicesAsync([], null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private EngineStartResult StartResult()
    {
        return new EngineStartResult
        {
            State = _wmi?.FeaturesAvailable == true && _hid?.Connected == true &&
                    (!_controllerWanted || _hid.Reading) &&
                    (!_model!.EcFan || _ec is not null)
                ? EngineOperationalState.Active
                : EngineOperationalState.Degraded,
            Reason = _hid?.Connected != true
                ? new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                    "Lenovo HID collection unavailable; WMI controls remain independent.")
                : null
        };
    }

    private void BeginReconnect()
    {
        // The shared observation owner reconnects the reader. No second command/lifecycle lane runs here.
        _controllerBinding = null;
    }

    private async ValueTask AcquireOemAsync(CancellationToken token)
    {
        _oemBinding = _hid!.Binding;
        if (!_model!.Slim)
        {
            await _hid.WriteAsync([5, 6, 0x69, 9, 1, 2, 1], token).ConfigureAwait(false);
        }

        await _hid.ListenAsync(_host!, token).ConfigureAwait(false);
    }

    private async ValueTask ReleaseOemAsync()
    {
        if (_oemBinding is not null && _oemBinding == _hid?.Binding && _hid.Connected && !_model!.Slim)
        {
            try
            {
                await _hid.WriteAsync([5, 6, 0x69, 9, 1, 1, 1], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _hidFaulted = true;
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-oem", ex.Message);
            }
        }

        _oemBinding = null;
    }
}
