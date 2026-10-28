namespace LibHandheld.Internal.Transports;

/// <summary>Intel package power limits through the signed, allowlisted PawnIO IntelMSR module.</summary>
/// <remarks>
///     Only MSR_PKG_POWER_LIMIT (0x610) is written. The module has no MCHBAR write, so the MMIO mirror
///     at MCHBAR 0x59A0/0x59A4 that Handheld Companion also updates is left as the firmware set it.
///     Each write replaces only the PL1 and PL2 power fields, sets their enable bits and keeps every
///     other bit of the current register. It is sent once and never retried.
/// </remarks>
internal sealed class IntelMsrPowerTransport : ICpuPowerTransport
{
    internal const long PowerUnitMsr = 0x606;
    internal const long PackagePowerLimitMsr = 0x610;
    private const ulong Pl1Power = 0x7FFFUL;
    private const ulong Pl1Enable = 1UL << 15;
    private const ulong Pl2Power = 0x7FFFUL << 32;
    private const ulong Pl2Enable = 1UL << 47;
    private const ulong Locked = 1UL << 63;
    private readonly Lock _gate = new();
    private readonly IPawnIoModule _module;
    private readonly double _unitWatts;
    private bool _disposed;

    internal IntelMsrPowerTransport(IPawnIoModule module)
    {
        _module = module;
        _unitWatts = UnitWatts(ReadMsr(module, PowerUnitMsr));
    }

    public CpuPowerLimits ReadLimits()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Decode(ReadMsr(_module, PackagePowerLimitMsr), _unitWatts);
        }
    }

    public void WriteLimits(CpuPowerLimits limits)
    {
        if (Math.Abs(limits.SlowWatts - limits.BoostWatts) > 0.001)
        {
            throw new ArgumentException(
                "Intel package power has PL1 and PL2; its slow and boost limits both address PL2.",
                nameof(limits));
        }

        var sustained = CpuPowerEncoding.IntelUnits(limits.SustainedWatts, _unitWatts);
        var boost = CpuPowerEncoding.IntelUnits(limits.BoostWatts, _unitWatts);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The current register supplies the time windows, clamp and lock bits this write keeps.
            var value = Compose(ReadMsr(_module, PackagePowerLimitMsr), sustained, boost);
            _module.Execute("ioctl_write_msr", [PackagePowerLimitMsr, unchecked((long)value)], outputWords: 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _module.Dispose();
        }
    }

    // Constructors and device detection never open a driver; Open belongs only to the selected device's Start.
    internal static IntelMsrPowerTransport Open()
    {
        var module = PawnIoModule.OpenModule("IntelMSR");
        try
        {
            return new IntelMsrPowerTransport(module);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    /// <summary>MSR_RAPL_POWER_UNIT bits 3:0 give the power unit as 1/2^n W.</summary>
    internal static double UnitWatts(ulong raplPowerUnit)
    {
        return 1.0 / (1UL << (int)(raplPowerUnit & 0xF));
    }

    internal static CpuPowerLimits Decode(ulong packagePowerLimit, double unitWatts)
    {
        var sustained = (packagePowerLimit & Pl1Power) * unitWatts;
        var boost = ((packagePowerLimit & Pl2Power) >> 32) * unitWatts;
        return new CpuPowerLimits(sustained, boost, boost);
    }

    /// <summary>Replaces the PL1 and PL2 power fields and enables both, keeping every other bit.</summary>
    /// <exception cref="InvalidOperationException">The firmware locked the register until reset.</exception>
    internal static ulong Compose(ulong current, ushort sustainedUnits, ushort boostUnits)
    {
        if ((current & Locked) != 0)
        {
            throw new InvalidOperationException(
                "The firmware locked Intel package power limits (MSR 0x610 bit 63); nothing was written.");
        }

        var kept = current & ~(Pl1Power | Pl2Power);
        return kept | Pl1Enable | Pl2Enable | (sustainedUnits & Pl1Power) | (((ulong)boostUnits << 32) & Pl2Power);
    }

    private static ulong ReadMsr(IPawnIoModule module, long msr)
    {
        var reply = module.Execute("ioctl_read_msr", [msr], outputWords: 1);
        return reply.Length == 1
            ? unchecked((ulong)reply[0])
            : throw new IOException($"The IntelMSR module returned no value for MSR 0x{msr:X}.");
    }
}
