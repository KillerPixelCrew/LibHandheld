namespace LibHandheld.Internal.Transports;

internal readonly record struct CpuPowerLimits(double SustainedWatts, double SlowWatts, double BoostWatts);

internal interface ICpuPowerTransport : IDisposable
{
    CpuPowerLimits ReadLimits();
    void WriteLimits(CpuPowerLimits limits);
}

internal enum CpuPowerLimit
{
    Sustained,
    Slow,
    Boost
}

internal readonly record struct SmuMailbox(uint Command, uint Response, uint Arguments);

internal readonly record struct SmuPowerCommands(uint Sustained, uint Slow, uint Boost);

internal static class CpuPowerEncoding
{
    internal static uint Milliwatts(double watts)
    {
        if (!double.IsFinite(watts) || watts <= 0 || watts * 1000 > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        var value = checked((uint)Math.Round(watts * 1000));
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        return value;
    }

    internal static ushort IntelUnits(double watts, double unitWatts)
    {
        if (!double.IsFinite(watts) || !double.IsFinite(unitWatts) || watts <= 0 || unitWatts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        var units = Math.Round(watts / unitWatts);
        if (units is <= 0 or > 0x7FFF)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        return (ushort)units;
    }
}
