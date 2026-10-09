using System.Numerics;
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
            Display = Label("Trackpad haptics"), SupportsAction = true, Persistence = CapabilityPersistence.Volatile
        }
    ];

    private readonly StationaryGyroBiasCalibrator _gyroBias = new();
    private readonly Lock _outputGate = new();
    private readonly Dictionary<string, string> _pressIds = new(StringComparer.Ordinal);
    private volatile bool _connected;
    private INeptuneDevice? _controller;
    private string? _controllerIdentity;
    private CapabilityReason? _controllerReason;
    private SteamDeckRecovery? _journal;
    private bool _management = context.ControllerManagementEnabled;
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
                : CapabilityValue.Choice(Connected ? SourceOwnership.Plugin : SourceOwnership.Unavailable),
            Quality = Connected ? HardwareStateQuality.Observed : HardwareStateQuality.Unknown,
            ObservedAt = Connected ? DateTimeOffset.UtcNow : null, DescriptorGeneration = 0, CycleGeneration = 0
        }).Concat(HardwareStates()).Concat(ClockStates()).ToArray();

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _journal ??= await SteamDeckRecovery.Open(context.StateDirectory, token).ConfigureAwait(false);
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
            var alreadyManaged = false;
            if (_journal!.EntryFor("neptune") is { } recovery)
            {
                if (recovery.FirmwareIdentity != _controllerIdentity)
                {
                    throw new InvalidOperationException(
                        "Pending controller state belongs to another firmware or physical device.");
                }

                if (recovery.Status != DeviceRecoveryStatus.Pending)
                {
                    if (OriginalControllerObserved(device, recovery.OriginalState))
                    {
                        await _journal.SetStatusAsync("neptune", DeviceRecoveryStatus.RestoredVerified, token)
                            .ConfigureAwait(false);
                    }
                    else if (ReadOriginal(device, 9) == 0 && ReadOriginal(device, 71) == 0
                                                          && (recovery.OriginalState.Values[2] is null ||
                                                              ReadOriginal(device, 48) == 0x18))
                    {
                        // Re-read resolves an uncertain handoff: adopt the still-active mode without repeating its writes.
                        await _journal.BeginAsync("neptune", _controllerIdentity, recovery.OriginalState, token)
                            .ConfigureAwait(false);
                        alreadyManaged = true;
                    }
                    else if (explicitEnable)
                    {
                        await _journal.BeginAsync("neptune", _controllerIdentity, recovery.OriginalState, token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "A previous controller restoration was uncertain; turn controller management off and on to reconcile it explicitly.");
                    }
                }
                else
                {
                    await RestoreControllerAsync(token).ConfigureAwait(false);
                    if (_journal.EntryFor("neptune") is not null && !explicitEnable)
                    {
                        throw new InvalidOperationException(
                            "Controller originals could not all be restored and verified.");
                    }
                }
            }

            var original = new SteamDeckOriginal
            {
                Registers = [9, 71, 48],
                Values = [ReadOriginal(device, 9), ReadOriginal(device, 71), ReadOriginal(device, 48)]
            };
            await _journal.BeginAsync("neptune", _controllerIdentity, original, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Suppress firmware keyboard/mouse output without clearing or resetting the user's digital mappings.
            if (!alreadyManaged)
            {
                device.WriteSetting(9, 0);
                device.WriteSetting(71, 0);
            }

            // Neptune streams its IMU by default. Only change a readable IMU mode so it can be put back exactly.
            if (!alreadyManaged && original.Values[2] is not null)
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

    private static bool OriginalControllerObserved(INeptuneDevice device, SteamDeckOriginal original)
    {
        if (original.Values[0] is null || original.Values[1] is null)
        {
            return false;
        }

        for (var i = 0; i < original.Registers.Length; i++)
        {
            if (original.Values[i] is { } value && ReadOriginal(device, original.Registers[i]) != value)
            {
                return false;
            }
        }

        return true;
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
            _pressIds[id] = $"{context.Sink.CycleGeneration}:{id}:{++_pressSequence}";
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

    private async ValueTask ReleaseControllerAsync(CancellationToken token)
    {
        _reading = false;
        var device = _controller;
        if (device is not null)
        {
            device.StopReading();
            if (_reader is { IsAlive: true } reader && !reader.Join(3000))
            {
                throw new TimeoutException("The Neptune reader has not retired; its handles remain owned.");
            }

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
        if (_controller is null || _journal?.PendingOriginalFor("neptune") is not { } original)
        {
            return;
        }

        var verified = true;
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

                    verified = false;
                }

                continue;
            }

            try
            {
                _controller.WriteSetting(original.Registers[i], value);
                verified &= _controller.ReadSetting(original.Registers[i]) == value;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed = true;
            }
        }

        await _journal.SetStatusAsync("neptune", failed ? DeviceRecoveryStatus.RestoreFailed
                : verified ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified, token)
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

        return Set(_journal?.OutstandingEntries.Count > 0
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

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }

    private static CapabilityDescriptor Source(string id, CapabilityRole role, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Choice, Display = Label(label),
            SupportsRead = true, Persistence = CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(SourceOwnership.Choices
                .Select(value => new CapabilityChoice(value, Label(value))).ToArray())
        };
    }
}
