using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Generic;

/// <summary>HHD's TECNO report 0x74 button bits translated directly to Windows HID input.</summary>
internal sealed class TecnoButtonSource : IProfiledOemSource
{
    private static readonly (string Id, string Label, int Byte, byte Mask, CanonicalButtons Button)[] Fields =
    [
        ("bottom-left", "Bottom left", 5, 1, CanonicalButtons.Guide),
        ("bottom-right", "Bottom right", 5, 2, CanonicalButtons.None),
        ("keyboard-switch", "Keyboard switch", 7, 32, CanonicalButtons.None)
    ];

    private volatile bool _connected;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = Array.AsReadOnly(Fields.Select(field =>
        new OemControlDescriptor
        {
            ControlId = field.Id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = field.Label },
            Placement = OemControlPlacement.Front,
            RequiresControllerAcquisition = true
        }).ToArray());

    public bool IsConnected => _connected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = HidDevices.Enumerate(0x2993, [0x2001]).Where(collection => collection.UsagePage == 0xFFA0 &&
            collection.Usage == 1
            && collection.InputLength >= 8 && !string.IsNullOrEmpty(collection.PhysicalLocation)).ToArray();
        if (candidates.Length != 1)
        {
            return ValueTask.FromResult(false);
        }

        _stream = HidDevices.OpenStream(candidates[0]);
        _stop = new CancellationTokenSource();
        _connected = true;
        _reader = ReadAsync(_stream, candidates[0].InputLength, publish, _stop.Token);
        return ValueTask.FromResult(true);
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        _reader = null;
        _stream = null;
        _stop?.Dispose();
        _stop = null;
        _connected = false;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal static byte Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length < 8 || report[0] != 0x74)
        {
            return 0;
        }

        byte result = 0;
        for (var index = 0; index < Fields.Length; index++)
        {
            if ((report[Fields[index].Byte] & Fields[index].Mask) != 0)
            {
                result |= (byte)(1 << index);
            }
        }

        return result;
    }

    private async Task ReadAsync(FileStream stream, int length, Action<string, CanonicalButtons, bool> publish,
        CancellationToken token)
    {
        var report = new byte[length];
        var previous = new bool[Fields.Length];
        try
        {
            while (await stream.ReadAsync(report, token).ConfigureAwait(false) == report.Length)
            {
                if (report[0] != 0x74)
                {
                    continue;
                }

                var current = Decode(report);
                for (var index = 0; index < Fields.Length; index++)
                {
                    var field = Fields[index];
                    var down = (current & (1 << index)) != 0;
                    if (down == previous[index])
                    {
                        continue;
                    }

                    previous[index] = down;
                    publish(field.Id, field.Button, down);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            for (var index = 0; index < Fields.Length; index++)
            {
                if (previous[index])
                {
                    publish(Fields[index].Id, Fields[index].Button, false);
                }
            }

            _connected = false;
        }
    }
}
