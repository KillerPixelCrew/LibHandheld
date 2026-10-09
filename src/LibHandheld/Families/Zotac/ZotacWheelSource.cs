using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
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

    private readonly HidReportSource _source = new();
    private byte _held;
    private Action<string, CanonicalButtons, bool>? _publish;

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = Wheels.Select(wheel =>
        new OemControlDescriptor
        {
            ControlId = wheel.Id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = wheel.Label },
            Placement = OemControlPlacement.Front, RequiresControllerAcquisition = true
        }).ToArray();

    public bool IsConnected => _source.IsConnected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x1EE9, [0x1590]), item => item.InputLength == 4
            && item.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(item.PhysicalLocation));
        if (collection is null)
        {
            return ValueTask.FromResult(false);
        }

        _publish = publish;
        return ValueTask.FromResult(_source.Start(HidDevices.OpenStream(collection), 4, HandleReport,
            () => Emit(0), token));
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        await _source.StopAsync(token).ConfigureAwait(false);
        _publish = null;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (report.Length == 4)
        {
            Emit(report[3]);
        }
    }

    private void Emit(byte current)
    {
        foreach (var wheel in Wheels)
        {
            if (((current ^ _held) & wheel.Mask) != 0)
            {
                _publish?.Invoke(wheel.Id, CanonicalButtons.None, (current & wheel.Mask) != 0);
            }
        }

        _held = current;
    }
}
