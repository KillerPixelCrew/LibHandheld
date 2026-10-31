using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.LegionGo;

internal interface ILegionHid : IAsyncDisposable
{
    bool Connected { get; }
    bool Reading { get; }
    bool Listening { get; }
    bool RumbleAvailable { get; }
    string? Binding { get; }
    bool RightGyro { get; set; }
    bool NativeTouchpad { get; set; }
    event Action? Disconnected;
    ValueTask<bool> OpenAsync(LegionGoModel model, CancellationToken cancellationToken);
    ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken);
    ValueTask StartAsync(IDeviceSink sink, CancellationToken cancellationToken);
    ValueTask ListenAsync(IDeviceSink sink, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    ValueTask CloseAsync(CancellationToken cancellationToken);
    ValueTask RumbleAsync(HapticOutputFrame output, CancellationToken cancellationToken);
}

/// <summary>Command collection and controller acquisition are independent; releasing input retains lighting.</summary>
internal sealed partial class WindowsLegionHid : ILegionHid
{
    /// <summary>HC's Legion DirectInput button indices.</summary>
    private static readonly CanonicalButtons[] DInputButtons =
    [
        CanonicalButtons.A,
        CanonicalButtons.B,
        CanonicalButtons.None,
        CanonicalButtons.X,
        CanonicalButtons.Y,
        CanonicalButtons.None,
        CanonicalButtons.LeftShoulder,
        CanonicalButtons.RightShoulder,
        CanonicalButtons.None,
        CanonicalButtons.None,
        CanonicalButtons.View,
        CanonicalButtons.Menu,
        CanonicalButtons.None,
        CanonicalButtons.LeftStick,
        CanonicalButtons.RightStick
    ];

    private readonly HidReportSource _gamepadSource = new();
    private readonly Lock _output = new();
    private readonly long[] _pressIds = new long[14];
    private readonly SemaphoreSlim _publishing = new(1, 1);
    private readonly Lock _samples = new();
    private readonly LegionTouchState _touch = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private HidCollection? _commandCollection;
    private SafeFileHandle? _commands;
    private HidUsageReader? _dinput;
    private volatile bool _forward;
    private FileStream? _gamepad;
    private CanonicalControllerSample _gamepadSample;
    private FileStream? _input;
    private LegionGoModel? _model;
    private volatile bool _nativeTouchpad;
    private long _nextSlotDiscovery;
    private ushort _oem;
    private long _pressSequence;
    private Task? _reader;
    private CancellationTokenSource? _readerCancellation;
    private volatile bool _rightGyro;
    private IDeviceSink? _sink;
    private int _slot = -1;
    public event Action? Disconnected;

    public bool Connected => _commands is not null;
    public bool Listening => _reader is { IsCompleted: false };
    public bool Reading => _forward && Listening;
    public bool RumbleAvailable => _slot >= 0;

    public string? Binding => _commandCollection is { } collection
        ? $"{collection.PhysicalLocation}|{collection.ProductId:X4}|{collection.ReleaseNumber:X4}"
        : null;

    public bool RightGyro
    {
        get => _rightGyro;
        set => _rightGyro = value;
    }

    public bool NativeTouchpad
    {
        get => _nativeTouchpad;
        set => _nativeTouchpad = value;
    }

    public ValueTask<bool> OpenAsync(LegionGoModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _model = model;
        if (_commands is not null)
        {
            return ValueTask.FromResult(true);
        }

        var candidates = HidDevices.Enumerate(model.VendorId, model.ProductIds.ToArray());
        var commands = candidates.Where(c => model.Slim
            ? c.InputLength == 65 && c.OutputLength == 65
            : c.UsagePage == 0xFFA0 && c.Usage == 1 && c.OutputLength >= 13).ToArray();
        _commandCollection = HidDevices.Unique(commands, _ => true);
        if (_commandCollection is null || string.IsNullOrWhiteSpace(_commandCollection.PhysicalLocation) ||
            commands.Any(c =>
                !HidDevices.SamePhysicalLocation(c.PhysicalLocation, _commandCollection.PhysicalLocation)))
        {
            return ValueTask.FromResult(false);
        }

        _commands = HidDevices.Open(_commandCollection, false);
        _slot = -1;
        return ValueTask.FromResult(true);
    }

    public async ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_commands is null || _commandCollection is null)
            {
                throw new IOException("The Lenovo command collection is unavailable.");
            }

            // One dispatch only. A failed write is never repeated by this transport.
            HidDevices.WriteOutput(_commands, _commandCollection, report);
        }
        finally
        {
            _writes.Release();
        }
    }

    public ValueTask StartAsync(IDeviceSink sink, CancellationToken cancellationToken)
    {
        return StartCoreAsync(sink, true, cancellationToken);
    }

    public ValueTask ListenAsync(IDeviceSink sink, CancellationToken cancellationToken)
    {
        return StartCoreAsync(sink, false, cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _forward = false;
        await _gamepadSource.StopAsync(CancellationToken.None).ConfigureAwait(false);
        // The source owns the stream once its reader starts; one opened by a start that failed earlier closes here.
        _gamepad?.Dispose();
        _gamepad = null;
        _dinput?.Dispose();
        _dinput = null;
        lock (_samples)
        {
            _gamepadSample = default;
        }

        _touch.Reset();
        lock (_output)
        {
            if (_slot >= 0)
            {
                try
                {
                    LegionXInput.Rumble(_slot, HapticOutputFrame.Stop(DateTimeOffset.UtcNow));
                }
                catch (IOException)
                {
                }
            }

            _slot = -1;
        }

        await _publishing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_sink is not null)
            {
                await _sink.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
                await _sink.PublishPhysicalDevicesAsync([], null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _publishing.Release();
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StopReaderAsync().ConfigureAwait(false);
        await _writes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _commands?.Dispose();
            _commands = null;
            _commandCollection = null;
        }
        finally
        {
            _writes.Release();
        }
    }

    public ValueTask RumbleAsync(HapticOutputFrame output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_output)
        {
            if (_readerCancellation?.IsCancellationRequested == false && Reading && _model is not null &&
                _slot >= 0)
            {
                try
                {
                    LegionXInput.Rumble(_slot, output);
                }
                catch
                {
                    _slot = -1;
                    throw;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _writes.Dispose();
        _publishing.Dispose();
    }

    private async ValueTask StartCoreAsync(IDeviceSink sink, bool forward, CancellationToken cancellationToken)
    {
        var model = _model ?? throw new InvalidOperationException("No Lenovo model is bound.");
        if (!Connected)
        {
            throw new IOException("The Lenovo command collection is unavailable.");
        }

        var candidates = HidDevices.Enumerate(model.VendorId, model.ProductIds.ToArray());
        var source = HidDevices.Unique(candidates, c => (model.Slim
                                                            ? c.InputLength == 33
                                                            : c.UsagePage == 0xFFA0 && c.Usage == 1 &&
                                                              c.InputLength >= 60) &&
                                                        HidDevices.SamePhysicalLocation(c.PhysicalLocation,
                                                            _commandCollection!.PhysicalLocation));
        if (source is null)
        {
            throw new IOException("The Lenovo controller report collection is unavailable.");
        }

        _sink = sink;
        if (Listening && !forward)
        {
            return;
        }

        if (!Listening)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            await StopReaderAsync().ConfigureAwait(false);
            _touch.ConfigureTiming(ReadGestureMilliseconds(), ReadGestureMilliseconds());
            _input = HidDevices.OpenStream(source);
            _readerCancellation = new CancellationTokenSource();
        }

        _forward = forward;
        lock (_output)
        {
            _slot = forward ? LegionXInput.Find(model) : -1;
        }

        // The separate Generic Desktop collection is decoded using Windows' descriptor parser.
        var gamepad = forward && _slot < 0
            ? HidDevices.Unique(candidates, c => c.UsagePage == 1 && c.Usage is 4 or 5 &&
                                                 !string.IsNullOrWhiteSpace(c.PhysicalLocation) &&
                                                 HidDevices.SamePhysicalLocation(c.PhysicalLocation,
                                                     source.PhysicalLocation))
            : null;
        if (gamepad is not null)
        {
            _gamepad = HidDevices.OpenStream(gamepad);
            _dinput = new HidUsageReader(_gamepad.SafeFileHandle);
        }

        // Only controller nodes from this composite device are eligible for hiding; never hide the command/IMU channel.
        // Every mode Windows has seen the pad in is hidden, so switching the native mode never shows the pad.
        PhysicalDeviceIdentity[] identities = forward
            ? [.. HidDevices.KnownControllerNodes(model.VendorId, model.ProductIds.ToArray(), source.PhysicalLocation)]
            : [];
        if (gamepad is not null)
        {
            identities =
            [
                .. identities, PhysicalIdentities.FromCollection(gamepad)
            ];
        }

        if (forward && _slot >= 0 && identities.Length == 0)
        {
            throw new IOException("The Lenovo XInput slot has no matching physical controller identity for hiding.");
        }

        await sink.PublishPhysicalDevicesAsync(identities, _slot >= 0
            ? new HapticCapabilities
            {
                LowFrequency = OutputChannelSupport.Native, HighFrequency = OutputChannelSupport.Native
            }
            : null, cancellationToken).ConfigureAwait(false);
        var token = _readerCancellation!.Token;
        if (gamepad is not null)
        {
            _gamepadSource.Start(_gamepad!, gamepad.InputLength, DecodeGamepad, ClearGamepad, token);
            _gamepad = null;
        }

        if (!Listening)
        {
            _reader = ReadAsync(model, source, sink, token);
        }
    }

    private static uint ReadGestureMilliseconds()
    {
        var duration = 500u;
        // HC uses SPI_GETMOUSEHOVERTIME for both tablet gesture delays.
        return SystemParametersInfo(action: 0x006A, parameter: 0, value: ref duration, flags: 0) ? duration : 500u;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint flags);

    private async ValueTask StopReaderAsync()
    {
        _readerCancellation?.Cancel();
        _input?.Dispose();
        if (_reader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }

        _reader = null;
        _input = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
    }

    private bool TryReadXInput(LegionGoModel model, DateTimeOffset now, out CanonicalControllerSample sample)
    {
        if (LegionXInput.TryRead(_slot, now, out sample))
        {
            return true;
        }

        // A slot is validated once on acquisition. Only a failed/missing read asks discovery again,
        // throttled while the device's XInput interface is absent.
        var tick = Environment.TickCount64;
        if (tick < _nextSlotDiscovery)
        {
            return false;
        }

        _nextSlotDiscovery = tick + 500;
        _slot = LegionXInput.Find(model);
        return LegionXInput.TryRead(_slot, now, out sample);
    }

    private async Task ReadAsync(LegionGoModel model, HidCollection source, IDeviceSink sink, CancellationToken token)
    {
        var report = new byte[source.InputLength];
        var nativeDisconnected = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _input!.ReadAsync(report, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    nativeDisconnected = true;
                    throw;
                }

                if (read == 0)
                {
                    nativeDisconnected = true;
                    throw new IOException("The Lenovo controller disconnected.");
                }

                var now = DateTimeOffset.UtcNow;
                // hidapi strips Windows' leading zero from unnumbered input. HC's Go S offsets refer to that payload.
                var offset = model.Slim && report[0] == 0 ? 1 : 0;
                if (!LegionInputCodec.TryDecode(model, report.AsSpan(offset, read - offset), now, RightGyro,
                        NativeTouchpad, out var decoded))
                {
                    continue;
                }

                var sample = decoded.Sample;
                if (_forward && TryReadXInput(model, now, out var xinput))
                {
                    sample = LegionInputCodec.Merge(xinput, sample);
                }
                else if (_dinput is not null)
                {
                    CanonicalControllerSample direct;
                    lock (_samples)
                    {
                        direct = _gamepadSample;
                    }

                    if (direct.Timestamp != default)
                    {
                        sample = LegionInputCodec.Merge(direct with
                        {
                            Timestamp = now,
                            LeftTrigger = model.Slim ? direct.LeftTrigger : sample.LeftTrigger,
                            RightTrigger = model.Slim ? direct.RightTrigger : sample.RightTrigger
                        }, sample);
                    }
                }

                if (_forward && !model.Slim && !NativeTouchpad)
                {
                    sample = _touch.Apply(sample);
                }
                else
                {
                    _touch.Reset();
                }

                var oem = (ushort)(_forward
                    ? decoded.OemButtons | (_touch.SecondaryHeld ? 8192 : 0)
                    : decoded.OemButtons & 3);
                await PublishOemAsync(oem, now, sink, token).ConfigureAwait(false);
                if (_forward)
                {
                    await _publishing.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (_forward)
                        {
                            await sink.PublishControllerSampleAsync(sample, token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _publishing.Release();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
                // A disconnect is a resource degradation; the owning observation turn performs a fresh acquisition.
            }
        }
        finally
        {
            try
            {
                await PublishOemAsync(0, DateTimeOffset.UtcNow, sink, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
            }

            try
            {
                await sink.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
            }

            try
            {
                await sink.PublishPhysicalDevicesAsync([], null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-controller", ex.Message);
            }

            if (nativeDisconnected && !token.IsCancellationRequested)
            {
                Disconnected?.Invoke();
            }
        }
    }

    private void DecodeGamepad(ReadOnlySpan<byte> report)
    {
        if (_dinput!.TryDecodeGamepad(report, DateTimeOffset.UtcNow, DInputButtons, CanonicalButtons.None,
                out var sample))
        {
            lock (_samples)
            {
                _gamepadSample = sample;
            }
        }
    }

    private void ClearGamepad()
    {
        lock (_samples)
        {
            _gamepadSample = default;
        }
    }

    private async ValueTask PublishOemAsync(ushort current, DateTimeOffset now, IDeviceSink sink,
        CancellationToken token)
    {
        var changed = current ^ _oem;
        _oem = current;
        for (var index = 0; index < LegionControls.Ids.Length; index++)
        {
            var bit = 1 << index;
            if ((changed & bit) == 0)
            {
                continue;
            }

            var pressed = (current & bit) != 0;
            if (pressed)
            {
                _pressIds[index] = ++_pressSequence;
            }

            await sink.PublishOemEventAsync(new OemControlEvent(LegionControls.Ids[index], OemPressKind.Short, now,
                        $"legion:{index}:{_pressIds[index]}",
                        pressed ? OemControlEdge.Pressed : OemControlEdge.Released),
                    token)
                .ConfigureAwait(false);
        }
    }
}

internal sealed class LegionTouchState
{
    private bool _double;
    private TimeSpan _doubleTapDelay;
    private DateTimeOffset _down;
    private DateTimeOffset _lastUp;
    private float _lastX;
    private float _lastY;
    private bool _long;
    private TimeSpan _longTapDelay;
    private bool _pending;
    private float _startX;
    private float _startY;
    private bool _touching;
    public bool SecondaryHeld => _long && _touching;

    public LegionTouchState(uint doubleTapMilliseconds = 500, uint longTapMilliseconds = 500)
    {
        ConfigureTiming(doubleTapMilliseconds, longTapMilliseconds);
    }

    internal void ConfigureTiming(uint doubleTapMilliseconds, uint longTapMilliseconds)
    {
        _doubleTapDelay = TimeSpan.FromMilliseconds(doubleTapMilliseconds > 0 ? doubleTapMilliseconds : 300);
        _longTapDelay = TimeSpan.FromMilliseconds(longTapMilliseconds > 0 ? longTapMilliseconds : 500);
    }

    public CanonicalControllerSample Apply(CanonicalControllerSample sample)
    {
        var touched = (sample.Buttons & CanonicalButtons.RightPadTouch) != 0;
        var click = false;
        if (touched && !_touching)
        {
            _double = _pending && sample.Timestamp - _lastUp <= _doubleTapDelay &&
                      Distance(sample.RightPadX, sample.RightPadY, _lastX, _lastY) <= 0.48f;
            _pending = false;
            _long = false;
            _down = sample.Timestamp;
            _startX = sample.RightPadX;
            _startY = sample.RightPadY;
        }

        if (touched)
        {
            _lastX = sample.RightPadX;
            _lastY = sample.RightPadY;
            _long |= !_double && sample.Timestamp - _down >= _longTapDelay &&
                     Distance(_lastX, _lastY, _startX, _startY) <= 0.1f;
            click = _double;
        }
        else if (_touching)
        {
            _pending = !_double && !_long;
            _lastUp = sample.Timestamp;
            _double = false;
            _long = false;
        }

        if (!touched && _pending && sample.Timestamp - _lastUp > _doubleTapDelay)
        {
            click = Distance(_lastX, _lastY, _startX, _startY) <= 0.1f;
            _pending = false;
        }

        _touching = touched;
        var buttons = sample.Buttons;
        if (_long)
        {
            buttons &= ~CanonicalButtons.RightPadTouch;
        }

        return sample with
        {
            Buttons = buttons | (click ? CanonicalButtons.RightPadClick : 0),
            RightPadForce = _long ? 0 : sample.RightPadForce
        };
    }

    public void Reset()
    {
        _touching = false;
        _pending = false;
        _double = false;
        _long = false;
    }

    private static float Distance(float x, float y, float a, float b)
    {
        return MathF.Sqrt((x - a) * (x - a) + (y - b) * (y - b));
    }
}

internal static class LegionControls
{
    public static readonly string[] Ids =
    [
        "legion-r", "legion-l", "y1", "y2", "m3", "y3", "m1", "m2", "page", "desktop", "wheel-click", "wheel-up",
        "wheel-down", "touchpad-secondary-click"
    ];
}
