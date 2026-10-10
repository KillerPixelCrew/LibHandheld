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
        MaxFramesPerSecond = 100
    };

    private static readonly CapabilityDescriptor[] ControllerDescriptors =
    [
        Source(CapabilityIds.Controller, CapabilityRole.ControllerSource, "Controller source"),
        Source(CapabilityIds.Motion, CapabilityRole.MotionSource, "Motion sensors"),
        new()
        {
            CapabilityId = CapabilityIds.Rumble, Role = CapabilityRole.HapticSink, ValueKind = CapabilityValueKind.None,
            Display = CapabilityDescriptors.Label("Trackpad haptics"), SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile
        }
    ];

    private readonly StationaryGyroBiasCalibrator _gyroBias = new();
    private readonly Lock _outputGate = new();
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
    internal bool Connected => _connected;
    public override bool Suspendable => true;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
        Array.AsReadOnly(ControllerDescriptors.Concat(HardwareDescriptors).Concat(ClockDescriptors).ToArray());

    internal override IReadOnlyList<CapabilityState> States => ControllerDescriptors.Select(descriptor =>
        new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, Available = _management && Connected, Reason = _controllerReason,
            ObservedValue = descriptor.Role == CapabilityRole.HapticSink
                ? null
                : CapabilityValue.Choice(Connected ? SourceOwnership.Managed : SourceOwnership.Unavailable),
            Quality = Connected ? HardwareStateQuality.Observed : HardwareStateQuality.Unknown,
            ObservedAt = Connected ? DateTimeOffset.UtcNow : null
        }).Concat(HardwareStates()).Concat(ClockStates()).ToArray();

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
            await AcquireControllerAsync(token).ConfigureAwait(false);
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
            await context.Sink.PublishPhysicalDevicesAsync(device.Identities, Haptics, token).ConfigureAwait(false);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "LibHandheld Neptune" };
            _reader.Start();
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
            _connected = false;
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
            if (!_management || !_connected || _controller is null)
            {
                return;
            }

            try
            {
                _controller.Rumble(frame);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _connected = false;
                _controllerReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    "Physical haptic output failed; the output was not retried.");
            }
        }
    }

    private async ValueTask StopInputAsync(CancellationToken token)
    {
        _reading = false;
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
    }

    private async ValueTask ReleaseControllerAsync(CancellationToken token)
    {
        await StopInputAsync(token).ConfigureAwait(false);
        var device = _controller;
        if (device is not null)
        {
            lock (_outputGate)
            {
                try
                {
                    device.Rumble(HapticOutputFrame.Stop(DateTimeOffset.UtcNow));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _controllerReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        "The haptic stop could not be confirmed.");
                }
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
        if (_controller is null || _journal?.PendingOriginalFor<SteamDeckOriginal>("neptune") is not { } original)
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
                // These two firmware switches have HC's documented native desktop baseline; custom mappings are untouched.
                if (original.Registers[i] is 9 or 71)
                {
                    try
                    {
                        _controller.WriteSetting(original.Registers[i], 1);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        failed = true;
                    }
                }

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

        await _journal.SetStatusAsync("neptune", failed
                ? DeviceRecoveryStatus.RestoreFailed
                : DeviceRecoveryStatus.RestoredUnverified, token)
            .ConfigureAwait(false);
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
            }
        }

        return Set(_journal?.Entries<SteamDeckOriginal>().Count > 0
            ? DeviceServiceState.ReleasedUnverified
            : DeviceServiceState.Idle);
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
        // Read measured fan RPM and battery temperature only; controller reconnect stays on the slow pass.
        RefreshHardware();
        return ValueTask.CompletedTask;
    }

    internal override async ValueTask RefreshAsync(CancellationToken token)
    {
        RefreshHardware();
        if (_management && !Connected)
        {
            await ReleaseControllerAsync(token).ConfigureAwait(false);
            await AcquireControllerAsync(token).ConfigureAwait(false);
        }
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
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
            Choices = Array.AsReadOnly(SourceOwnership.Choices
                .Select(value => new CapabilityChoice(value, CapabilityDescriptors.Label(value))).ToArray())
        };
    }
}
