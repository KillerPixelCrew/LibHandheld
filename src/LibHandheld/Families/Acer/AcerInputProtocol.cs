using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Decodes the Atlas's OEM HID reports independently of the ordinary XInput gamepad.</summary>
/// <remarks>
///     The measured variants are USB 0502:F901 release 0004 with 16-byte vendor input and releases
///     0100 and 0105 (Acer controller firmware 1.00 and 1.05) with 15-byte input. Report lengths
///     include the report ID. These selectors deliberately
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
        return item is { VendorId: 0x0502, ProductId: 0xF90A, ReleaseNumber: 0x0100 or 0x0105 }
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
                   || collection.ReleaseNumber is 0x0100 or 0x0105 && collection.InputLength == 15);
    }

    /// <summary>Matches MI_02's 0088:0001 three-byte front-right/QAM collection.</summary>
    internal static bool IsQamCollection(HidCollection collection)
    {
        return IsAtlasCollection(collection, "mi_02") && collection.UsagePage == 0x88
               && collection.Usage == 1 && collection.InputLength == 3;
    }

    private static bool IsAtlasCollection(HidCollection collection, string usbInterface)
    {
        return collection.VendorId == 0x0502
               && (collection.ProductId == 0xF901 && collection.ReleaseNumber is 4 or 0x0100 or 0x0105
                   || collection.ProductId == AcerControllerTransport.Atlas7ProductId && collection.ReleaseNumber == 0x0100)
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

        // Bit 2 is rear-left (M2), bit 1 rear-right (M1). RearPaddle1 is the left paddle (L4).
        buttons = ((report[1] & 4) != 0 ? CanonicalButtons.RearPaddle1 : 0)
                  | ((report[1] & 2) != 0 ? CanonicalButtons.RearPaddle2 : 0);
        return true;
    }

    /// <summary>The XInput buttons report 9A names in its two key slots while a rear key is held.</summary>
    /// <param name="report">One complete 15- or 16-byte report 9A.</param>
    /// <returns>XInput button bits; an empty slot or an unknown code adds nothing.</returns>
    /// <remarks>
    ///     Combination mode (rear keys 1) is Predator Sense's System Hotkeys mode: while M1 or M2 is held the
    ///     firmware stops the XInput report and names each other key pressed in little-endian words 3 and
    ///     5, the first key and a second one held with it. 2026-11-17 captures, firmware 1.05: A 01, B 02,
    ///     X 03, Y 04, LB 05, RB 06, View 07, Menu 08, R3 0A and D-pad up/down/left/right 90-93; L3 09
    ///     follows the order. The triggers send no code; their travel is in report 9B.
    /// </remarks>
    internal static ushort DecodeCombinationButtons(ReadOnlySpan<byte> report)
    {
        return report.Length is 15 or 16 && report[0] == 0x9A
            ? (ushort)(CombinationButton(BinaryPrimitives.ReadUInt16LittleEndian(report[3..]))
                       | CombinationButton(BinaryPrimitives.ReadUInt16LittleEndian(report[5..])))
            : (ushort)0;
    }

    /// <summary>The front keys report 9A names while a rear key is held: A1 the left (Guide), A0 the right (Quick Access).</summary>
    /// <param name="report">One complete 15- or 16-byte report 9A.</param>
    /// <returns>The front keys held, with a real release unlike their ordinary notifications.</returns>
    internal static CanonicalButtons DecodeCombinationFront(ReadOnlySpan<byte> report)
    {
        if (report.Length is not (15 or 16) || report[0] != 0x9A)
        {
            return CanonicalButtons.None;
        }

        var first = BinaryPrimitives.ReadUInt16LittleEndian(report[3..]);
        var second = BinaryPrimitives.ReadUInt16LittleEndian(report[5..]);
        return (first == 0xA1 || second == 0xA1 ? CanonicalButtons.Guide : 0)
               | (first == 0xA0 || second == 0xA0 ? CanonicalButtons.QuickAccess : 0);
    }

    private static ushort CombinationButton(ushort code)
    {
        return code switch
        {
            0x01 => 0x1000,
            0x02 => 0x2000,
            0x03 => 0x4000,
            0x04 => 0x8000,
            0x05 => 0x0100,
            0x06 => 0x0200,
            0x07 => 0x0020,
            0x08 => 0x0010,
            0x09 => 0x0040,
            0x0A => 0x0080,
            0x90 => 0x0001,
            0x91 => 0x0002,
            0x92 => 0x0004,
            0x93 => 0x0008,
            _ => 0
        };
    }

    /// <summary>Reads report 9B's sticks and triggers, which replace the frozen XInput pad while a rear key is held.</summary>
    /// <param name="report">One complete 15- or 16-byte report 9B.</param>
    /// <param name="analog">The sticks and triggers on XInput's scale.</param>
    /// <returns>False for another report, and for the neutral 9B sent at release with byte 1 zero.</returns>
    /// <remarks>
    ///     Byte 1 repeats the held rear keys. Little-endian words 3, 5, 7 and 9 are the left X, left Y, right
    ///     X and right Y, unsigned with X centred on 8000 and Y on 7FFF; up is low, so Y is inverted for
    ///     XInput. Words 11 and 13 are the triggers, 0 to 1023. A squeezed RT moved word 13 in the
    ///     2026-11-17 captures; word 11 follows XInput's left-before-right order. The report arrives about
    ///     every 4 ms while anything moves, and only while a rear key is held: the one sent at release reads
    ///     neutral even with a stick still deflected.
    /// </remarks>
    internal static bool TryDecodeAnalog(ReadOnlySpan<byte> report, out AcerAnalog analog)
    {
        analog = default;
        if (report.Length is not (15 or 16) || report[0] != 0x9B || (report[1] & 6) == 0)
        {
            return false;
        }

        analog = new AcerAnalog(
            (short)(BinaryPrimitives.ReadUInt16LittleEndian(report[3..]) - 32768),
            (short)(32767 - BinaryPrimitives.ReadUInt16LittleEndian(report[5..])),
            (short)(BinaryPrimitives.ReadUInt16LittleEndian(report[7..]) - 32768),
            (short)(32767 - BinaryPrimitives.ReadUInt16LittleEndian(report[9..])),
            Trigger(BinaryPrimitives.ReadUInt16LittleEndian(report[11..])),
            Trigger(BinaryPrimitives.ReadUInt16LittleEndian(report[13..])));
        return true;
    }

    private static byte Trigger(ushort value)
    {
        return (byte)((Math.Min(value, (ushort)1023) * 255 + 511) / 1023);
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

/// <summary>The sticks and triggers report 9B carries, already on XInput's scale.</summary>
internal readonly record struct AcerAnalog(
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger);
