using LibHandheld.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>One descriptor parser with reusable usage storage. Families supply their own button and axis maps.</summary>
internal sealed unsafe class HidUsageReader : IDisposable
{
    private readonly ushort[] _usages;
    private readonly HidCapability[] _values;
    private readonly NativeHid.PreparsedData _preparsed;
    private nint _data;

    internal HidUsageReader(SafeFileHandle handle)
    {
        var status = NativeHid.ReadCaps(handle, out var preparsed, out var caps);
        if (status != HidCapsStatus.Ok)
        {
            throw new IOException(status == HidCapsStatus.NoDescriptor
                ? "The HID report descriptor is unavailable."
                : "HID capabilities are unavailable.");
        }

        _preparsed = preparsed!;
        _data = _preparsed.Value;
        try
        {
            InputLength = caps.InputReportByteLength;
            _values = HidDevices.InputValues(_data, caps.NumberInputValueCaps);
            _usages = new ushort[Math.Max(1, (int)NativeHid.HidP_MaxUsageListLength(0, 0, _data))];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int InputLength { get; }

    public void Dispose()
    {
        if (_data == 0)
        {
            return;
        }

        _data = 0;
        _preparsed.Dispose();
    }

    internal bool HasValue(ushort usage, ushort page = 1)
    {
        return Find(page, usage, null) is not null;
    }

    private HidCapability? Find(ushort page, ushort usage, ushort? link, byte? reportId = null)
    {
        foreach (var value in _values)
        {
            if (value.UsagePage == page && usage >= value.UsageMin && usage <= value.UsageMax
                && (link is null || value.LinkCollection == link)
                && (reportId is null || value.ReportId == reportId))
            {
                return value;
            }
        }

        return null;
    }

    internal bool TryValue(ReadOnlySpan<byte> report, ushort usage, out HidReading reading, ushort page = 1,
        ushort? link = null)
    {
        reading = default;
        if (_data == 0 || report.IsEmpty || Find(page, usage, link, report[0]) is not { } value)
        {
            return false;
        }

        fixed (byte* bytes = report)
        {
            if (NativeHid.HidP_GetUsageValue(0, page, value.LinkCollection, usage, out var raw, _data, bytes,
                    (uint)report.Length) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                return false;
            }

            var number = value.LogicalMin < 0 && value.BitSize is > 0 and < 32
                ? (int)(raw << (32 - value.BitSize)) >> (32 - value.BitSize)
                : (long)raw;
            reading = new HidReading(number, value.LogicalMin, value.LogicalMax, value.LinkCollection);
            return true;
        }
    }

    internal ReadOnlySpan<ushort> Usages(ReadOnlySpan<byte> report, ushort page, ushort link = 0)
    {
        if (_data == 0 || report.IsEmpty)
        {
            return [];
        }

        var count = (uint)_usages.Length;
        fixed (byte* bytes = report)
        fixed (ushort* values = _usages)
        {
            if (NativeHid.HidP_GetUsages(0, page, link, values, ref count, _data, bytes, (uint)report.Length)
                != NativeHid.HIDP_STATUS_SUCCESS)
            {
                return [];
            }
        }

        return _usages.AsSpan(0, (int)Math.Min(count, (uint)_usages.Length));
    }

    /// <summary>Decodes a DirectInput gamepad: X/Y left stick, Z/Rz right stick, optional Rx/Ry triggers, 0x39 hat.</summary>
    /// <param name="report">One input report.</param>
    /// <param name="timestamp">The sample's timestamp.</param>
    /// <param name="buttons">The family's button map, indexed by page-9 usage minus one.</param>
    /// <param name="initial">Buttons already held from another source, such as an OEM latch.</param>
    /// <param name="sample">The decoded sample.</param>
    /// <returns>False when the report carries no X, Y, Z and Rz values.</returns>
    internal bool TryDecodeGamepad(ReadOnlySpan<byte> report, DateTimeOffset timestamp,
        ReadOnlySpan<CanonicalButtons> buttons, CanonicalButtons initial, out CanonicalControllerSample sample)
    {
        sample = default;
        if (!TryValue(report, 0x30, out var x) || !TryValue(report, 0x31, out var y)
                                               || !TryValue(report, 0x32, out var z) ||
                                               !TryValue(report, 0x35, out var rz))
        {
            return false;
        }

        var held = initial;
        foreach (var usage in Usages(report, 9))
        {
            var index = usage - 1;
            if ((uint)index < (uint)buttons.Length)
            {
                held |= buttons[index];
            }
        }

        if (TryValue(report, 0x39, out var hat))
        {
            held |= Hat((int)(hat.Raw - hat.Minimum));
        }

        sample = new CanonicalControllerSample
        {
            Timestamp = timestamp, Buttons = held, LeftStickX = x.Signed, LeftStickY = -y.Signed,
            RightStickX = z.Signed, RightStickY = -rz.Signed,
            LeftTrigger = TryValue(report, 0x33, out var rx) ? rx.Unsigned : 0,
            RightTrigger = TryValue(report, 0x34, out var ry) ? ry.Unsigned : 0
        };
        return true;
    }

    internal static CanonicalButtons Hat(int position)
    {
        return position switch
        {
            0 => CanonicalButtons.DPadUp, 1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
            2 => CanonicalButtons.DPadRight, 3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
            4 => CanonicalButtons.DPadDown, 5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
            6 => CanonicalButtons.DPadLeft, 7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp,
            _ => CanonicalButtons.None
        };
    }
}

internal readonly record struct HidReading(long Raw, int Minimum, int Maximum, ushort LinkCollection = 0)
{
    internal float Signed =>
        Maximum > Minimum ? Math.Clamp((float)((Raw - Minimum) * 2d / (Maximum - Minimum) - 1), -1, 1) : 0;

    internal float Unsigned =>
        Maximum > Minimum ? Math.Clamp((float)((Raw - Minimum) / (double)(Maximum - Minimum)), 0, 1) : 0;
}
