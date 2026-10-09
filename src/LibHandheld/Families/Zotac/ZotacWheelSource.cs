using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Zotac;

/// <summary>The separate four-byte wheel collection is independent of the Xbox controller reports.</summary>
internal sealed class ZotacWheelSource : IProfiledOemSource
{
    private static readonly (byte Mask, string Id, string Label)[] Wheels =
    [
        (16, "left-wheel-counterclockwise", "Left wheel counterclockwise"),
        (8, "left-wheel-clockwise", "Left wheel clockwise"),
        (2, "right-wheel-counterclockwise", "Right wheel counterclockwise"),
        (1, "right-wheel-clockwise", "Right wheel clockwise")
    ];

    private volatile bool _connected;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = Array.AsReadOnly(Wheels.Select(wheel =>
        new OemControlDescriptor
        {
            ControlId = wheel.Id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = wheel.Label },
            Placement = OemControlPlacement.Front, RequiresControllerAcquisition = true
        }).ToArray());

    public bool IsConnected => _connected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = HidDevices.Enumerate(0x1EE9, [0x1590]).Where(collection => collection.InputLength == 4
            && collection.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(collection.PhysicalLocation)).ToArray();
        if (candidates.Length != 1)
        {
            return ValueTask.FromResult(false);
        }

        _stream = HidDevices.OpenStream(candidates[0]);
        _stop = new CancellationTokenSource();
        _connected = true;
        _reader = ReadAsync(_stream, publish, _stop.Token);
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

    private async Task ReadAsync(FileStream stream, Action<string, CanonicalButtons, bool> publish,
        CancellationToken token)
    {
        var report = new byte[4];
        byte previous = 0;
        try
        {
            while (await stream.ReadAsync(report, token).ConfigureAwait(false) == report.Length)
            {
                var current = report[3];
                foreach (var wheel in Wheels)
                {
                    if (((current ^ previous) & wheel.Mask) != 0)
                    {
                        publish(wheel.Id, CanonicalButtons.None, (current & wheel.Mask) != 0);
                    }
                }

                previous = current;
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
            foreach (var wheel in Wheels)
            {
                if ((previous & wheel.Mask) != 0)
                {
                    publish(wheel.Id, CanonicalButtons.None, false);
                }
            }

            _connected = false;
        }
    }
}
