using System.Diagnostics;
using System.Globalization;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Transports;

/// <summary>Intel package power limits in the MCHBAR mirror, through Handheld Companion's pinned KX helper.</summary>
/// <remarks>
///     MCHBAR 0x59A0/0x59A4 hold PL1/PL2 in eighth-watts. The package enforces the lower of this mirror
///     and MSR 0x610, so a limit above the mirror needs both. KX loads HwRwDrv, which Windows refuses
///     while Memory Integrity or the vulnerable-driver blocklist is on. KX reports the value it read or
///     wrote on a "Return" line and as its exit code, so a nonzero exit is not a failure. Writes are sent
///     once and never retried. KX loads HwRwDrv for one command and unloads it on exit (2026-10-31), and
///     opening this transport only verifies the pinned file, so the driver is present only while a
///     selected KX operation runs.
/// </remarks>
internal sealed class IntelKxMchbar : IDisposable
{
    internal const string KxDigest = "1695E6C7877979B1297179680E5AA3C767EAF13D27EE259D8F332EC60083EAE0";
    private const uint Pl1Offset = 0x59A0;
    private const uint Pl2Offset = 0x59A4;
    private const double UnitWatts = 0.125;
    private static readonly uint[] Bases = [0xFEDC0000, 0xFED10000];
    private readonly Lock _gate = new();
    private readonly FileStream _held;
    private readonly string _path;
    private uint? _base;
    private bool _disposed;

    private IntelKxMchbar(string path, FileStream held)
    {
        _path = path;
        _held = held;
    }

    /// <summary>Reads PL1/PL2 from the mirror, in watts.</summary>
    internal CpuPowerLimits ReadLimits()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mchbar = Base();
            var sustained = (Read(mchbar + Pl1Offset) & 0x7FFF) * UnitWatts;
            var boost = (Read(mchbar + Pl2Offset) & 0x7FFF) * UnitWatts;
            return new CpuPowerLimits(sustained, boost, boost);
        }
    }

    /// <summary>Writes enabled PL1 and PL2 as Handheld Companion does, PL2 first when it rises.</summary>
    internal void WriteLimits(double sustainedWatts, double boostWatts)
    {
        var sustained = (ushort)(CpuPowerEncoding.IntelUnits(sustainedWatts, UnitWatts) | 0x8000);
        var boost = (ushort)(CpuPowerEncoding.IntelUnits(boostWatts, UnitWatts) | 0x8000);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mchbar = Base();
            Write(mchbar + Pl1Offset, sustained);
            Write(mchbar + Pl2Offset, boost);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _held.Dispose();
        }
    }

    /// <summary>Verifies and holds the pinned payload without running it; no driver is loaded here.</summary>
    internal static IntelKxMchbar Open(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        return new IntelKxMchbar(path, PinnedPayload.OpenHeld(path, KxDigest));
    }

    /// <summary>Finds the MCHBAR base on first use; the Atlas answers at 0xFEDC0000 and not at 0xFED10000.</summary>
    private uint Base()
    {
        if (_base is { } known)
        {
            return known;
        }

        foreach (var candidate in Bases)
        {
            var value = Parse(Run(_path, "/rdmem32", Hex(candidate + Pl1Offset)));
            if (value is { } read && unchecked((uint)read) != uint.MaxValue)
            {
                _base = candidate;
                return candidate;
            }
        }

        throw new IOException(
            "KX could not read MCHBAR; HwRwDrv needs Memory Integrity and the vulnerable-driver blocklist off.");
    }

    private long Read(uint address)
    {
        return Parse(Run(_path, "/rdmem16", Hex(address)))
               ?? throw new IOException($"KX returned no value for 0x{address:X8}.");
    }

    private void Write(uint address, ushort value)
    {
        if (Parse(Run(_path, "/wrmem16", Hex(address), Hex(value))) != value)
        {
            throw new IOException($"KX did not acknowledge 0x{value:X4} at 0x{address:X8}.");
        }
    }

    internal static long? Parse(string output)
    {
        foreach (var line in output.Split('\n'))
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

        using var process = Process.Start(start) ?? throw new IOException("Intel KX did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new TimeoutException("Intel KX did not complete within ten seconds; write completion is unknown.");
        }

        return output.GetAwaiter().GetResult();
    }

    private static string Hex(uint value)
    {
        return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
    }
}
