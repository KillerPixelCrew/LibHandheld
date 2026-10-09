using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.LegionGo;

internal readonly record struct LegionReport(CanonicalControllerSample Sample, ushort OemButtons);

internal static class LegionInputCodec
{
    public static bool TryDecode(LegionGoModel model, ReadOnlySpan<byte> report, DateTimeOffset now,
        bool rightGyro, bool nativeTouchpad, out LegionReport decoded)
    {
        decoded = default;
        if (model.Slim)
        {
            if (report.Length is not (32 or 33))
            {
                return false;
            }

            var buttons = CanonicalButtons.None;
            buttons |= (report[2] & 1) != 0 ? CanonicalButtons.RearPaddle1 : 0;
            buttons |= (report[2] & 2) != 0 ? CanonicalButtons.RearPaddle2 : 0;
            var touch = !nativeTouchpad && (report[8] & 0x80) != 0;
            buttons |= touch ? CanonicalButtons.RightPadTouch : 0;
            buttons |= !nativeTouchpad && (report[9] & 0x80) != 0 ? CanonicalButtons.RightPadClick : 0;
            decoded = new LegionReport(new CanonicalControllerSample
            {
                Timestamp = now, Buttons = buttons,
                RightPadX = touch ? Pad(BinaryPrimitives.ReadUInt16LittleEndian(report[2..])) : 0,
                RightPadY = touch ? -Pad(BinaryPrimitives.ReadUInt16LittleEndian(report[4..])) : 0,
                RightPadForce = touch ? 1 : 0,
                Motion = new MotionSample
                {
                    HasAccelerometer = true, HasGyro = true,
                    AccelX = -Le(report, 14) * (4f / short.MaxValue),
                    AccelY = -Le(report, 18) * (4f / short.MaxValue),
                    AccelZ = -Le(report, 16) * (4f / short.MaxValue),
                    GyroX = -Le(report, 20) * (2000f / short.MaxValue),
                    GyroY = -Le(report, 24) * (2000f / short.MaxValue),
                    GyroZ = Le(report, 22) * (2000f / short.MaxValue)
                }
            }, (ushort)(((report[0] & 2) != 0 ? 1 : 0) | ((report[0] & 1) != 0 ? 2 : 0)
                                                       | ((report[2] & 1) != 0 ? 4 : 0) |
                                                       ((report[2] & 2) != 0 ? 8 : 0)));
            return true;
        }

        if (report.Length < 60 || report[0] != 0x74)
        {
            return false;
        }

        var held = CanonicalButtons.None;
        held |= (report[18] & 0x20) != 0 ? CanonicalButtons.LeftStick : 0;
        held |= (report[18] & 0x10) != 0 ? CanonicalButtons.RightStick : 0;
        held |= (report[18] & 8) != 0 ? CanonicalButtons.DPadUp : 0;
        held |= (report[18] & 4) != 0 ? CanonicalButtons.DPadDown : 0;
        held |= (report[18] & 2) != 0 ? CanonicalButtons.DPadLeft : 0;
        held |= (report[18] & 1) != 0 ? CanonicalButtons.DPadRight : 0;
        held |= (report[19] & 0x80) != 0 ? CanonicalButtons.A : 0;
        held |= (report[19] & 0x40) != 0 ? CanonicalButtons.B : 0;
        held |= (report[19] & 0x20) != 0 ? CanonicalButtons.X : 0;
        held |= (report[19] & 0x10) != 0 ? CanonicalButtons.Y : 0;
        held |= (report[19] & 8) != 0 ? CanonicalButtons.LeftShoulder : 0;
        held |= (report[19] & 2) != 0 ? CanonicalButtons.RightShoulder : 0;
        // HHD's full tablet 0x74 map supplies these two fields HC reads through XInput instead.
        held |= (report[20] & 1) != 0 ? CanonicalButtons.Menu : 0;
        held |= (report[20] & 2) != 0 ? CanonicalButtons.View : 0;
        held |= (report[20] & 0x80) != 0 ? CanonicalButtons.RearPaddle1 : 0;
        held |= (report[20] & 0x40) != 0 ? CanonicalButtons.RearPaddle2 : 0;
        held |= (report[20] & 4) != 0 ? CanonicalButtons.RearPaddle3 : 0;
        held |= (report[20] & 0x20) != 0 ? CanonicalButtons.RearPaddle4 : 0;
        var x = BinaryPrimitives.ReadUInt16BigEndian(report[26..]);
        var y = BinaryPrimitives.ReadUInt16BigEndian(report[28..]);
        var touching = !nativeTouchpad && (x != 0 || y != 0);
        held |= touching ? CanonicalButtons.RightPadTouch : 0;
        var a = rightGyro ? 48 : 35;
        var g = rightGyro ? 54 : 41;
        var connected = report[rightGyro ? 13 : 12] is 2 or 3;
        var motion = connected
            ? new MotionSample
            {
                HasGyro = true, HasAccelerometer = true,
                AccelX = -Be(report, a + (rightGyro ? 2 : 0)) * (4f / short.MaxValue),
                AccelY = -Be(report, a + 4) * (4f / short.MaxValue),
                AccelZ = Be(report, a + (rightGyro ? 0 : 2)) * (rightGyro ? 1 : -1) * (4f / short.MaxValue),
                GyroX = -Be(report, g + (rightGyro ? 2 : 0)) * (2000f / short.MaxValue),
                GyroY = -Be(report, g + 4) * (2000f / short.MaxValue),
                GyroZ = Be(report, g + (rightGyro ? 0 : 2)) * (rightGyro ? 1 : -1) * (2000f / short.MaxValue)
            }
            : (MotionSample?)null;
        decoded = new LegionReport(new CanonicalControllerSample
        {
            Timestamp = now, Buttons = held,
            LeftStickX = Axis(report[14]), LeftStickY = -Axis(report[15]),
            RightStickX = Axis(report[16]), RightStickY = -Axis(report[17]),
            LeftTrigger = report[22] / 255f, RightTrigger = report[23] / 255f,
            RightPadX = touching ? Pad(x) : 0, RightPadY = touching ? -Pad(y) : 0,
            RightPadForce = touching ? 1 : 0, Motion = motion
        }, (ushort)(((report[18] & 0x40) != 0 ? 1 : 0) | ((report[18] & 0x80) != 0 ? 2 : 0)
                                                       | ((report[20] & 0x80) != 0 ? 4 : 0) |
                                                       ((report[20] & 0x40) != 0 ? 8 : 0)
                                                       | ((report[20] & 4) != 0 ? 16 : 0) |
                                                       ((report[20] & 0x20) != 0 ? 32 : 0)
                                                       | ((report[20] & 0x10) != 0 ? 64 : 0) |
                                                       ((report[20] & 8) != 0 ? 128 : 0)
                                                       | ((report[21] & 0x20) != 0 ? 256 : 0) |
                                                       ((report[21] & 0x40) != 0 ? 512 : 0)
                                                       | ((report[21] & 0x80) != 0 ? 1024 : 0)
                                                       | (report[25] == 129 ? 2048 : 0) |
                                                       (report[25] == 255 ? 4096 : 0)));
        return true;
    }

    public static CanonicalControllerSample Merge(CanonicalControllerSample gamepad, CanonicalControllerSample vendor)
    {
        const CanonicalButtons extra = CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2 |
                                       CanonicalButtons.RearPaddle3 | CanonicalButtons.RearPaddle4 |
                                       CanonicalButtons.RightPadTouch |
                                       CanonicalButtons.RightPadClick;
        return gamepad with
        {
            Buttons = gamepad.Buttons | (vendor.Buttons & extra), Motion = vendor.Motion,
            RightPadX = vendor.RightPadX, RightPadY = vendor.RightPadY, RightPadForce = vendor.RightPadForce
        };
    }

    private static short Le(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]);
    }

    private static short Be(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt16BigEndian(bytes[offset..]);
    }

    private static float Axis(byte value)
    {
        return Math.Clamp((value - 128) / 127f, -1, 1);
    }

    private static float Pad(ushort value)
    {
        return Math.Clamp(value / 500f - 1, -1, 1);
    }
}

internal static class LegionXInput
{
    public static int Find(LegionGoModel model)
    {
        var result = -1;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (StandardXInput.Capabilities(1, slot, 0, out var capabilities) == 0 &&
                capabilities.Vendor == model.VendorId && model.ProductIds.Contains(capabilities.Product))
            {
                if (result >= 0)
                {
                    return -1;
                }

                result = (int)slot;
            }
        }

        return result;
    }

    public static bool TryRead(int slot, DateTimeOffset now, out CanonicalControllerSample sample)
    {
        sample = default;
        if (slot < 0 || StandardXInput.StateEx((uint)slot, out var state) != 0)
        {
            return false;
        }

        sample = StandardControllerCodec.Decode(state.Gamepad, CanonicalButtons.None, null, now);
        return true;
    }

    public static bool Matches(int slot, LegionGoModel model)
    {
        try
        {
            return slot >= 0 && StandardXInput.Capabilities(1, (uint)slot, 0, out var capabilities) == 0 &&
                   capabilities.Vendor == model.VendorId && model.ProductIds.Contains(capabilities.Product);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static void Rumble(int slot, HapticOutputFrame frame)
    {
        if (slot < 0)
        {
            return;
        }

        var vibration = new StandardXInput.Vibration
        {
            Low = Motor(frame.LowFrequency), High = Motor(frame.HighFrequency)
        };
        if (StandardXInput.SetState((uint)slot, ref vibration) != 0)
        {
            throw new IOException("The physical Lenovo XInput rumble endpoint disconnected.");
        }
    }

    private static ushort Motor(float value)
    {
        return (ushort)(float.IsFinite(value) ? Math.Clamp(value, 0, 1) * ushort.MaxValue : 0);
    }
}
