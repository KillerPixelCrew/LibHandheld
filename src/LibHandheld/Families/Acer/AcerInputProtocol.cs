using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Decodes the Atlas's OEM HID reports independently of the ordinary XInput gamepad.</summary>
/// <remarks>
///     The measured variants are USB 0502:F901 release 0004 with 16-byte vendor input and release
///     0100 with 15-byte input. Report lengths include the report ID. These selectors deliberately
///     reject other releases and ambiguous collections; a shared VID/PID is not a protocol guarantee.
///     The separate keyboard's Win+Tab/F22 holds and I2C volume input are outside this parser.
/// </remarks>
internal static class AcerInputProtocol
{
    /// <summary>The F90A vendor command collection, which clients incorrectly expose as an extra pad.</summary>
    /// <remarks>
    ///     Correlated to the selected gamepad port and limited to the gamepad's own MI_00 interface. The
    ///     consumer collection MI_01/COL03 shares the keyboard's interface; hiding it stopped all keyboard
    ///     input in the deployed build, so it stays visible.
    /// </remarks>
    internal static IReadOnlyList<PhysicalDeviceIdentity> DInputCompanions(HidCollection gamepad)
    {
        if (gamepad is not { VendorId: 0x0502, ProductId: 0xF90A, UsagePage: 1, Usage: 5 }
            || string.IsNullOrWhiteSpace(gamepad.PhysicalLocation))
        {
            return [];
        }
        return HidDevices.Enumerate(0x0502, [0xF90A])
            .Where(item => IsDInputCompanion(item)
                           && HidDevices.SamePhysicalLocation(item.PhysicalLocation, gamepad.PhysicalLocation))
            .Select(PhysicalIdentities.FromCollection).ToArray();
    }

    internal static bool IsDInputCompanion(HidCollection item)
    {
        return item is { VendorId: 0x0502, ProductId: 0xF90A, ReleaseNumber: 0x0100 }
               && !string.IsNullOrWhiteSpace(item.PhysicalLocation)
               && item is { UsagePage: 0xFFF0, Usage: 0x40, InputLength: 64, OutputLength: 64 }
               && item.InstancePath.Contains("&MI_00&COL02\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Matches MI_01's FFF1:00F5 rear-key and Guide notification collection.</summary>
    internal static bool IsVendorCollection(HidCollection collection)
    {
        return IsAtlasCollection(collection, "mi_01") && collection.UsagePage == 0xFFF1
               && collection.Usage == 0xF5
               && (collection.ReleaseNumber == 4 && collection.InputLength == 16
                   || collection.ReleaseNumber == 0x0100 && collection.InputLength == 15);
    }

    /// <summary>Matches MI_02's 0088:0001 three-byte front-right/QAM collection.</summary>
    internal static bool IsQamCollection(HidCollection collection)
    {
        return IsAtlasCollection(collection, "mi_02") && collection.UsagePage == 0x88
               && collection.Usage == 1 && collection.InputLength == 3;
    }

    private static bool IsAtlasCollection(HidCollection collection, string usbInterface)
    {
        return collection.VendorId == 0x0502 && collection.ProductId == 0xF901
               && collection.ReleaseNumber is 4 or 0x0100
               && collection.DevicePath.Contains(usbInterface, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(collection.PhysicalLocation);
    }

    /// <summary>Reads the complete rear-button state from report 9A; unrelated reports are not releases.</summary>
    /// <param name="report">One complete 15- or 16-byte report, including ID 9A.</param>
    /// <param name="buttons">M1/right from byte 1 bit 1, M2/left from bit 2; both may be held.</param>
    /// <returns>True for a recognized rear state, including byte 1 zero as an explicit release.</returns>
    /// <remarks>Native rear reports require controller rear-key mode Combination (1), owned by AcerControllerModeService.</remarks>
    internal static bool TryDecodeRear(ReadOnlySpan<byte> report, out CanonicalButtons buttons)
    {
        buttons = CanonicalButtons.None;
        if (report.Length is not (15 or 16) || report[0] != 0x9A)
        {
            return false;
        }

        buttons = ((report[1] & 2) != 0 ? CanonicalButtons.RearPaddle1 : 0)
                  | ((report[1] & 4) != 0 ? CanonicalButtons.RearPaddle2 : 0);
        return true;
    }

    /// <summary>Recognizes only 9E/0000/0005, the release-less front-left short-press notification.</summary>
    /// <remarks>The two words after report ID 9E are little endian. A timer supplies the synthetic release.</remarks>
    internal static bool IsGuidePress(ReadOnlySpan<byte> report)
    {
        // This event has no release report. Other 9E events must not become Guide presses.
        return report.Length is 15 or 16 && report[0] == 0x9E
               && BinaryPrimitives.ReadUInt16LittleEndian(report[1..]) == 0
               && BinaryPrimitives.ReadUInt16LittleEndian(report[3..]) == 5;
    }

    /// <summary>Decodes 37 81 FF as front-right down and 37 00 00 as release.</summary>
    /// <param name="report">Exactly three bytes, including report ID 37.</param>
    /// <param name="pressed">True for the little-endian FF81 payload, false for zero.</param>
    /// <returns>False for other report IDs, lengths or payloads; they do not change held state.</returns>
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
