using System.Diagnostics;
using System.IO.Ports;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>Native vendor edges are distinct from the Xbox report and never fabricate ordinary gamepad buttons.</summary>
internal sealed class OneXOemSource(OneXProtocolProfile profile) : IProfiledOemSource
{
    private readonly Lock _gate = new();
    private readonly bool[] _held = new bool[5];
    private readonly HidReportSource _hidSource = new();
    private readonly OemButtonLatch[] _pulses = [new(), new(), new()];
    private readonly SemaphoreSlim _resend = new(0, 1);
    private Task? _configuration;
    private volatile bool _connected;

    private int _hidReportLength;
    private Action<string, CanonicalButtons, bool>? _publish;
    private Timer? _pulseTimer;
    private Task? _reader;
    private SerialPort? _serial;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;
    private OneXVendorTransport? _vendor;
    public bool IsConnected => _connected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _publish = publish;
        if (profile.SerialButtons)
        {
            _serial = TryOpenSerial(() => OneXSerial.Open(true));
        }

        if (_serial is null)
        {
            var v2 = profile.SerialButtons;
            var collection = HidDevices.Unique(HidDevices.Enumerate(v2 ? (ushort)0x1A2C : (ushort)0x1A86,
                v2 ? [0xB001] : [0xFE00, 0x1305]), item =>
                item.UsagePage == (v2 ? 0xFF01 : 0xFF00) && item.Usage == 1 && item.InputLength is 64 or 65);
            if (collection is null)
            {
                return ValueTask.FromResult(false);
            }

            _stream = HidDevices.OpenStream(collection);
            _hidReportLength = collection.InputLength;
        }

        _stop = new CancellationTokenSource();
        _pulseTimer = new Timer(_ => ReleaseExpiredPulses(), null, Timeout.Infinite, Timeout.Infinite);
        if (profile.VendorButtons && profile.HasRearButtons)
        {
            try
            {
                _vendor = OneXVendorTransport.Open(OneXLightingKind.HidV1);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _vendor = null;
            }

            if (_vendor is not null)
            {
                _configuration = Task.Run(() => ConfigureAsync(_stop.Token), CancellationToken.None);
            }
        }

        _connected = true;
        if (_serial is not null)
        {
            _reader = ReadAsync(_stop.Token);
        }
        else
        {
            _connected = _hidSource.Start(_stream!, _hidReportLength, ParseHid, ReaderStopped, token);
            _stream = null;
        }

        return ValueTask.FromResult(_connected);
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        _serial?.Dispose();
        await _hidSource.StopAsync(token).ConfigureAwait(false);
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        if (_configuration is not null)
        {
            await _configuration.WaitAsync(token).ConfigureAwait(false);
        }

        Timer? pulseTimer;
        lock (_gate)
        {
            pulseTimer = _pulseTimer;
            _pulseTimer = null;
        }

        if (pulseTimer is not null)
        {
            await pulseTimer.DisposeAsync().ConfigureAwait(false);
        }

        _vendor?.Dispose();
        _vendor = null;

        ReleaseAll();
        _connected = false;
        _reader = null;
        _configuration = null;
        while (_resend.Wait(0))
        {
        }

        _stop?.Dispose();
        _stop = null;
        _stream = null;
        _serial = null;
        _publish = null;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal static SerialPort? TryOpenSerial(Func<SerialPort?> open)
    {
        try
        {
            return open();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // HC falls back to HID when the selected serial port is busy or denied.
            return null;
        }
    }

    private async Task ConfigureAsync(CancellationToken token)
    {
        try
        {
            // HC waits for the controller firmware after opening the vendor collection.
            await Task.Delay(4000, token).ConfigureAwait(false);
            var frames = OneXCommandCodec.ControllerConfiguration(profile);
            while (true)
            {
                foreach (var frame in frames)
                {
                    _vendor!.Write(frame, token);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                await _resend.WaitAsync(token).ConfigureAwait(false);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed dispatch is uncertain. End this writer; status reports cannot retry it.
        }
    }

    private async Task ReadAsync(CancellationToken token)
    {
        var buffer = new byte[256];
        var serialFrame = new byte[14];
        var serialLength = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_serial is { } serial)
                {
                    var count = await serial.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var value = buffer[index];
                        if (serialLength == 1 && value != 0x3F)
                        {
                            serialFrame[0] = value;
                            continue;
                        }

                        serialFrame[serialLength++] = value;
                        if (serialLength != 14)
                        {
                            continue;
                        }

                        serialLength = 0;
                        if (serialFrame[0] == 0x1A && serialFrame[12] == 0x3F && serialFrame[13] == 0x1A)
                        {
                            Edge(serialFrame[2], serialFrame[8] == 1, true);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException or InvalidOperationException)
        {
        }
        finally
        {
            ReaderStopped();
        }
    }

    private void ReaderStopped()
    {
        _connected = false;
        _stop?.Cancel();
        ReleaseAll();
    }

    internal void ParseHid(ReadOnlySpan<byte> report)
    {
        var frame = report.Length == 65 && report[0] == 0 ? report[1..] : report;
        if (frame.Length < 14 || frame[1] != 0x3F || frame[^2] != 0x3F || frame[^1] != frame[0])
        {
            return;
        }

        if (RequestsConfiguration(profile, frame))
        {
            if (_configuration is { IsCompleted: false } && _resend.CurrentCount == 0)
            {
                _resend.Release();
            }

            return;
        }

        if (frame[0] != 0xB2)
        {
            return;
        }

        Edge(frame[6], frame[12] == 1, false);
    }

    internal static bool RequestsConfiguration(OneXProtocolProfile model, ReadOnlySpan<byte> frame)
    {
        return model.VendorButtons && model.HasRearButtons && frame.Length >= 6 && frame[0] == 0xB8
               && (model.SimpleVibration ? frame[3] == 0xFE : frame[5] == 0x20);
    }

    private void Edge(byte code, bool down, bool serial)
    {
        var index = DecodeButton(profile, code, serial);
        if (index < 0 || (serial && code == 0x21))
        {
            return;
        }

        lock (_gate)
        {
            var pulse = index < 3 && (profile.X2 || serial);
            if (pulse && down)
            {
                _pulses[index].Pulse(serial ? OemButtonLatch.HoldDuration : TimeSpan.FromMilliseconds(100));
                SchedulePulse();
            }

            if (pulse && !down)
            {
                return;
            }

            if (_held[index] == down)
            {
                return;
            }

            _held[index] = down;
            Publish(index, down);
        }
    }

    internal static int DecodeButton(OneXProtocolProfile model, byte code, bool serial = false)
    {
        var reverseRear = model.Apex || (!model.X2 && !serial);
        var index = code switch
        {
            0x20 => model.X2 ? 0 : -1, 0x21 => model.Mini ? 1 : 2,
            0x22 => reverseRear ? 4 : 3, 0x23 => reverseRear ? 3 : 4,
            0x24 => model.Three || model.Mini ? 2 : 1, _ => -1
        };
        return !model.HasRearButtons && index >= 3 ? -1 : index;
    }

    private void ReleaseExpiredPulses()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            for (var index = 0; index < _pulses.Length; index++)
            {
                if (_held[index] && _pulses[index].PulseDeadline != 0 && !_pulses[index].PulseHeld(now))
                {
                    _held[index] = false;
                    _pulses[index].Clear();
                    Publish(index, false);
                }
            }

            SchedulePulse();
        }
    }

    private void SchedulePulse()
    {
        var next = long.MaxValue;
        foreach (var pulse in _pulses)
        {
            var deadline = pulse.PulseDeadline;
            if (deadline != 0)
            {
                next = Math.Min(next, deadline);
            }
        }

        var delay = next == long.MaxValue
            ? Timeout.Infinite
            : (int)Math.Clamp(Math.Ceiling((next - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency), 1,
                int.MaxValue);
        _pulseTimer?.Change(delay, Timeout.Infinite);
    }

    private void Publish(int index, bool down)
    {
        _publish?.Invoke(index switch
        {
            0 => "turbo", 1 => "keyboard", 2 => "home", 3 => "l4", _ => "r4"
        }, index switch
        {
            0 => CanonicalButtons.QuickAccess, 2 => CanonicalButtons.Guide, 3 => CanonicalButtons.RearPaddle1,
            4 => CanonicalButtons.RearPaddle2, _ => CanonicalButtons.None
        }, down);
    }

    private void ReleaseAll()
    {
        lock (_gate)
        {
            for (var index = 0; index < _held.Length; index++)
            {
                if (_held[index])
                {
                    _held[index] = false;
                    Publish(index, false);
                }
            }

            foreach (var pulse in _pulses)
            {
                pulse.Clear();
            }

            SchedulePulse();
        }
    }
}
