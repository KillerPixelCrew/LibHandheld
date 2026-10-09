using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace LibHandheld.Internal.Transports;

internal readonly record struct IntelPowerSnapshot(ushort SustainedMirror, ushort BoostMirror, ulong PackageMsr);

internal sealed class IntelPowerTransport : ICpuPowerTransport
{
    internal const string KxDigest = "1695E6C7877979B1297179680E5AA3C767EAF13D27EE259D8F332EC60083EAE0";
    private readonly Lock _gate = new();
    private readonly FileStream _held;
    private readonly uint _mchbar;
    private readonly string _path;
    private readonly double _unitWatts;
    private bool _disposed;

    private IntelPowerTransport(string path, FileStream held, uint mchbar, double unitWatts)
    {
        _path = path;
        _held = held;
        _mchbar = mchbar;
        _unitWatts = unitWatts;
    }

    public CpuPowerLimits ReadLimits()
    {
        return Transaction(() =>
        {
            var snapshot = ReadSnapshotCore();
            return new CpuPowerLimits((snapshot.SustainedMirror & 0x7FFF) * _unitWatts,
                (snapshot.SustainedMirror & 0x7FFF) * _unitWatts,
                (snapshot.BoostMirror & 0x7FFF) * _unitWatts);
        });
    }

    public void WriteLimits(CpuPowerLimits limits)
    {
        var sustained = CpuPowerEncoding.IntelUnits(limits.SustainedWatts, _unitWatts);
        var boost = CpuPowerEncoding.IntelUnits(limits.BoostWatts, _unitWatts);
        if (Math.Abs(limits.SlowWatts - limits.SustainedWatts) > 0.001)
        {
            throw new ArgumentException("Intel package power has PL1 and PL2; its slow limit must equal PL1.",
                nameof(limits));
        }

        Transaction(() =>
        {
            WriteMemory(0x59A0, (ushort)(sustained | 0x8000));
            WriteMemory(0x59A4, (ushort)(boost | 0x8000));
            // HC's enabled package time windows. Set the value directly, regardless of whether the
            // optional observation path can read it back; accepted writes are published by the owner.
            WriteMsr(0x610, ((ulong)(0x00438000u | boost) << 32) | (0x00DD8000u | sustained));
            return true;
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _held.Dispose();
        }
    }

    // The caller supplies the already deployed, pinned KX payload. Constructors and device
    // detection never start a process; Open belongs only to the selected device's Start.
    internal static IntelPowerTransport Open(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "Resources", "Intel", "KX", "KX.exe");
        path = Path.GetFullPath(path);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(held)).Equals(KxDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Intel KX.exe does not match the pinned payload.");
            }

            using var msr = PawnIoModule.OpenLhm("IntelMSR");
            var rawUnit = msr.Execute("ioctl_read_msr", [0x606], 1)[0];
            var units = 1.0 / (1 << (int)(rawUnit & 0xF));
            var candidates = new[] { 0xFEDC0000, 0xFED10000 };
            foreach (var candidate in candidates)
            {
                var value = ParseReturn(Run(path, "/rdmem32", Hex(candidate)));
                if (value is not null && unchecked((uint)value.Value) != uint.MaxValue)
                {
                    return new IntelPowerTransport(path, held, candidate, units);
                }
            }

            throw new IOException("Intel MCHBAR did not respond at either documented base address.");
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    internal IntelPowerSnapshot ReadSnapshot()
    {
        return Transaction(ReadSnapshotCore);
    }

    internal void Restore(IntelPowerSnapshot original)
    {
        Transaction(() =>
        {
            WriteMemory(0x59A0, original.SustainedMirror);
            WriteMemory(0x59A4, original.BoostMirror);
            WriteMsr(0x610, original.PackageMsr);
            return true;
        });
    }

    internal int ReadGpuClock()
    {
        return Transaction(() => checked((int)MemoryReturn("/rdmem8", 0x5994) * 50));
    }

    internal void SetGpuClock(int megahertz)
    {
        if (megahertz is < 0 or > 12750 || megahertz % 50 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(megahertz));
        }

        Transaction(() =>
        {
            Run(_path, "/wrmem8", Hex(_mchbar + 0x5994), Hex((uint)(megahertz / 50)));
            return true;
        });
    }

    internal void SetUndervolt(int rail, int millivolts)
    {
        if (rail is < 0 or > 3 || millivolts is < -1000 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(millivolts));
        }

        var scaled = (Math.Abs(millivolts) * 1024 + 500) / 1000;
        var code = scaled == 0 ? 0u : (uint)(4096 - scaled * 2);
        var railIndex = rail == 3 ? 4 : rail;
        Transaction(() =>
        {
            WriteMsr(0x150, ((0x80000011UL | ((ulong)railIndex << 8)) << 32) | ((ulong)code << 20));
            return true;
        });
    }

    private IntelPowerSnapshot ReadSnapshotCore()
    {
        return new IntelPowerSnapshot(checked((ushort)MemoryReturn("/rdmem16", 0x59A0)),
            checked((ushort)MemoryReturn("/rdmem16", 0x59A4)),
            ParseMsr(Run(_path, "/rdmsr", "0x610")) ?? throw new InvalidDataException("KX did not return MSR 0x610."));
    }

    private long MemoryReturn(string operation, uint offset)
    {
        return ParseReturn(Run(_path, operation, Hex(_mchbar + offset)))
               ?? throw new InvalidDataException($"KX did not return MCHBAR offset 0x{offset:X}.");
    }

    private void WriteMemory(uint offset, ushort value)
    {
        var response = ParseReturn(Run(_path, "/wrmem16", Hex(_mchbar + offset), Hex(value)));
        if (response != value)
        {
            throw new IOException($"KX did not acknowledge the value submitted to MCHBAR offset 0x{offset:X}.");
        }
    }

    private void WriteMsr(uint index, ulong value)
    {
        var response = ParseReturn(Run(_path, "/wrmsr", Hex(index), Hex((uint)(value >> 32)), Hex((uint)value)));
        if (response != (uint)value)
        {
            throw new IOException($"KX did not acknowledge the value submitted to MSR 0x{index:X}.");
        }
    }

    private T Transaction<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HardwareBus.Run(@"Global\Access_PCI", TimeSpan.FromSeconds(2), operation);
        }
    }

    internal static long? ParseReturn(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var start = line.IndexOf("Return ", StringComparison.Ordinal);
            if (start >= 0 && long.TryParse(line[(start + 7)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    internal static ulong? ParseMsr(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var start = line.IndexOf("Msr Data", StringComparison.Ordinal);
            var colon = start < 0 ? -1 : line.IndexOf(':', start);
            if (colon < 0)
            {
                continue;
            }

            var words = line[(colon + 1)..]
                .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && uint.TryParse(words[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                                      NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out var high)
                                  && uint.TryParse(words[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                                      NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out var low))
            {
                return ((ulong)high << 32) | low;
            }
        }

        return null;
    }

    private static string Run(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Intel KX.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new TimeoutException("Intel KX did not complete within ten seconds; write completion is unknown.");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"Intel KX failed with exit {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}");
        }

        return output.GetAwaiter().GetResult();
    }

    private static string Hex(uint value)
    {
        return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
    }
}
