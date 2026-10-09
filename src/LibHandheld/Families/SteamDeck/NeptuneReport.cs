using System.Buffers.Binary;
using LibHandheld.Contracts;

namespace LibHandheld.Families.SteamDeck;

/// <summary>Valve's Neptune report layout, shared by LCD and OLED firmware.</summary>
internal static class NeptuneReport
{
    internal static bool TryDecode(ReadOnlySpan<byte> report, DateTimeOffset timestamp,
        out CanonicalControllerSample sample)
    {
        sample = default;
        // Windows may prepend the unnumbered report ID. The firmware payload begins 01 00 09.
        if (report.Length == 65 && report[0] == 0)
        {
            report = report[1..];
        }

        if (report.Length != 64 || report[0] != 1 || report[1] != 0 || report[2] != 9 || report[3] is not (56 or 64))
        {
            return false;
        }

        var buttons = CanonicalButtons.None;
        Add(report[8], 0x80, CanonicalButtons.A);
        Add(report[8], 0x20, CanonicalButtons.B);
        Add(report[8], 0x40, CanonicalButtons.X);
        Add(report[8], 0x10, CanonicalButtons.Y);
        Add(report[8], 0x08, CanonicalButtons.LeftShoulder);
        Add(report[8], 0x04, CanonicalButtons.RightShoulder);
        Add(report[9], 0x01, CanonicalButtons.DPadUp);
        Add(report[9], 0x08, CanonicalButtons.DPadDown);
        Add(report[9], 0x04, CanonicalButtons.DPadLeft);
        Add(report[9], 0x02, CanonicalButtons.DPadRight);
        Add(report[9], 0x10, CanonicalButtons.View);
        Add(report[9], 0x40, CanonicalButtons.Menu);
        Add(report[9], 0x20, CanonicalButtons.Guide);
        Add(report[14], 0x04, CanonicalButtons.QuickAccess);
        Add(report[13], 0x02, CanonicalButtons.RearPaddle1);
        Add(report[13], 0x04, CanonicalButtons.RearPaddle2);
        Add(report[9], 0x80, CanonicalButtons.RearPaddle3);
        Add(report[10], 0x01, CanonicalButtons.RearPaddle4);
        Add(report[10], 0x40, CanonicalButtons.LeftStick);
        Add(report[11], 0x04, CanonicalButtons.RightStick);
        Add(report[13], 0x40, CanonicalButtons.LeftStickTouch);
        Add(report[13], 0x80, CanonicalButtons.RightStickTouch);
        Add(report[10], 0x08, CanonicalButtons.LeftPadTouch);
        Add(report[10], 0x10, CanonicalButtons.RightPadTouch);
        Add(report[10], 0x02, CanonicalButtons.LeftPadClick);
        Add(report[10], 0x04, CanonicalButtons.RightPadClick);
        sample = new CanonicalControllerSample
        {
            Timestamp = timestamp, Buttons = buttons,
            LeftStickX = Signed(report, 48), LeftStickY = Signed(report, 50),
            RightStickX = Signed(report, 52), RightStickY = Signed(report, 54),
            LeftTrigger = Positive(report, 44), RightTrigger = Positive(report, 46),
            LeftPadX = (buttons & CanonicalButtons.LeftPadTouch) != 0 ? Signed(report, 16) : 0,
            LeftPadY = (buttons & CanonicalButtons.LeftPadTouch) != 0 ? Signed(report, 18) : 0,
            RightPadX = (buttons & CanonicalButtons.RightPadTouch) != 0 ? Signed(report, 20) : 0,
            RightPadY = (buttons & CanonicalButtons.RightPadTouch) != 0 ? Signed(report, 22) : 0,
            LeftPadForce = Positive(report, 56), RightPadForce = Positive(report, 58),
            LeftStickForce = (buttons & CanonicalButtons.LeftStickTouch) != 0 ? 1 : 0,
            RightStickForce = (buttons & CanonicalButtons.RightStickTouch) != 0 ? 1 : 0,
            Motion = new MotionSample
            {
                HasGyro = true, HasAccelerometer = true, SensorTimestamp = timestamp,
                GyroX = Signed(report, 30) * 2000, GyroY = Signed(report, 34) * 2000,
                GyroZ = -Signed(report, 32) * 2000,
                AccelX = Signed(report, 24) * 2, AccelY = Signed(report, 28) * 2, AccelZ = -Signed(report, 26) * 2
            }
        };
        return true;

        void Add(byte value, byte mask, CanonicalButtons button)
        {
            if ((value & mask) != 0)
            {
                buttons |= button;
            }
        }
    }

    private static float Signed(ReadOnlySpan<byte> value, int offset)
    {
        return Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(value[offset..]) / 32767f, -1, 1);
    }

    private static float Positive(ReadOnlySpan<byte> value, int offset)
    {
        return Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(value[offset..]) / 32767f, 0, 1);
    }

    internal static void EncodeRumble(Span<byte> packet, HapticOutputFrame frame)
    {
        packet.Clear();
        packet[1] = 0xEB;
        packet[2] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], Speed(frame.LowFrequency));
        BinaryPrimitives.WriteUInt16LittleEndian(packet[8..], Speed(frame.HighFrequency));
        packet[10] = 2;

        static ushort Speed(float value)
        {
            return float.IsFinite(value)
                ? checked((ushort)Math.Round(Math.Clamp(value, 0, 1) * ushort.MaxValue))
                : (ushort)0;
        }
    }
}
