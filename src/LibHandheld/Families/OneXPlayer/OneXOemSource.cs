using System.Diagnostics;
using System.IO.Ports;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>Native vendor edges are distinct from the Xbox report and never fabricate ordinary gamepad buttons.</summary>
internal sealed class OneXOemSource(OneXProtocolProfile profile) : IProfiledOemSource
{
    private readonly Lock _gate = new();
    private readonly bool[] _held = new bool[5];
    private readonly long[] _pulseUntil = new long[5];
    private volatile bool _connected;
    private Action<string, CanonicalButtons, bool>? _publish;
    private Task? _pulseReader;
    private Task? _reader;
    private SerialPort? _serial;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;
    public bool IsConnected => _connected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _publish = publish;
        if (profile.SerialButtons)
        {
            _serial = OneXSerial.Open(true);
        }

        if (_serial is null)
        {
            var v2 = profile.SerialButtons;
            var collection = HidDevices.Enumerate(v2 ? (ushort)0x1A2C : (ushort)0x1A86,
                    v2 ? [0xB001] : [0xFE00, 0x1305])
                .FirstOrDefault(item =>
                    item.UsagePage == (v2 ? 0xFF01 : 0xFF00) && item.Usage == 1 && item.InputLength is 64 or 65);
            if (collection is null)
            {
                return ValueTask.FromResult(false);
            }

            _stream = HidDevices.OpenStream(collection);
        }

        _stop = new CancellationTokenSource();
        _connected = true;
        _reader = Task.Run(() => ReadAsync(_stop.Token), CancellationToken.None);
        _pulseReader = Task.Run(() => PulseAsync(_stop.Token), CancellationToken.None);
        return ValueTask.FromResult(true);
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        _serial?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        if (_pulseReader is not null)
        {
            await _pulseReader.WaitAsync(token).ConfigureAwait(false);
        }

        ReleaseAll();
        _connected = false;
        _reader = null;
        _pulseReader = null;
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
                    int count;
                    try
                    {
                        count = serial.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
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
                else
                {
                    var count = await _stream!.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    ParseHid(buffer.AsSpan(0, count));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException or InvalidOperationException)
        {
        }
        finally
        {
            _connected = false;
            ReleaseAll();
        }
    }

    internal void ParseHid(ReadOnlySpan<byte> report)
    {
        var frame = report.Length == 65 && report[0] == 0 ? report[1..] : report;
        if (frame.Length < 14 || frame[0] != 0xB2 || frame[1] != 0x3F || frame[^2] != 0x3F || frame[^1] != 0xB2)
        {
            return;
        }

        Edge(frame[6], frame[12] == 1, false);
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
                _pulseUntil[index] = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
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

    private async Task PulseAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    for (var index = 0; index < _pulseUntil.Length; index++)
                    {
                        if (_held[index] && _pulseUntil[index] != 0 && now >= _pulseUntil[index])
                        {
                            _held[index] = false;
                            _pulseUntil[index] = 0;
                            Publish(index, false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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

            Array.Clear(_pulseUntil);
        }
    }
}
