// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>Aura effects HC offers on the Ally (<c>ROGAlly.cs:22-29, 224-228</c>).</summary>
internal enum AuraEffect : byte
{
    /// <summary>HC <c>SolidColor</c>, HHD <c>solid</c>.</summary>
    Solid = 0,

    /// <summary>HC <c>Breathing</c>, HHD <c>pulse</c>/<c>duality</c>: primary and secondary colours.</summary>
    Breathing = 1,

    /// <summary>HC <c>Wheel</c>, HHD <c>rainbow</c> ("color cycle").</summary>
    ColorCycle = 2,

    /// <summary>HC <c>Rainbow</c>, HHD <c>spiral</c>.</summary>
    Rainbow = 3
}

/// <summary>Aura zones: HC <c>LEDZone</c>, HHD <c>rgb_command</c> zones (identical numbering).</summary>
internal enum AuraZone : byte
{
    All = 0,
    LeftStickLeft = 1,
    LeftStickRight = 2,
    RightStickLeft = 3,
    RightStickRight = 4
}

/// <summary>Byte-level encoders and decoders. Nothing here opens a device.</summary>
internal static class AllyProtocol
{
    /// <summary>HHD <c>FEATURE_KBD_DRIVER</c>, HC <c>INPUT_HID_ID</c> (90).</summary>
    public const byte VendorReportId = 0x5A;

    /// <summary>HC <c>AURA_HID_ID</c> (93), HHD <c>FEATURE_KBD_APP</c>.</summary>
    public const byte AuraReportId = 0x5D;

    public const int ConfigurationLength = 64;

    /// <summary>
    ///     HC <c>AuraSpeed</c>: Slow 0xEB, Medium 0xF5, Fast 0xE1 (<c>ROGAlly.cs:31-36</c>). HHD reads
    ///     the same bytes the other way round, 0xE1 low to 0xF5 high (<c>hid.py:92-100</c>); HC is
    ///     followed for lighting and the disagreement is in PROVENANCE.md.
    /// </summary>
    public const byte SpeedSlow = 0xEB;

    public const byte SpeedMedium = 0xF5;
    public const byte SpeedFast = 0xE1;

    /// <summary>HC <c>M1F18M2F17</c> (<c>ROGAlly.cs:163-168</c>): M1 and M2 become keyboard keys the plugin can read.</summary>
    /// <remarks>
    ///     Without this block the rear buttons send ASUS's own M1/M2 codes, which no Windows API
    ///     surfaces. HHD writes the identical bytes as <c>REMAP_M1M2_F17F18</c>.
    /// </remarks>
    public static readonly byte[] RearKeyboardMapping =
        Pad("5a d1 02 08 2c 02 00 28 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 02 00 30");

    /// <summary>HC <c>M1M2Default</c> (<c>ROGAlly.cs:156-161</c>): ASUS's M2 code 0x8E, then M1 0x8F.</summary>
    public static readonly byte[] RearDefaultMapping =
        Pad("5a d1 02 08 2c 02 00 8e 00 00 00 00 00 00 00 00 02 00 8e 00 00 00 00 00 00 00 00 02 00 8f");

    // HC commitReset1of4-4of4 (ROGAlly.cs:177-183): turbo reset, vibration 100/100, stick and trigger
    // ranges 0-100.
    private static readonly byte[][] Commit =
    [
        Pad("5a d1 0f 20"),
        Pad("5a d1 06 02 64 64"),
        Pad("5a d1 04 04 00 64 00 64"),
        Pad("5a d1 05 04 00 64 00 64")
    ];

    // HC modeGame and the default D-pad, stick, shoulder, face and view/menu tables
    // (ROGAlly.cs:93-154), byte for byte and in HC's ConfigureController order (ROGAlly.cs:652-659).
    // HC has shipped these on every Ally including the Xbox models; HHD's variants of the D-pad
    // left/right and A/B tables differ by one byte (PROVENANCE.md) and are not used.
    private static readonly byte[][] FrontTables =
    [
        Pad("5a d1 01 01 01"),
        Pad("5a d1 02 01 2c 01 09 00 00 00 00 00 00 00 00 00 05 00 00 19 00 00 00 00 00 00 00 01 0a 00 00 00 00"
            + " 00 00 00 00 00 04 00 00 00 00 03 8c 88 76"),
        Pad("5a d1 02 02 2c 01 0b 00 00 00 00 00 00 00 00 00 04 00 00 00 00 02 82 23 00 00 00 01 0c 00 00 00 00"
            + " 00 00 00 00 04 00 00 00 00 02 82 0d"),
        Pad("5a d1 02 03 2c 01 07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 08"),
        Pad("5a d1 02 04 2c 01 05 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 06"),
        Pad("5a d1 02 05 2c 01 01 00 00 00 00 00 00 00 00 00 05 00 00 16 00 00 00 00 00 00 00 01 02 00 00 00 00"
            + " 00 00 00 00 00 04 00 00 00 02 82 31"),
        Pad("5a d1 02 06 2c 01 03 00 00 00 00 00 00 00 00 00 04 00 00 00 00 02 82 4d 00 00 00 01 04 00 00 00 00"
            + " 00 00 00 00 00 05 00 00 1e"),
        Pad("5a d1 02 07 2c 01 11 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 12")
    ];

    // HC triggersDefault (ROGAlly.cs:170-175).
    private static readonly byte[] Triggers =
        Pad("5a d1 02 09 2c 01 0d 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 0e");

    /// <summary>
    ///     The controller configuration written when the plugin takes the gamepad: HC's
    ///     <c>ConfigureController(Remap: true)</c> (<c>ROGAlly.cs:646-668</c>), every table in its order,
    ///     sent as 64-byte feature reports.
    /// </summary>
    public static IReadOnlyList<byte[]> GameModeConfiguration { get; } =
        [.. FrontTables, RearKeyboardMapping, Triggers, .. Commit];

    /// <summary>The same tables with the factory M1/M2 block, written when the plugin lets go.</summary>
    /// <remarks>HC's <c>Close</c> calls <c>ConfigureController(Remap: false)</c> (<c>ROGAlly.cs:396-400</c>).</remarks>
    public static IReadOnlyList<byte[]> DefaultConfiguration { get; } =
        [.. FrontTables, RearDefaultMapping, Triggers, .. Commit];

    /// <summary>Reads one vendor input report and returns its event code.</summary>
    /// <remarks>HHD reads <c>rep[1]</c> after checking <c>rep[0] == 0x5A</c> (<c>base.py:176-179</c>).</remarks>
    /// <param name="report">Input report bytes; at least report ID and event byte are required.</param>
    /// <param name="code">Nonzero event byte on success; zero on failure.</param>
    /// <returns>True only for a 0x5A report with a nonzero event code; known-action classification is separate.</returns>
    public static bool TryReadVendorEvent(ReadOnlySpan<byte> report, out byte code)
    {
        code = 0;
        if (report.Length < 2 || report[0] != VendorReportId || report[1] == 0)
        {
            return false;
        }

        code = report[1];
        return true;
    }

    /// <summary>HC's brightness feature report: <c>5D BA C5 C4 level</c>, level 0-3.</summary>
    /// <remarks>HC scales 0-100 by 33.33 (<c>ROGAlly.cs:507-522</c>); HHD sends the same bytes on 0x5A.</remarks>
    /// <param name="percent">Requested percentage, converted to the nearest firmware level and clamped to 0-3.</param>
    /// <returns>A newly allocated five-byte Aura brightness feature report; no hardware call is made.</returns>
    public static byte[] Brightness(int percent)
    {
        var level = (byte)Math.Clamp((int)Math.Round(percent / 33.33), 0, 3);
        return [AuraReportId, 0xBA, 0xC5, 0xC4, level];
    }

    /// <summary>HC's <c>AuraMessage</c> (<c>ROGAlly.cs:595-617</c>), seventeen bytes.</summary>
    /// <param name="effect">Aura effect code, encoded without enum validation.</param>
    /// <param name="zone">Target Aura zone, encoded without enum validation.</param>
    /// <param name="primary">Primary packed 0xRRGGBB color.</param>
    /// <param name="secondary">Secondary packed 0xRRGGBB color used by applicable effects.</param>
    /// <param name="speed">Firmware speed byte, normally produced by Speed.</param>
    /// <returns>A newly allocated 17-byte Aura color message; this helper does not send it.</returns>
    public static byte[] Color(AuraEffect effect, AuraZone zone, int primary, int secondary, byte speed)
    {
        return
        [
            AuraReportId,
            0xB3,
            (byte)zone,
            (byte)effect,
            Red(primary),
            Green(primary),
            Blue(primary),
            speed,
            0,
            effect is AuraEffect.Breathing ? (byte)1 : (byte)0,
            Red(secondary),
            Green(secondary),
            Blue(secondary),
            0,
            0,
            0,
            0
        ];
    }

    /// <summary>HC <c>MESSAGE_APPLY</c>.</summary>
    /// <returns>A new 0x5D/0xB4 apply message for the caller to send as an Aura output report.</returns>
    public static byte[] Apply()
    {
        return [AuraReportId, 0xB4];
    }

    /// <summary>HC <c>MESSAGE_SET</c>.</summary>
    /// <returns>A new 0x5D/0xB5 set message for the caller to send as an Aura output report.</returns>
    public static byte[] Set()
    {
        return [AuraReportId, 0xB5, 0, 0, 0];
    }

    /// <summary>HC's speed bands: at most 33 slow, at most 66 medium, else fast (<c>ROGAlly.cs:552</c>).</summary>
    /// <param name="percent">Requested speed percentage; values outside 0-100 still map by the same thresholds.</param>
    /// <returns>The firmware slow, medium or fast byte for the requested band.</returns>
    public static byte Speed(int percent)
    {
        return percent <= 33 ? SpeedSlow : percent <= 66 ? SpeedMedium : SpeedFast;
    }

    internal static byte[] Pad(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
        if (bytes.Length > ConfigurationLength)
        {
            throw new ArgumentException("A configuration report is at most 64 bytes.", nameof(hex));
        }

        var report = new byte[ConfigurationLength];
        bytes.CopyTo(report, 0);
        return report;
    }

    private static byte Red(int color)
    {
        return (byte)((color >> 16) & 0xFF);
    }

    private static byte Green(int color)
    {
        return (byte)((color >> 8) & 0xFF);
    }

    private static byte Blue(int color)
    {
        return (byte)(color & 0xFF);
    }
}
