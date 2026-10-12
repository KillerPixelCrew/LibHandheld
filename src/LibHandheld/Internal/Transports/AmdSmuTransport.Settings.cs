namespace LibHandheld.Internal.Transports;

internal enum AmdCurveKind
{
    AllCores,
    PerCore,
    Gpu
}

internal enum AmdThermalLimit
{
    Tctl,
    Chtc,
    ApuSkin
}

internal sealed partial class AmdSmuTransport
{
    internal bool SupportsGpuClock => SupportsGpuClockFor(CodeName);
    internal bool SupportsAutomaticGpuClock => CodeName is 2 or 6 or 7 or 15;

    internal void RestoreGpuClockRange(int minimum, int maximum)
    {
        if (!SupportsAutomaticGpuClock || minimum <= 0 || maximum < minimum || maximum > 12750)
        {
            throw new NotSupportedException("This processor has no supported automatic GPU clock range.");
        }

        Transaction(() =>
        {
            var low = checked((uint)minimum);
            var high = checked((uint)maximum);
            RequireEcho(0x47, low, SendModuleCore(0x47, [low]));
            RequireEcho(0x46, high, SendModuleCore(0x46, [high]));
            return true;
        });
    }

    internal bool SupportsCurve(AmdCurveKind kind)
    {
        return CurveCommandFor(CodeName, kind) != 0;
    }

    internal bool SupportsThermalLimit(AmdThermalLimit limit)
    {
        return ThermalCommandFor(CodeName, limit) != 0;
    }

    internal bool SupportsThermalRead(AmdThermalLimit limit)
    {
        return ThermalReadCommandFor(CodeName, limit) != 0;
    }

    internal void WriteCurve(AmdCurveKind kind, int steps)
    {
        var command = CurveCommandFor(CodeName, kind);
        RequireCommand(command);
        var encoded = EncodeCurveOffset(steps);
        // HC uses the PawnIO module path for curve commands, rather than its power mailbox.
        // Its per-core entry point carries the same low 20 bits and therefore addresses core 0.
        Transaction(() => RequireEcho(command, encoded, SendModuleCore(command, [encoded])));
    }

    internal void WriteThermalLimit(AmdThermalLimit limit, int celsius)
    {
        var command = ThermalCommandFor(CodeName, limit);
        RequireCommand(command);
        var encoded = EncodeThermalLimit(limit, celsius);
        RequireEcho(command, encoded, Send(command, [encoded]));
    }

    internal int ReadThermalLimit(AmdThermalLimit limit)
    {
        var command = ThermalReadCommandFor(CodeName, limit);
        RequireCommand(command);
        var response = Send(command, []);
        if (response.Length == 0 || response[0] is < 30 or > 120)
        {
            throw new InvalidDataException("AMD did not return a valid thermal limit.");
        }

        return checked((int)response[0]);
    }

    internal void WriteGpuClock(int megahertz)
    {
        if (!SupportsGpuClock)
        {
            throw new NotSupportedException("This AMD processor has no documented GPU clock command.");
        }

        if (megahertz is < 1 or > 12750)
        {
            throw new ArgumentOutOfRangeException(nameof(megahertz));
        }

        Transaction(() =>
        {
            var encoded = checked((uint)megahertz);
            if (CodeName is 2 or 6 or 7 or 15)
            {
                RequireEcho(0x47, encoded, SendModuleCore(0x47, [encoded]));
                RequireEcho(0x46, encoded, SendModuleCore(0x46, [encoded]));
            }
            else
            {
                RequireEcho(0x89, encoded, SendModuleCore(0x89, [encoded]));
            }

            return true;
        });
    }

    internal static bool SupportsGpuClockFor(uint codeName)
    {
        return codeName is 1 or 2 or 6 or 7 or 10 or 12 or 13 or 15 or 16 or 17 or 22 or 23 or 24 or 25
            or 28 or 30 or 31 or 32 or 33;
    }

    // Command selection follows HC 1.3.3.1 RyzenSmuService. Unknown/new codenames never inherit
    // another processor's command IDs, even when their power mailbox layout is the same.
    internal static uint CurveCommandFor(uint codeName, AmdCurveKind kind)
    {
        return (kind, codeName) switch
        {
            (AmdCurveKind.AllCores, 1 or 13 or 22) => 0x55,
            (AmdCurveKind.AllCores, 10 or 12 or 23 or 24 or 25 or 30 or 31 or 32 or 33) => 0x4C,
            (AmdCurveKind.AllCores, 16 or 17 or 28) => 0x07,
            (AmdCurveKind.PerCore, 1 or 13 or 22) => 0x54,
            (AmdCurveKind.PerCore, 10 or 12 or 23 or 24 or 25 or 30 or 31 or 32 or 33) => 0x4B,
            (AmdCurveKind.PerCore, 16 or 17 or 28) => 0x06,
            (AmdCurveKind.Gpu, 1 or 13 or 22) => 0x64,
            (AmdCurveKind.Gpu, 10 or 12 or 23 or 24 or 30 or 33) => 0xB7,
            _ => 0
        };
    }

    internal static uint ThermalCommandFor(uint codeName, AmdThermalLimit limit)
    {
        return (limit, codeName) switch
        {
            (AmdThermalLimit.Tctl, 2 or 6 or 7 or 15) => 0x1F,
            (AmdThermalLimit.Tctl, 1 or 10 or 12 or 13 or 22 or 23 or 24 or 25 or 30 or 31 or 32 or 33) => 0x19,
            (AmdThermalLimit.Tctl, 16 or 17 or 28) => 0x3F,
            (AmdThermalLimit.Chtc, 10 or 23 or 24 or 25 or 30 or 31 or 32 or 33 or 34) => 0x63,
            (AmdThermalLimit.ApuSkin, 1 or 13 or 22) => 0x38,
            (AmdThermalLimit.ApuSkin, 10 or 12 or 23 or 24 or 25 or 30 or 31 or 32 or 33 or 34) => 0x33,
            _ => 0
        };
    }

    internal static uint ThermalReadCommandFor(uint codeName, AmdThermalLimit limit)
    {
        return (limit, codeName) switch
        {
            (AmdThermalLimit.Tctl, 2 or 6 or 7 or 15) => 0x33,
            (AmdThermalLimit.Tctl, 16 or 17 or 28) => 0x59,
            (AmdThermalLimit.Chtc, 2 or 6 or 7 or 15) => 0x56,
            (AmdThermalLimit.Chtc, 1 or 10 or 12 or 13 or 22 or 23 or 24 or 25 or 30 or 31 or 32 or 33 or 34) => 0x37,
            (AmdThermalLimit.Chtc, 16 or 17 or 28) => 0x59,
            _ => 0
        };
    }

    internal static uint EncodeCurveOffset(int steps)
    {
        if (steps is < -24 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        return unchecked((uint)steps) & 0xFFFFF;
    }

    internal static uint EncodeThermalLimit(AmdThermalLimit limit, int celsius)
    {
        if (celsius is < 30 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(celsius));
        }

        return limit == AmdThermalLimit.ApuSkin ? checked((uint)celsius * 256) : checked((uint)celsius);
    }

    private static bool RequireEcho(uint command, uint value, uint[] response)
    {
        if (response.Length == 0 || response[0] != value)
        {
            throw new IOException($"AMD SMU command 0x{command:X} did not acknowledge its submitted value.");
        }

        return true;
    }

    private static void RequireCommand(uint command)
    {
        if (command == 0)
        {
            throw new NotSupportedException("This AMD processor has no documented command for this setting.");
        }
    }
}
