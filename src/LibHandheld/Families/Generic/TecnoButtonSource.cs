using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
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

    private readonly HidReportSource _source = new();
    private byte _held;
    private Action<string, CanonicalButtons, bool>? _publish;

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = Fields.Select(field =>
        new OemControlDescriptor
        {
            ControlId = field.Id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = field.Label },
            Placement = OemControlPlacement.Front, RequiresControllerAcquisition = true,
            ControllerButton = field.Button
        }).ToArray();

    public bool IsConnected => _source.IsConnected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x2993, [0x2001]), item =>
            item.UsagePage == 0xFFA0 && item.Usage == 1 && item.InputLength >= 8
            && !string.IsNullOrEmpty(item.PhysicalLocation));
        if (collection is null)
        {
            return ValueTask.FromResult(false);
        }

        _publish = publish;
        return ValueTask.FromResult(_source.Start(HidDevices.OpenStream(collection), collection.InputLength,
            HandleReport, () => Emit(0), token));
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

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 8 && report[0] == 0x74)
        {
            Emit(Decode(report));
        }
    }

    private void Emit(byte current)
    {
        for (var index = 0; index < Fields.Length; index++)
        {
            var mask = 1 << index;
            if (((current ^ _held) & mask) != 0)
            {
                _publish?.Invoke(Fields[index].Id, Fields[index].Button, (current & mask) != 0);
            }
        }

        _held = current;
    }
}
