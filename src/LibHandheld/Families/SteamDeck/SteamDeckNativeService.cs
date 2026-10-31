using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.SteamDeck;

internal sealed partial class SteamDeckNativeService(
    ProfiledFeatureContext context,
    Func<INeptuneDevice> openController,
    Func<ISteamDeckMemory>? openMemory = null,
    Func<VangoghPower>? openClocks = null) : ProfiledFeatureService("steamdeck-native")
{
    private static readonly HapticCapabilities Haptics = new()
    {
        LowFrequency = OutputChannelSupport.Native, HighFrequency = OutputChannelSupport.Native,
        NativePulse = OutputChannelSupport.Native,
        MaxFramesPerSecond = 100
    };

    private static readonly CapabilityDescriptor[] ControllerDescriptors =
    [
        Source(CapabilityIds.Controller, CapabilityRole.ControllerSource, "Controller source"),
        Source(CapabilityIds.Motion, CapabilityRole.MotionSource, "Motion sensors"),
        CapabilityDescriptors.Action(CapabilityIds.Rumble, CapabilityRole.HapticSink,
            CapabilityDescriptors.Label("Trackpad haptics"))
    ];

    internal const string RefreshMeasurementsId = "steamdeck.refresh-native-measurements";
    private static readonly CapabilityDescriptor RefreshMeasurements = CapabilityDescriptors.Action(
        RefreshMeasurementsId, CapabilityRole.GenericAction,
        CapabilityDescriptors.Label("Refresh native measurements"));

    private readonly StationaryGyroBiasCalibrator _gyroBias = new();
    private readonly Lock _outputGate = new();
    private readonly ManualResetEventSlim _rumbleStop = new(false);
    private readonly Dictionary<string, string> _pressIds = new(StringComparer.Ordinal);
    private volatile bool _connected;
    private INeptuneDevice? _controller;
    private string? _controllerIdentity;
    private CapabilityReason? _controllerReason;
    private DeviceRecoveryJournal? _journal;
    private bool _management = context.ControllerManagementEnabled;
    private CancellationTokenSource? _mappingCancellation;
    private Task? _mappingWatchdog;
    private bool _mappingsOwned;
    private long _pressSequence;
    private CanonicalButtons _previousButtons;
    private Thread? _reader;
    private volatile bool _reading;
    private Thread? _rumbleWorker;
    private HapticOutputFrame _rumbleFrame;
    private volatile bool _hapticFaulted;
    private CapabilityReason? _hapticReason;
    private bool _hapticsStopped;
    internal bool Connected => _connected;
    public override bool Suspendable => true;

    // Built on first use: the arrays live in other partial files, whose static initialisation order is unspecified.
    private static IReadOnlyList<CapabilityDescriptor>? _allDescriptors;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _allDescriptors ??=
        Array.AsReadOnly<CapabilityDescriptor>(
            [.. ControllerDescriptors, .. HardwareDescriptors, .. ClockDescriptors, RefreshMeasurements]);

    internal override IReadOnlyList<CapabilityState> States => ControllerDescriptors.Select(descriptor =>
        new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId,
            Available = _management && Connected && (descriptor.Role != CapabilityRole.HapticSink || !_hapticFaulted),
            Reason = descriptor.Role == CapabilityRole.HapticSink ? _hapticReason ?? _controllerReason : _controllerReason,
            ObservedValue = descriptor.Role == CapabilityRole.HapticSink
                ? null
                : CapabilityValue.Choice(Connected ? SourceOwnership.Managed : SourceOwnership.Unavailable),
            Quality = Connected ? HardwareStateQuality.Observed : HardwareStateQuality.Unknown,
            ObservedAt = Connected ? DateTimeOffset.UtcNow : null
        }).Concat(HardwareStates()).Concat(ClockStates()).Append(new CapabilityState
        {
            CapabilityId = RefreshMeasurementsId, Available = _memory is not null || _clocks is not null,
            Quality = HardwareStateQuality.Unknown
        }).ToArray();

    internal override ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Rumble(frame);
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _journal ??= context.Journal;
        await AcquireHardwareAsync(token).ConfigureAwait(false);
        AcquireClocks();
        if (_management)
        {
            await AcquireControllerAsync(token, explicitEnable: true).ConfigureAwait(false);
        }

        return Set(_memory is null && _clocks is null && !Connected ? DeviceServiceState.Passive
            : _memory is null || _clocks is null || (_management && !Connected) ? DeviceServiceState.Degraded
            : DeviceServiceState.Owned,
            _hardwareReason ?? _controllerReason);
    }

    private async ValueTask AcquireControllerAsync(CancellationToken token, bool explicitEnable = false)
    {
        if (_controller is not null)
        {
            return;
        }

        try
        {
            var device = openController();
            _controller = device;
            _controllerIdentity = context.Firmware + "|" + device.FirmwareIdentity;
            await _journal!.ReconcileAsync<SteamDeckOriginal>("neptune", _controllerIdentity,
                (_, ct) => RestoreControllerAsync(ct), token).ConfigureAwait(false);
            var original = new SteamDeckOriginal
            {
                Registers = [48],
                Values = [ReadOriginal(device, 48)]
            };
            await _journal.BeginAsync("neptune", _controllerIdentity, original, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _mappingsOwned = true;
            device.SetManagedMappings(true);

            // Neptune streams its IMU by default. Only change a readable IMU mode so it can be put back exactly.
            if (original.Values[0] is not null)
            {
                device.WriteSetting(48, 0x18);
            }

            _previousButtons = CanonicalButtons.None;
            _reading = true;
            _connected = true;
            _controllerReason = null;
            _rumbleFrame = HapticOutputFrame.Stop(DateTimeOffset.UtcNow);
            if (explicitEnable)
            {
                _hapticFaulted = false;
                _hapticReason = null;
            }
            _hapticsStopped = false;
            _rumbleStop.Reset();
            await context.Sink.PublishPhysicalDevicesAsync(device.Identities, Haptics, token).ConfigureAwait(false);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "LibHandheld Neptune" };
            _reader.Start();
            _rumbleWorker = new Thread(RumbleLoop) { IsBackground = true, Name = "LibHandheld Neptune haptics" };
            _rumbleWorker.Start();
            _mappingCancellation = new CancellationTokenSource();
            _mappingWatchdog = MaintainMappingsAsync(_mappingCancellation.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await ReleaseControllerAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _controllerReason = Missing(exception.Message);
            await ReleaseControllerAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ushort? ReadOriginal(INeptuneDevice device, byte register)
    {
        try
        {
            return device.ReadSetting(register);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task MaintainMappingsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                lock (_outputGate)
                {
                    if (!_reading || !_management || _controller is null)
                    {
                        return;
                    }

                    _controller.SetManagedMappings(true);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _controllerReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "Neptune mapping ownership could not be reasserted; the uncertain write was not retried.");
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[65];
        try
        {
            while (_reading && _controller is { } device)
            {
                var count = device.Read(buffer);
                if (!_reading)
                {
                    break;
                }

                if (!NeptuneReport.TryDecode(buffer.AsSpan(0, count), DateTimeOffset.UtcNow, out var sample))
                {
                    continue;
                }

                if (sample.Motion is { } motion)
                {
                    var corrected = _gyroBias.Correct(
                        MotionSampleBuilder.ClipSaturated(new Vector3(motion.GyroX, motion.GyroY, motion.GyroZ)),
                        new Vector3(motion.AccelX, motion.AccelY, motion.AccelZ));
                    sample = sample with
                    {
                        Motion = motion with { GyroX = corrected.X, GyroY = corrected.Y, GyroZ = corrected.Z }
                    };
                }

                context.Sink.PublishControllerSampleAsync(sample, CancellationToken.None).GetAwaiter().GetResult();
                PublishOemEdges(sample.Buttons);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_reading)
            {
                _controllerReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    "The Neptune controller disconnected.");
            }
        }
        finally
        {
            lock (_outputGate)
            {
                _connected = false;
                _rumbleStop.Set();
                StopHaptics();
            }

            try
            {
                PublishOemEdges(CanonicalButtons.None);
                context.Sink
                    .PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                        CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }
    }

    private void PublishOemEdges(CanonicalButtons current)
    {
        var changed = _previousButtons ^ current;
        _previousButtons = current;
        if ((changed & CanonicalButtons.Guide) != 0)
        {
            Publish("steam", (current & CanonicalButtons.Guide) != 0);
        }

        if ((changed & CanonicalButtons.QuickAccess) != 0)
        {
            Publish("quick-access", (current & CanonicalButtons.QuickAccess) != 0);
        }

        if ((changed & CanonicalButtons.RearPaddle1) != 0)
        {
            Publish("l4", (current & CanonicalButtons.RearPaddle1) != 0);
        }

        if ((changed & CanonicalButtons.RearPaddle2) != 0)
        {
            Publish("r4", (current & CanonicalButtons.RearPaddle2) != 0);
        }

        if ((changed & CanonicalButtons.RearPaddle3) != 0)
        {
            Publish("l5", (current & CanonicalButtons.RearPaddle3) != 0);
        }

        if ((changed & CanonicalButtons.RearPaddle4) != 0)
        {
            Publish("r5", (current & CanonicalButtons.RearPaddle4) != 0);
        }
    }

    private void Publish(string id, bool pressed)
    {
        if (pressed)
        {
            _pressIds[id] = $"{id}:{++_pressSequence}";
        }

        if (!_pressIds.TryGetValue(id, out var pressId))
        {
            return;
        }

        context.Sink.PublishOemEventAsync(new OemControlEvent(id, OemPressKind.Short, DateTimeOffset.UtcNow,
                pressId, pressed ? OemControlEdge.Pressed : OemControlEdge.Released), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    internal void Rumble(HapticOutputFrame frame)
    {
        lock (_outputGate)
        {
            if (!_reading || !_management || !_connected || _controller is null || _hapticFaulted)
            {
                return;
            }

            if (frame.NativePulse is null)
            {
                _rumbleFrame = frame;
            }

            SendHapticFrame(frame);
        }
    }

    private void RumbleLoop()
    {
        // The firmware renders short pulses; continuous game rumble requires HC's 10 ms repetition.
        while (!_rumbleStop.Wait(10))
        {
            lock (_outputGate)
            {
                if (!_reading || !_management || !_connected || _controller is null || _hapticFaulted)
                {
                    return;
                }

                if (!_rumbleFrame.IsSilent)
                {
                    SendHapticFrame(_rumbleFrame);
                }
            }
        }
    }

    private void SendHapticFrame(HapticOutputFrame frame)
    {
        try
        {
            _controller!.Rumble(frame);
            _hapticsStopped = frame.IsSilent;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _hapticReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "Physical haptic output failed; the uncertain output was not retried.");
            _hapticFaulted = true;
            _rumbleStop.Set();
        }
    }

    private void StopHaptics()
    {
        _rumbleFrame = HapticOutputFrame.Stop(DateTimeOffset.UtcNow);
        if (_controller is not null && !_hapticFaulted && !_hapticsStopped)
        {
            SendHapticFrame(_rumbleFrame);
        }
    }

    private async ValueTask StopInputAsync(CancellationToken token)
    {
        _reading = false;
        _rumbleStop.Set();
        if (_rumbleWorker is { IsAlive: true } rumbleWorker && !rumbleWorker.Join(3000))
        {
            throw new TimeoutException("The Neptune haptic worker has not retired; its handles remain owned.");
        }

        _rumbleWorker = null;
        _mappingCancellation?.Cancel();
        if (_mappingWatchdog is { } watchdog)
        {
            await watchdog.WaitAsync(token).ConfigureAwait(false);
            _mappingWatchdog = null;
            _mappingCancellation?.Dispose();
            _mappingCancellation = null;
        }

        var device = _controller;
        if (device is not null)
        {
            device.StopReading();
            if (_reader is { IsAlive: true } reader && !reader.Join(3000))
            {
                throw new TimeoutException("The Neptune reader has not retired; its handles remain owned.");
            }
        }
    }

    internal override async ValueTask DisposeAsync()
    {
        await StopInputAsync(CancellationToken.None).ConfigureAwait(false);
        _controller?.Dispose();
        _controller = null;
        _reader = null;
        _connected = false;
        _memory?.Dispose();
        _memory = null;
        _clocks?.Dispose();
        _clocks = null;
        _rumbleStop.Dispose();
    }

    private async ValueTask ReleaseControllerAsync(CancellationToken token)
    {
        await StopInputAsync(token).ConfigureAwait(false);
        var device = _controller;
        if (device is not null)
        {
            lock (_outputGate)
            {
                StopHaptics();
            }

            try
            {
                if (_mappingsOwned)
                {
                    device.SetManagedMappings(false);
                    _mappingsOwned = false;
                }

                await RestoreControllerAsync(token).ConfigureAwait(false);
            }
            finally
            {
                device.Dispose();
                _controller = null;
                _reader = null;
                _connected = false;
            }
        }

        await context.Sink
            .PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        await context.Sink.PublishPhysicalDevicesAsync([], null, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask RestoreControllerAsync(CancellationToken token)
    {
        if (_controller is null || _journal?.OriginalStateFor<SteamDeckOriginal>("neptune") is not { } original)
        {
            return;
        }

        if (_journal!.EntryFor<SteamDeckOriginal>("neptune")?.FirmwareIdentity != _controllerIdentity)
        {
            await _journal.RetireAsync("neptune", "firmware changed", token).ConfigureAwait(false);
            return;
        }

        var failed = false;
        for (var i = 0; i < original.Registers.Length; i++)
        {
            if (original.Values[i] is not { } value)
            {
                continue;
            }

            try
            {
                _controller.WriteSetting(original.Registers[i], value);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed = true;
            }
        }

        if (!failed)
        {
            await _journal.MarkRestoredAsync("neptune", token).ConfigureAwait(false);
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await ReleaseControllerAsync(token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await ReleaseHardwareAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _clocks?.Dispose();
                _clocks = null;
                _writtenClocks.Clear();
                _measuredClocks.Clear();
            }
        }

        return Set(_hapticFaulted || _journal?.Entries<SteamDeckOriginal>().Count > 0
            ? DeviceServiceState.ReleasedUnverified
            : DeviceServiceState.Idle, _hapticFaulted ? _hapticReason : null);
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        if (_management == enabled)
        {
            return;
        }

        await ReleaseControllerAsync(token).ConfigureAwait(false);
        _management = enabled;
        if (enabled)
        {
            await AcquireControllerAsync(token, true).ConfigureAwait(false);
        }
    }

    internal override ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Live clock queries measure frequencies; accepted clock limits and charge settings are not polled.
        RefreshHardware();
        RefreshClockMeasurements();
        return ValueTask.CompletedTask;
    }

    internal override async ValueTask RefreshAsync(CancellationToken token)
    {
        RefreshHardware();
        RefreshClockMeasurements();
        if (_management && !Connected)
        {
            await ReleaseControllerAsync(token).ConfigureAwait(false);
            await AcquireControllerAsync(token).ConfigureAwait(false);
        }
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (command.CapabilityId == RefreshMeasurementsId)
        {
            if (token.IsCancellationRequested || _memory is null && _clocks is null)
            {
                return ValueTask.FromResult(CommandResults.Rejected(command,
                    CapabilityReasonCode.PrerequisiteMissing, "Native measurement access is unavailable or cancelled."));
            }

            var charge = RefreshChargeMeasurement();
            var clocks = RefreshClockMeasurements();
            return ValueTask.FromResult(charge && clocks ? CommandResults.Applied(command)
                : CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                    "Some native measurements were unavailable; accepted settings are retained."));
        }

        return command.CapabilityId.StartsWith("steamdeck.", StringComparison.Ordinal)
            ? ExecuteClockAsync(command, token)
            : ExecuteHardwareAsync(command, token);
    }


    private static CapabilityDescriptor Source(string id, CapabilityRole role, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label(label),
            SupportsRead = true, Persistence = CapabilityPersistence.Volatile,
            Choices = CapabilityDescriptors.Choices(SourceOwnership.Choices)
        };
    }
}
