using System.Runtime.InteropServices;
using LibHandheld.Internal.Input;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.MsiClaw;

/// <summary>
///     Decodes the DirectInput gamepad through its HID report descriptor, the way HC's
///     <c>DClawController</c> reads every Claw through DirectInput's descriptor-parsed state.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ClawControllerCodec" /> decodes fixed byte offsets measured on MS-1T52. Its bits are
///         DirectInput's button indices in order and its bytes the X, Y, Z, Rz, Rx and Ry values, which is
///         the mapping HC applies to every model, but the byte layout itself was only measured on the
///         reference unit. On any other model this class reads the same usages through <c>HidP_*</c>, so
///         a different layout of the same collection still decodes correctly.
///     </para>
///     <para>
///         Mapping, from HC 1.3.1.6 <c>HandheldCompanion.Controllers.MSI/DClawController.cs</c>: button
///         index 0 X, 1 A, 2 B, 3 Y, 4 LB, 5 RB, 8 View, 9 Menu, 10 left stick, 11 right stick, 15 and 16
///         the rear paddles; X/Y the left stick with Y inverted, Z/Rz the right stick with Rz inverted,
///         Rx/Ry the triggers, and the first POV the D-pad. Index 6 and 7 are the triggers' digital
///         bits, which HC reads and WSGM leaves to the analog values. The paddles follow the reference
///         unit's measurement (index 16 left, 15 right), which HC has reversed.
///     </para>
/// </remarks>
internal sealed unsafe class HidDescriptorGamepad : IDisposable
{
    private const int HidpInput = 0;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort ButtonPage = 0x09;

    private readonly Axis _hat;
    private readonly Axis _rx;
    private readonly Axis _ry;
    private readonly Axis _rz;
    private readonly ushort[] _usages = new ushort[128];
    private readonly Axis _x;
    private readonly Axis _y;
    private readonly Axis _z;
    private nint _preparsed;

    private HidDescriptorGamepad(nint preparsed, int inputLength, ReadOnlySpan<HidpValueCaps> values)
    {
        _preparsed = preparsed;
        InputLength = inputLength;
        _x = Find(values, 0x30);
        _y = Find(values, 0x31);
        _z = Find(values, 0x32);
        _rx = Find(values, 0x33);
        _ry = Find(values, 0x34);
        _rz = Find(values, 0x35);
        _hat = Find(values, 0x39);
    }

    /// <summary>The input report length the descriptor declares, report id included.</summary>
    public int InputLength { get; }

    public void Dispose()
    {
        var preparsed = _preparsed;
        _preparsed = 0;
        if (preparsed != 0)
        {
            _ = NativeHid.HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Reads the descriptor of an open gamepad collection.</summary>
    /// <exception cref="IOException">The descriptor lacks a stick axis or cannot be read.</exception>
    /// <param name="handle">Borrowed open HID collection handle; it is neither closed nor retained by the decoder.</param>
    /// <returns>A caller-owned decoder holding native preparsed data; dispose it after decoding stops.</returns>
    public static HidDescriptorGamepad Create(SafeFileHandle handle)
    {
        if (!NativeHid.HidD_GetPreparsedData(handle, out var preparsed))
        {
            throw new IOException("The gamepad's HID descriptor could not be read.");
        }

        try
        {
            if (NativeHid.HidP_GetCaps(preparsed, out var caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                throw new IOException("The gamepad's HID capabilities could not be read.");
            }

            var values = new HidpValueCaps[Math.Max((int)caps.NumberInputValueCaps, 1)];
            var count = (ushort)values.Length;
            fixed (HidpValueCaps* pointer = values)
            {
                if (HidP_GetValueCaps(HidpInput, pointer, ref count, preparsed) != NativeHid.HIDP_STATUS_SUCCESS)
                {
                    throw new IOException("The gamepad's HID value capabilities could not be read.");
                }
            }

            HidDescriptorGamepad gamepad = new(preparsed, caps.InputReportByteLength, values.AsSpan(0, count));
            if (!gamepad._x.Present || !gamepad._y.Present || !gamepad._z.Present || !gamepad._rz.Present)
            {
                gamepad.Dispose();
                throw new IOException("The gamepad's HID descriptor has no X, Y, Z and Rz sticks.");
            }

            preparsed = 0;
            return gamepad;
        }
        finally
        {
            if (preparsed != 0)
            {
                _ = NativeHid.HidD_FreePreparsedData(preparsed);
            }
        }
    }

    /// <summary>Decodes one input report using required stick usages; the reader filters MCU idle reports before this call.</summary>
    /// <param name="report">Complete input report including report ID; its memory is used only during this call.</param>
    /// <param name="timestamp">Observation time assigned to the sample and used to merge held OEM buttons.</param>
    /// <param name="oemButtons">Optional OEM press latch, or null to omit OEM buttons.</param>
    /// <param name="sample">Canonical complete sample on success; default on failure.</param>
    /// <returns>True when required stick usages decode; false after disposal or an incompatible/unreadable report.</returns>
    /// <remarks>Serialize decoding and disposal because usage scratch storage and preparsed data are shared.</remarks>
    public bool TryDecode(
        ReadOnlySpan<byte> report,
        DateTimeOffset timestamp,
        OemButtonLatch? oemButtons,
        out CanonicalControllerSample sample)
    {
        sample = default;
        if (_preparsed == 0
            || !_x.TryRead(_preparsed, report, out var x)
            || !_y.TryRead(_preparsed, report, out var y)
            || !_z.TryRead(_preparsed, report, out var z)
            || !_rz.TryRead(_preparsed, report, out var rz))
        {
            return false;
        }

        // HC's DClawController skips a DirectInput state with RotationX/Y/Z all at 32767, the centred
        // state DirectInput reports before the first HID report; raw HID has no such state, and the
        // MCU's idle report (all 0xFF) is skipped by the reader instead.
        var rx = _rx.TryRead(_preparsed, report, out var rxValue) ? rxValue : _rx.Minimum;
        var ry = _ry.TryRead(_preparsed, report, out var ryValue) ? ryValue : _ry.Minimum;

        var buttons = CanonicalButtons.None;
        var length = (uint)_usages.Length;
        fixed (ushort* usages = _usages)
        fixed (byte* data = report)
        {
            if (HidP_GetUsages(HidpInput, ButtonPage, 0, usages, ref length, _preparsed, data, (uint)report.Length)
                == NativeHid.HIDP_STATUS_SUCCESS)
            {
                for (var index = 0; index < length; index++)
                {
                    buttons |= Button(_usages[index] - 1);
                }
            }
        }

        if (_hat.TryRead(_preparsed, report, out var hat))
        {
            buttons |= DecodeHat(hat - _hat.Minimum);
        }

        buttons |= oemButtons?.Current(timestamp) ?? CanonicalButtons.None;
        sample = new CanonicalControllerSample
        {
            Timestamp = timestamp,
            Buttons = buttons,
            LeftStickX = _x.Signed(x),
            LeftStickY = -_y.Signed(y),
            RightStickX = _z.Signed(z),
            RightStickY = -_rz.Signed(rz),
            LeftTrigger = _rx.Unsigned(rx),
            RightTrigger = _ry.Unsigned(ry)
        };
        return true;
    }

    /// <summary>HC's DirectInput button index to the canonical button.</summary>
    /// <param name="index">Zero-based DirectInput button index.</param>
    /// <returns>The mapped button, or None for unrepresented indices, including digital trigger bits.</returns>
    internal static CanonicalButtons Button(int index)
    {
        return index switch
        {
            0 => CanonicalButtons.X,
            1 => CanonicalButtons.A,
            2 => CanonicalButtons.B,
            3 => CanonicalButtons.Y,
            4 => CanonicalButtons.LeftShoulder,
            5 => CanonicalButtons.RightShoulder,
            8 => CanonicalButtons.View,
            9 => CanonicalButtons.Menu,
            10 => CanonicalButtons.LeftStick,
            11 => CanonicalButtons.RightStick,
            15 => CanonicalButtons.RearPaddle2,
            16 => CanonicalButtons.RearPaddle1,
            _ => CanonicalButtons.None
        };
    }

    /// <summary>A POV value from the descriptor's logical minimum, clockwise from up in eighths.</summary>
    /// <param name="position">Hat value relative to its logical minimum; 0–7 run clockwise from up.</param>
    /// <returns>The cardinal/diagonal D-pad mask, or None outside 0–7.</returns>
    internal static CanonicalButtons DecodeHat(int position)
    {
        return position switch
        {
            0 => CanonicalButtons.DPadUp,
            1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
            2 => CanonicalButtons.DPadRight,
            3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
            4 => CanonicalButtons.DPadDown,
            5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
            6 => CanonicalButtons.DPadLeft,
            7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp,
            _ => CanonicalButtons.None
        };
    }

    private static Axis Find(ReadOnlySpan<HidpValueCaps> values, ushort usage)
    {
        foreach (var value in values)
        {
            var first = value.UsageOrMinimum;
            var last = value.IsRange != 0 ? value.UsageMaximum : first;
            if (value.UsagePage == GenericDesktopPage && usage >= first && usage <= last)
            {
                return new Axis(usage, value.LogicalMin, value.LogicalMax, value.BitSize);
            }
        }

        return default;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, HidpValueCaps* valueCaps, ref ushort length,
        nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        ushort* usageList, ref uint usageLength, nint preparsedData, byte* report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, nint preparsedData, byte* report, uint reportLength);

    /// <summary>One Generic Desktop value and its logical range.</summary>
    private readonly record struct Axis(ushort Usage, int Minimum, int Maximum, ushort BitSize)
    {
        public bool Present => Usage != 0 && Maximum > Minimum;

        public bool TryRead(nint preparsed, ReadOnlySpan<byte> report, out int value)
        {
            value = 0;
            if (!Present)
            {
                return false;
            }

            uint raw;
            fixed (byte* data = report)
            {
                if (HidP_GetUsageValue(HidpInput, GenericDesktopPage, 0, Usage, out raw, preparsed, data,
                        (uint)report.Length) != NativeHid.HIDP_STATUS_SUCCESS)
                {
                    return false;
                }
            }

            // HidP returns the field's raw bits; a signed logical range needs them sign-extended.
            value = Minimum < 0 && BitSize is > 0 and < 32 && (raw & (1u << (BitSize - 1))) != 0
                ? (int)(raw | (uint.MaxValue << BitSize))
                : (int)raw;
            return true;
        }

        public float Signed(int value)
        {
            return Math.Clamp((value - Minimum) * 2f / (Maximum - Minimum) - 1f, -1f, 1f);
        }

        public float Unsigned(int value)
        {
            return Present ? Math.Clamp((value - Minimum) / (float)(Maximum - Minimum), 0f, 1f) : 0f;
        }
    }

    /// <summary><c>HIDP_VALUE_CAPS</c> from hidpi.h, 72 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidpValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageOrMinimum;
        [FieldOffset(58)] public ushort UsageMaximum;
    }
}

/// <summary>The preparsed-data calls the descriptor decoder alone needs; the rest of HID lives in the SDK.</summary>
internal static partial class NativeHid
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(nint preparsedData, out HidCaps capabilities);

    [StructLayout(LayoutKind.Sequential)]
    public struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
