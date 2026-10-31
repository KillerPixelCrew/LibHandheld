using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Generic;

/// <summary>HHD's TECNO report 0x74 button bits translated directly to Windows HID input.</summary>
internal static class TecnoButtonSource
{
    private static readonly OemReportBit[] Bits =
    [
        new("bottom-left", "Bottom left", Byte: 5, Mask: 1, CanonicalButtons.Guide),
        new("bottom-right", "Bottom right", Byte: 5, Mask: 2, CanonicalButtons.None),
        new("keyboard-switch", "Keyboard switch", Byte: 7, Mask: 32, CanonicalButtons.None)
    ];

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = HidOemBitSource.Controls(Bits);

    internal static HidOemBitSource Create()
    {
        return new HidOemBitSource(Bits, static () => HidDevices.Unique(HidDevices.Enumerate(0x2993, [0x2001]),
                item => item.UsagePage == 0xFFA0 && item.Usage == 1 && item.InputLength >= 8
                        && !string.IsNullOrEmpty(item.PhysicalLocation)),
            minimumLength: 8, reportId: 0x74);
    }
}
