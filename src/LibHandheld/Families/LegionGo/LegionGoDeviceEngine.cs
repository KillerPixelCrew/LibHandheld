using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.LegionGo;

internal sealed record LegionHardware(
    Func<DeviceIdentitySnapshot> Identity,
    Func<ILegionWmi> Wmi,
    Func<ILegionHid> Hid,
    Func<IEmbeddedController> Ec);

/// <summary>One reusable Lenovo lifecycle for every exact model; capabilities degrade by transport.</summary>
internal sealed partial class LegionGoDeviceEngine : IDeviceEngine
{
    private readonly LegionHardware _hardware;
    private readonly Dictionary<(string, string?), CapabilityValue> _observed = [];
    private readonly DeviceReconnect _reconnect = new();
    private readonly DeviceCommandSerializer _serializer;
    private readonly HashSet<(string, string?)> _written = [];
    private bool _active;
    private int _brightness = 100;
    private string? _controllerBinding;
    private bool _controllerWanted;
    private long _cycle;
    private bool _disposed;
    private IEmbeddedController? _ec;
    private bool _ecFaulted;
    private string _effect = "solid";
    private IReadOnlyList<CurvePoint>? _fanCurve;
    private bool _fanCurveFaulted;
    private string _fanMode = "automatic";
    private long _generation;
    private ILegionHid? _hid;
    private bool _hidFaulted;
    private IDeviceSink? _host;
    private DeviceIdentitySnapshot? _identity;
    private LegionRecoveryJournal? _journal;
    private int? _lastFanRpm;
    private int _leftColor = 0xFFFFFF;
    private bool _lightingEnabled = true;
    private LegionGoModel? _model;
    private string? _oemBinding;
    private bool _quiescing;
    private int _rightColor = 0xFFFFFF;
    private int _speed = 50;
    private CapabilityDescriptorSet? _surface;
    private ILegionWmi? _wmi;

    public LegionGoDeviceEngine() : this(new LegionHardware(LegionIdentity.Read, () => new LegionWmi(),
        () => new WindowsLegionHid(), () => PawnIoEmbeddedController.OpenIndexed()))
    {
    }

    internal LegionGoDeviceEngine(LegionHardware hardware)
    {
        _hardware = hardware;
        _serializer = new DeviceCommandSerializer("Legion Go", () => _active && !_quiescing ? _surface : null,
            RefreshAsync, PublishStatesAsync);
    }

    public string PackageId => LegionGoModels.PackageId;

    public ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = LegionGoModels.Match(context.Identity);
        return ValueTask.FromResult(new PluginDetectionResult
            { Matched = model is not null, DeviceDefinitionId = model?.DefinitionId });
    }

    public async ValueTask<PluginStartResult> StartAsync(PluginStartContext context,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            throw new InvalidOperationException("The Lenovo cycle is already active.");
        }

        _model = LegionGoModels.ById(context.DeviceDefinitionId) ??
                 throw new ArgumentException("Unknown Lenovo model.", nameof(context));
        _identity = _hardware.Identity();
        if (LegionGoModels.Match(_identity) != _model)
        {
            throw new IOException("The live Lenovo model does not match detection.");
        }

        if (context.CycleGeneration != context.Host.CycleGeneration)
        {
            throw new ArgumentException("Inconsistent Lenovo cycle.", nameof(context));
        }

        _host = context.Host;
        _cycle = context.CycleGeneration;
        _controllerWanted = context.ControllerManagementEnabled;
        _observed.Clear();
        _written.Clear();
        _journal = await LegionRecoveryJournal.OpenAsync(context.StateDirectory, cancellationToken)
            .ConfigureAwait(false);
        _wmi = _hardware.Wmi();
        _hid = _hardware.Hid();
        _hid.Disconnected += BeginReconnect;
        _active = true;
        _quiescing = false;
        try
        {
            await _serializer.RunAsync(async () =>
            {
                await OpenTransportsAsync(cancellationToken).ConfigureAwait(false);
                await RestoreAsync(cancellationToken).ConfigureAwait(false);
                if (_controllerWanted)
                {
                    await AcquireControllerAsync(cancellationToken).ConfigureAwait(false);
                }

                BuildSurface();
                await PublishSurfaceAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            _serializer.StartObservation();
            if (_hid?.Connected != true)
            {
                BeginReconnect();
            }

            return StartResult();
        }
        catch
        {
            await StopAsync(new PluginStopContext(PluginStopReason.StartFailed, Deadline.Never), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        return _serializer.ExecuteAsync(command, token => ExecuteCoreAsync(command, token), cancellationToken);
    }

    public async ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken)
    {
        _quiescing = true;
        _serializer.StopObservation();
        await _reconnect.StopAsync().ConfigureAwait(false);
        await _serializer.RunAsync(async () =>
        {
            await ReleaseControllerCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await RestoreAsync(CancellationToken.None, context.Deadline).ConfigureAwait(false);
            await ReleaseOemAsync().ConfigureAwait(false);
            await _hid!.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            _ec?.Dispose();
            _ec = null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        await _serializer.RunAsync(async () =>
        {
            var identity = _hardware.Identity();
            if (LegionGoModels.Match(identity) != _model ||
                LegionIdentity.Binding(identity) != LegionIdentity.Binding(_identity!))
            {
                throw new IOException("The Lenovo model or BIOS changed during suspension.");
            }

            _cycle = context.CycleGeneration;
            _quiescing = false;
            _observed.Clear();
            _written.Clear();
            await OpenTransportsAsync(cancellationToken).ConfigureAwait(false);
            if (_controllerWanted)
            {
                await AcquireControllerAsync(cancellationToken).ConfigureAwait(false);
            }

            BuildSurface();
            await PublishSurfaceAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        _serializer.StartObservation();
        return StartResult();
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = new Dictionary<string, string>
            {
                ["model"] = _model?.DefinitionId ?? "none", ["wmi"] = _wmi is null ? "released" : "opened",
                ["hid"] = _hid?.Connected == true ? "connected" : "unavailable",
                ["controller"] = _hid?.Reading == true ? "acquired" : "released",
                ["ec"] = _ec is null ? "unavailable" : "opened", ["recovery"] = _journal?.DiagnosticState ?? "closed"
            }
        });
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        return _hid?.Reading == true ? _hid.RumbleAsync(frame, cancellationToken) : ValueTask.CompletedTask;
    }

    public async ValueTask SetControllerManagementAsync(PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Enabled)
        {
            _controllerWanted = false;
            await _reconnect.StopAsync().ConfigureAwait(false);
        }

        await _serializer.RunAsync(async () =>
        {
            _controllerWanted = context.Enabled;
            if (context.Enabled && !_quiescing)
            {
                if (_hid?.Connected == true && !_hid.Listening)
                {
                    _hidFaulted = true;
                    await AcquireOemAsync(cancellationToken).ConfigureAwait(false);
                    _hidFaulted = false;
                }

                await AcquireControllerAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReleaseControllerCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await PublishStatesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        _controllerWanted = false;
        await _reconnect.StopAsync().ConfigureAwait(false);
        await _serializer.RunAsync(() => ReleaseControllerCoreAsync(CancellationToken.None), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken cancellationToken)
    {
        _quiescing = true;
        _serializer.StopObservation();
        await _reconnect.StopAsync().ConfigureAwait(false);
        return await _serializer.RunAsync(async () =>
        {
            if (!_active)
            {
                return new PluginStopResult { Status = PluginStopStatus.Clean };
            }

            await ReleaseControllerCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await RestoreAsync(CancellationToken.None, context.Deadline).ConfigureAwait(false);
            await ReleaseOemAsync().ConfigureAwait(false);
            await _hid!.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            _ec?.Dispose();
            _ec = null;
            _wmi?.Dispose();
            _wmi = null;
            _active = false;
            var pending = _journal?.OutstandingEntries.Count > 0;
            return new PluginStopResult
            {
                Status = pending || _hidFaulted ? PluginStopStatus.Unverified : PluginStopStatus.Clean,
                Reason = pending
                    ? new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        "Some Lenovo original state could not be verified or restored.")
                    : null
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serializer.StopObservation();
        await _reconnect.StopAsync().ConfigureAwait(false);
        await _serializer.RunAsync(async () =>
        {
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
            _active = false;
        }, CancellationToken.None).ConfigureAwait(false);
        _serializer.Dispose();
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
            _host!.Trace(DeviceTraceLevel.Warn, "legion-hid", ex.Message);
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
                _host!.Trace(DeviceTraceLevel.Warn, "legion-ec", ex.Message);
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
            _host!.Trace(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
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
                    _host?.Trace(DeviceTraceLevel.Warn, "legion-controller",
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

    private PluginStartResult StartResult()
    {
        return new PluginStartResult
        {
            State = _wmi?.FeaturesAvailable == true && _hid?.Connected == true &&
                    (!_controllerWanted || _hid.Reading) &&
                    (!_model!.EcFan || _ec is not null)
                ? PluginOperationalState.Active
                : PluginOperationalState.Degraded,
            Reason = _hid?.Connected != true
                ? new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                    "Lenovo HID collection unavailable; WMI controls remain independent.")
                : null
        };
    }

    private void BeginReconnect()
    {
        if (!_active || _quiescing || _disposed)
        {
            return;
        }

        _reconnect.Start(token => _serializer.RunAsync(async () =>
        {
            if (!_active || _quiescing || _disposed || _hidFaulted || _hid is null)
            {
                return true;
            }

            if (_hid.Reading)
            {
                return true;
            }

            var identity = _hardware.Identity();
            if (LegionGoModels.Match(identity) != _model ||
                LegionIdentity.Binding(identity) != LegionIdentity.Binding(_identity!))
            {
                return true;
            }

            await _hid.CloseAsync(token).ConfigureAwait(false);
            if (!await _hid.OpenAsync(_model!, token).ConfigureAwait(false))
            {
                return false;
            }

            _hidFaulted = true;
            await AcquireOemAsync(token).ConfigureAwait(false);
            _hidFaulted = false;
            if (_controllerWanted)
            {
                await AcquireControllerAsync(token).ConfigureAwait(false);
            }

            await PublishStatesAsync(token).ConfigureAwait(false);
            return true;
        }, token), ex => _host?.Trace(DeviceTraceLevel.Warn, "legion-reconnect", ex.Message));
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
                _host?.Trace(DeviceTraceLevel.Warn, "legion-oem", ex.Message);
            }
        }

        _oemBinding = null;
    }
}
