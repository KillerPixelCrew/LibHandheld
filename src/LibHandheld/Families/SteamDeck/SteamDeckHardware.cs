using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.SteamDeck;

internal interface ISteamDeckMemory : IDisposable
{
    uint Read(uint address, int width);
    void Write(uint address, uint value, int width);
    void WriteFanMode(byte mode);
    void AuthorizeGpuRange(uint mmioBase);
}

/// <summary>Valve's admitted EC fields, GPU mailbox words and fan-mode port over the pinned InpOut transport.</summary>
internal sealed class SteamDeckMemory(InpOutTransport inpOut) : ISteamDeckMemory
{
    private readonly Lock _gate = new();
    private uint? _gpuBase;

    public uint Read(uint address, int width)
    {
        return Map(address, width, pointer => width switch
        {
            8 => Marshal.ReadByte(pointer), 16 => unchecked((ushort)Marshal.ReadInt16(pointer)),
            32 => unchecked((uint)Marshal.ReadInt32(pointer)),
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        });
    }

    public void Write(uint address, uint value, int width)
    {
        Map(address, width, pointer =>
        {
            switch (width)
            {
                case 8: Marshal.WriteByte(pointer, checked((byte)value)); break;
                case 16: Marshal.WriteInt16(pointer, unchecked((short)checked((ushort)value))); break;
                case 32: Marshal.WriteInt32(pointer, unchecked((int)value)); break;
            }

            return true;
        });
    }

    public void WriteFanMode(byte mode)
    {
        if (mode is not (0xCC or 0xCD))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        inpOut.WritePortBytes([[0x6C, 0, mode]], "Valve fan-mode dispatch failed; completion is unknown.");
    }

    public void AuthorizeGpuRange(uint mmioBase)
    {
        if (mmioBase is not (0x80300000 or 0x80500000 or 0x80600000))
        {
            throw new ArgumentOutOfRangeException(nameof(mmioBase));
        }

        lock (_gate)
        {
            _gpuBase = mmioBase;
        }
    }

    public void Dispose()
    {
        inpOut.Dispose();
    }

    internal static SteamDeckMemory Open(string? path)
    {
        return new SteamDeckMemory(InpOutTransport.Open(path));
    }

    private T Map<T>(uint address, int width, Func<nint, T> action)
    {
        if (width is not (8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        lock (_gate)
        {
            if (!IsAllowed(address, width, _gpuBase))
            {
                throw new ArgumentOutOfRangeException(nameof(address),
                    "Only the selected Valve mailbox and documented EC fields may be mapped.");
            }

            return inpOut.Map(address, checked((uint)(width / 8)), action);
        }
    }

    internal static bool IsAllowed(uint address, int width, uint? gpuBase)
    {
        return (gpuBase is { } selected && width == 32 && (address == selected + 0x1629A * 4
                                                           || address == selected + 0x16282 * 4 ||
                                                           address == selected + 0x16292 * 4))
               || (address, width) is (0xFE700C4C, 16) or (0xFE7003BD, 8) or (0xFE700C01, 8)
               or (0xFE700B92, 16) or (0xFE700B95, 16) or (0xFE700B97, 16)
               or (0xFE7003B0, 16) or (0xFE70046E, 16) or (0xFE700B9F, 32) or (0xFE700B9F, 8);
    }
}

internal sealed class VangoghPower(ISteamDeckMemory memory, uint mmioBase) : ICpuPowerTransport
{
    private const uint Response = 0x1629A * 4;
    private const uint Command = 0x16282 * 4;
    private const uint Argument = 0x16292 * 4;

    private static readonly GpuRange[] GpuRanges =
    [
        new("AMD Custom GPU 0405", 0x80300000, [0x043F3900, 0x043F3C05, 0x043F3E00, 0x063F0F00]),
        new("AMD Custom GPU 0932", 0x80500000, [0x063F0F00]),
        new("AMD Custom GPU 0932", 0x80600000, [0x063F0E00, 0x063F0A00]),
        new("AMD Radeon 670M", 0x80300000, [0x043F3900, 0x043F3C05, 0x043F3E00]),
        new("AMD Radeon RX 670 Graphics", 0x80300000, [0x043F3900, 0x043F3C05, 0x043F3E00])
    ];

    // The machine has one SMU mailbox. The clock service and the profile power transport each hold a
    // VangoghPower, so one gate for every instance keeps their command sequences from interleaving.
    private static readonly Lock Mailbox = new();

    public CpuPowerLimits ReadLimits()
    {
        return Transaction(() =>
        {
            var slow = ExchangeCore(0x4C, 0) / 1000d;
            var fast = ExchangeCore(0x4B, 0) / 1000d;
            if (slow is <= 0 or > 21 || fast is <= 0 or > 21)
            {
                throw new InvalidDataException("The Valve SMU returned invalid PPT limits.");
            }

            return new CpuPowerLimits(slow, fast, fast);
        });
    }

    public void WriteLimits(CpuPowerLimits limits)
    {
        var slow = EncodeWatts(limits.SustainedWatts);
        var fast = EncodeWatts(limits.BoostWatts);
        if (slow > fast)
        {
            throw new ArgumentException("The slow PPT limit cannot exceed the fast limit.");
        }

        Transaction(() =>
        {
            ExchangeCore(0x4A, slow);
            ExchangeCore(0x49, fast);
            return true;
        });
    }

    public void Dispose()
    {
        lock (Mailbox)
        {
            memory.Dispose();
        }
    }

    internal static VangoghPower Open(string? path)
    {
        var memory = SteamDeckMemory.Open(path);
        try
        {
            var candidates = PresentGpuRanges();
            foreach (var candidate in candidates)
            {
                memory.AuthorizeGpuRange(candidate.Base);
                var transport = new VangoghPower(memory, candidate.Base);
                if (candidate.Versions.Contains(transport.Exchange(2, 0)))
                {
                    return transport;
                }
            }

            throw new NotSupportedException("No exact Valve GPU resource range and supported SMU firmware matched.");
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    internal uint Exchange(uint command, uint value)
    {
        return Transaction(() => ExchangeCore(command, value));
    }

    private uint ExchangeCore(uint command, uint value)
    {
        var expires = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 20;
        if (WaitResponse(expires) != 1)
        {
            throw new IOException("The Valve SMU mailbox is not ready; no command was dispatched.");
        }

        HardwareBus.Run(HardwareBus.Pci, TimeSpan.FromSeconds(2), () =>
        {
            memory.Write(mmioBase + Response, 0, 32);
            memory.Write(mmioBase + Argument, value, 32);
            memory.Write(mmioBase + Command, command, 32);
            return true;
        });
        var status = WaitResponse(expires);
        if (status != 1)
        {
            // A terminal firmware rejection is known complete. A zero/timeout is never reset or retried.
            if (status is 0xFC or 0xFD or 0xFE or 0xFF)
            {
                memory.Write(mmioBase + Response, 1, 32);
            }

            throw new SmuCommandException(command, status);
        }

        return memory.Read(mmioBase + Argument, 32);
    }

    private uint WaitResponse(long expires)
    {
        for (;;)
        {
            var value = HardwareBus.Run(HardwareBus.Pci, TimeSpan.FromSeconds(2),
                () => memory.Read(mmioBase + Response, 32));
            if (value != 0)
            {
                return value;
            }

            if (Stopwatch.GetTimestamp() >= expires)
            {
                throw new TimeoutException("The Valve SMU mailbox did not complete; the command is not retried.");
            }

            Thread.Sleep(1);
        }
    }

    private T Transaction<T>(Func<T> action)
    {
        lock (Mailbox)
        {
            return action();
        }
    }

    internal static uint EncodeWatts(double watts)
    {
        if (!double.IsFinite(watts) || watts is < 3 or > 21)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        return checked((uint)Math.Round(watts * 1000));
    }

    private static IReadOnlyList<GpuRange> PresentGpuRanges()
    {
        List<GpuRange> candidates = [];
        using var videos = new ManagementObjectSearcher("SELECT Name,PNPDeviceID FROM Win32_VideoController");
        using var found = videos.Get();
        foreach (ManagementObject video in found)
        {
            using (video)
            {
                var name = video["Name"] as string;
                var id = video["PNPDeviceID"] as string;
                if (id is null || !id.StartsWith("PCI\\VEN_1002&", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var expected = GpuRanges.Where(item => item.Name == name).ToArray();
                if (expected.Length == 0)
                {
                    continue;
                }

                var escaped = id.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                using var resources = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_PnPEntity.DeviceID=\"{escaped}\"}} WHERE AssocClass=Win32_PNPAllocatedResource ResultClass=Win32_DeviceMemoryAddress");
                using var ranges = resources.Get();
                foreach (ManagementObject range in ranges)
                {
                    using (range)
                    {
                        var start = Convert.ToUInt64(range["StartingAddress"], CultureInfo.InvariantCulture);
                        var end = Convert.ToUInt64(range["EndingAddress"], CultureInfo.InvariantCulture);
                        candidates.AddRange(expected.Where(item => item.Base == start && item.Base + 0x7FFFFUL == end));
                    }
                }
            }
        }

        return candidates;
    }

    private sealed record GpuRange(string Name, uint Base, uint[] Versions);
}
