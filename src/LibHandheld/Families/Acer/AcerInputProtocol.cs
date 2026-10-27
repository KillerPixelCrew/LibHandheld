using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Input shapes captured on PA08-I51, USB 0502:F901, device release 0004.</summary>
internal static class AcerInputProtocol
{
    internal static bool IsVendorCollection(HidCollection collection)
    {
        return IsAtlasCollection(collection, "mi_01") && collection.UsagePage == 0xFFF1
               && collection.Usage == 0xF5 && collection.InputLength == 16;
    }

    internal static bool IsQamCollection(HidCollection collection)
    {
        return IsAtlasCollection(collection, "mi_02") && collection.UsagePage == 0x88
               && collection.Usage == 1 && collection.InputLength == 3;
    }

    private static bool IsAtlasCollection(HidCollection collection, string usbInterface)
    {
        return collection.VendorId == 0x0502 && collection.ProductId == 0xF901
               && collection.ReleaseNumber == 4
               && collection.DevicePath.Contains(usbInterface, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(collection.PhysicalLocation);
    }

    internal static bool TryDecodeRear(ReadOnlySpan<byte> report, out CanonicalButtons buttons)
    {
        buttons = CanonicalButtons.None;
        if (report.Length != 16 || report[0] != 0x9A)
        {
            return false;
        }

        buttons = ((report[1] & 2) != 0 ? CanonicalButtons.RearPaddle1 : 0)
                  | ((report[1] & 4) != 0 ? CanonicalButtons.RearPaddle2 : 0);
        return true;
    }

    internal static bool IsGuidePress(ReadOnlySpan<byte> report)
    {
        // This event has no release report. Other 9E events must not become Guide presses.
        return report.Length == 16 && report[0] == 0x9E
               && BinaryPrimitives.ReadUInt16LittleEndian(report[1..]) == 0
               && BinaryPrimitives.ReadUInt16LittleEndian(report[3..]) == 5;
    }

    internal static bool TryDecodeQam(ReadOnlySpan<byte> report, out bool pressed)
    {
        pressed = false;
        if (report.Length != 3 || report[0] != 0x37)
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(report[1..]);
        if (value is not (0xFF81 or 0))
        {
            return false;
        }

        pressed = value == 0xFF81;
        return true;
    }
}
