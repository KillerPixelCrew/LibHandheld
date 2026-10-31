using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Zotac;

/// <summary>The separate four-byte wheel collection is independent of the Xbox controller reports.</summary>
internal static class ZotacWheelSource
{
    private static readonly OemReportBit[] Wheels =
    [
        new("left-wheel-counterclockwise", "Left wheel counterclockwise", Byte: 3, Mask: 16, CanonicalButtons.None),
        new("left-wheel-clockwise", "Left wheel clockwise", Byte: 3, Mask: 8, CanonicalButtons.None),
        new("right-wheel-counterclockwise", "Right wheel counterclockwise", Byte: 3, Mask: 2, CanonicalButtons.None),
        new("right-wheel-clockwise", "Right wheel clockwise", Byte: 3, Mask: 1, CanonicalButtons.None)
    ];

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } = HidOemBitSource.Controls(Wheels);

    internal static HidOemBitSource Create()
    {
        // The collection reports exactly four bytes, so every read the four-byte reader accepts is a wheel report.
        return new HidOemBitSource(Wheels, static () => HidDevices.Unique(HidDevices.Enumerate(0x1EE9, [0x1590]),
                item => item.InputLength == 4
                        && item.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(item.PhysicalLocation)),
            minimumLength: 4, reportId: null);
    }
}
