using System.Runtime.InteropServices;

namespace LibHandheld.Families.Ayaneo;

internal static class AyaneoPowerSource
{
    internal static byte? Read()
    {
        return GetSystemPowerStatus(out var status) && status.AcLine is 0 or 1 ? status.AcLine : null;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLine;
        public byte BatteryFlags;
        public byte ChargePercent;
        public byte SystemFlags;
        public uint BatteryLifeTime;
        public uint FullBatteryLifeTime;
    }
}
