using LibHandheld.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>One descriptor parser with reusable usage storage. Families supply their own button and axis maps.</summary>
internal sealed unsafe class HidUsageReader : IDisposable
{
    private readonly ushort[] _usages;
    private readonly HidCapability[] _values;
    private nint _data;

    internal HidUsageReader(SafeFileHandle handle)
    {
        if (!NativeHid.HidD_GetPreparsedData(handle, out _data))
        {
            throw new IOException("The HID report descriptor is unavailable.");
        }

        try
        {
            if (NativeHid.HidP_GetCaps(_data, out var caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                throw new IOException("HID capabilities are unavailable.");
            }

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

        _ = NativeHid.HidD_FreePreparsedData(_data);
        _data = 0;
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
