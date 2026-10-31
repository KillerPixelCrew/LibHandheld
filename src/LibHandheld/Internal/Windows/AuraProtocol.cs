namespace LibHandheld.Internal.Windows;

/// <summary>Common ASUS Aura report fields; callers retain module-specific report lengths and ordering.</summary>
internal static class AuraProtocol
{
    internal const byte SpeedSlow = 0xEB;
    internal const byte SpeedMedium = 0xF5;
    internal const byte SpeedFast = 0xE1;

    internal static byte Speed(int percent)
    {
        return percent <= 33 ? SpeedSlow : percent <= 66 ? SpeedMedium : SpeedFast;
    }

    internal static byte[] Packet(byte reportId, int length, byte command, params byte[] arguments)
    {
        if (length < 2 || arguments.Length > length - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var report = new byte[length];
        report[0] = reportId;
        report[1] = command;
        arguments.CopyTo(report, 2);
        return report;
    }

    /// <summary>The native brightness level, 0 to 3, that a percentage is written as.</summary>
    internal static byte BrightnessLevel(int percent)
    {
        return (byte)Math.Round(Math.Clamp(percent, 0, 100) * 3d / 100);
    }

    /// <summary>The percentage a written brightness actually lands on, for publishing what was written.</summary>
    internal static int BrightnessPercent(int percent)
    {
        return (int)Math.Round(BrightnessLevel(percent) * 100d / 3);
    }

    internal static byte[] Brightness(byte reportId, int length, int percent)
    {
        return Packet(reportId, length, 0xBA, 0xC5, 0xC4, BrightnessLevel(percent));
    }

    internal static byte[] Color(byte reportId, int length, byte mode, byte zone, int primary, int secondary,
        byte speed, byte direction = 0, byte breathing = 0)
    {
        return Packet(reportId, length, 0xB3, zone, mode,
            (byte)(primary >> 16), (byte)(primary >> 8), (byte)primary, speed, direction, breathing,
            (byte)(secondary >> 16), (byte)(secondary >> 8), (byte)secondary);
    }
}
