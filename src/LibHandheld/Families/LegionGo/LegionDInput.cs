using System.Runtime.InteropServices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.LegionGo;

/// <summary>Windows descriptor-decoded Legion DirectInput state; no assumed input-report byte layout.</summary>
internal sealed unsafe class LegionDInput : IDisposable
{
    private const int Success = 0x00110000;
    private readonly ushort[] _buttons = new ushort[64];
    private readonly Value[] _values;
    private nint _data;

    public LegionDInput(SafeFileHandle handle)
    {
        if (!NativeHid.HidD_GetPreparsedData(handle, out _data))
        {
            throw new IOException("Lenovo gamepad HID descriptor unavailable.");
        }

        try
        {
            if (NativeHid.HidP_GetCaps(_data, out var caps) != Success)
            {
                throw new IOException("Lenovo gamepad HID capabilities unavailable.");
            }

            _values = new Value[caps.NumberInputValueCaps];
            var length = (ushort)_values.Length;
            fixed (Value* values = _values)
            {
                if (HidP_GetValueCaps(0, values, ref length, _data) != Success)
                {
                    throw new IOException("Lenovo gamepad value ranges unavailable.");
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_data != 0)
        {
            _ = NativeHid.HidD_FreePreparsedData(_data);
            _data = 0;
        }
    }

    public bool TryDecode(ReadOnlySpan<byte> report, DateTimeOffset timestamp, out CanonicalControllerSample sample)
    {
        sample = default;
        if (!Read(report, 0x30, out var x) || !Read(report, 0x31, out var y) ||
            !Read(report, 0x32, out var z) || !Read(report, 0x35, out var rz))
        {
            return false;
        }

        var held = CanonicalButtons.None;
        fixed (byte* bytes = report)
        fixed (ushort* buttons = _buttons)
        {
            var count = (uint)_buttons.Length;
            if (HidP_GetUsages(0, 9, 0, buttons, ref count, _data, bytes, (uint)report.Length) == Success)
            {
                for (var i = 0; i < count; i++)
                {
                    held |= Button(_buttons[i] - 1);
                }
            }
        }

        if (Read(report, 0x39, out var hat))
        {
            held |= (hat.Raw - hat.Minimum) switch
            {
                0 => CanonicalButtons.DPadUp, 1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
                2 => CanonicalButtons.DPadRight, 3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
                4 => CanonicalButtons.DPadDown, 5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
                6 => CanonicalButtons.DPadLeft, 7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp, _ => 0
            };
        }

        sample = new CanonicalControllerSample
        {
            Timestamp = timestamp, Buttons = held, LeftStickX = x.Signed, LeftStickY = -y.Signed,
            RightStickX = z.Signed, RightStickY = -rz.Signed,
            LeftTrigger = Read(report, 0x33, out var rx) ? rx.Unsigned : 0,
            RightTrigger = Read(report, 0x34, out var ry) ? ry.Unsigned : 0
        };
        return true;
    }

    internal static CanonicalButtons Button(int index)
    {
        return index switch
        {
            0 => CanonicalButtons.A, 1 => CanonicalButtons.B, 3 => CanonicalButtons.X, 4 => CanonicalButtons.Y,
            6 => CanonicalButtons.LeftShoulder, 7 => CanonicalButtons.RightShoulder,
            10 => CanonicalButtons.View, 11 => CanonicalButtons.Menu,
            13 => CanonicalButtons.LeftStick, 14 => CanonicalButtons.RightStick, _ => 0
        };
    }

    private bool Read(ReadOnlySpan<byte> report, ushort usage, out Reading reading)
    {
        reading = default;
        if (_data == 0)
        {
            return false;
        }

        foreach (var value in _values)
        {
            if (value.Page != 1 ||
                (value.Range == 0 ? value.First != usage : usage < value.First || usage > value.Last))
            {
                continue;
            }

            uint raw;
            fixed (byte* bytes = report)
            {
                if (HidP_GetUsageValue(0, 1, 0, usage, out raw, _data, bytes, (uint)report.Length) != Success)
                {
                    return false;
                }
            }

            var number = value.Minimum < 0 && value.Bits is > 0 and < 32 && (raw & (1u << (value.Bits - 1))) != 0
                ? (int)(raw | (uint.MaxValue << value.Bits))
                : (int)raw;
            reading = new Reading(number, value.Minimum, value.Maximum);
            return true;
        }

        return false;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int kind, Value* values, ref ushort count, nint data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int kind, ushort page, ushort link, ushort* usages, ref uint count,
        nint data, byte* report, uint length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int kind, ushort page, ushort link, ushort usage, out uint value,
        nint data, byte* report, uint length);

    private readonly record struct Reading(int Raw, int Minimum, int Maximum)
    {
        public float Signed =>
            Maximum > Minimum ? Math.Clamp((Raw - Minimum) * 2f / (Maximum - Minimum) - 1, -1, 1) : 0;

        public float Unsigned => Maximum > Minimum ? Math.Clamp((Raw - Minimum) / (float)(Maximum - Minimum), 0, 1) : 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct Value
    {
        [FieldOffset(0)] public ushort Page;
        [FieldOffset(12)] public byte Range;
        [FieldOffset(18)] public ushort Bits;
        [FieldOffset(40)] public int Minimum;
        [FieldOffset(44)] public int Maximum;
        [FieldOffset(56)] public ushort First;
        [FieldOffset(58)] public ushort Last;
    }
}
