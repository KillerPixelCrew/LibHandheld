using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibHandheld.Internal.Transports;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.SteamDeck;

internal interface ISteamDeckMemory : IDisposable
{
    uint Read(uint address, int width);
    void Write(uint address, uint value, int width);
    void WriteFanMode(byte mode);
    void AuthorizeGpuRange(uint mmioBase);
}

/// <summary>The pinned InpOut ABI; the pre-existing driver must be ready before DLL loading.</summary>
internal sealed class SteamDeckMemory : ISteamDeckMemory
{
    internal const string Digest = "5F27ED4D5CD58A1EE23DEEB802E09E73F3A1D884CE2135F6E827F67B171269E7";
    private readonly Lock _gate = new();
    private readonly FileStream _held;
    private readonly nint _library;
    private readonly MapMemory _map;
    private readonly SafeFileHandle _readiness;
    private readonly UnmapMemory _unmap;
    private bool _disposed;
    private uint? _gpuBase;

    private SteamDeckMemory(FileStream held, SafeFileHandle readiness, nint library)
    {
        _held = held;
        _readiness = readiness;
        _library = library;
        _map = Export<MapMemory>("MapPhysToLin");
        _unmap = Export<UnmapMemory>("UnmapPhysicalMemory");
        _ = Export<WritePort>("DlPortWritePortUchar");
        if (!Export<DriverReady>("IsInpOutDriverOpen")())
        {
            throw new IOException("The InpOut driver did not open.");
        }
    }

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

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The public void export silently discards IOCTL failure. The pinned driver's verified
            // ABI is a ushort port followed by one byte; acknowledge the actual driver dispatch.
            var request = new byte[] { 0x6C, 0, mode };
            if (!DeviceIoControl(_readiness, 0x9C402008, request, 3, new byte[10], 10, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "Valve fan-mode dispatch failed; completion is unknown.");
            }
        }
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeLibrary.Free(_library);
            _readiness.Dispose();
            _held.Dispose();
        }
    }

    internal static SteamDeckMemory Open()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "InpOut", "inpoutx64.dll");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        SafeFileHandle? readiness = null;
        nint library = 0;
        try
        {
            if (Convert.ToHexString(SHA256.HashData(held)) != Digest)
            {
                throw new InvalidDataException("InpOut does not match its pinned payload.");
            }

            // InpOut's DLL entry point may install a missing driver. Require the existing device first.
            readiness = CreateFileW(@"\\.\inpoutx64", 0, 3, 0, 3, 0, 0);
            if (readiness.IsInvalid)
            {
                throw new IOException(
                    "The pre-installed InpOut x64 driver is not ready. The library does not install it.");
            }

            library = NativeLibrary.Load(path);
            return new SteamDeckMemory(held, readiness, library);
        }
        catch
        {
            if (library != 0)
            {
                NativeLibrary.Free(library);
            }

            readiness?.Dispose();
            held.Dispose();
            throw;
        }
    }

    private T Map<T>(uint address, int width, Func<nint, T> action)
    {
        if (width is not (8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsAllowed(address, width, _gpuBase))
            {
                throw new ArgumentOutOfRangeException(nameof(address),
                    "Only the selected Valve mailbox and documented EC fields may be mapped.");
            }

            var pointer = _map(new nint(address), checked((uint)(width / 8)), out var mapping);
            if (pointer == 0 || mapping == 0)
            {
                // The driver can return a section handle without a view after translation fails.
                if (mapping != 0)
                {
                    _unmap(mapping, pointer);
                }

                throw new IOException("InpOut did not return a physical-memory mapping.");
            }

            try
            {
                return action(pointer);
            }
            finally
            {
                if (!_unmap(mapping, pointer))
                {
                    throw new IOException("The physical-memory mapping could not be released.");
                }
            }
        }
    }

    internal static bool IsAllowed(uint address, int width, uint? gpuBase)
    {
        return (gpuBase is { } selected && width == 32 && (address == selected + 0x1629A * 4
                                                           || address == selected + 0x16282 * 4 ||
                                                           address == selected + 0x16292 * 4))
               || (address, width) is (0xFE700C4C, 16) or (0xFE7003BD, 8) or (0xFE700C01, 8)
               or (0xFE700B92, 16) or (0xFE700B95, 16) or (0xFE700B97, 16)
               or (0xFE7003B0, 16) or (0xFE70046E, 16) or (0xFE700B9F, 8);
    }

    private T Export<T>(string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength,
        byte[] output, uint outputLength, out uint returned, nint overlapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint MapMemory(nint address, uint bytes, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UnmapMemory(nint handle, nint pointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WritePort(ushort port, byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool DriverReady();
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

    private readonly Lock _gate = new();
    internal uint FirmwareVersion { get; private set; }

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
        lock (_gate)
        {
            memory.Dispose();
        }
    }

    internal static VangoghPower Open()
    {
        var memory = SteamDeckMemory.Open();
        try
        {
            var candidates = PresentGpuRanges();
            foreach (var candidate in candidates)
            {
                memory.AuthorizeGpuRange(candidate.Base);
                var transport = new VangoghPower(memory, candidate.Base);
                var version = transport.Exchange(2, 0);
                if (candidate.Versions.Contains(version))
                {
                    transport.FirmwareVersion = version;
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

        memory.Write(mmioBase + Response, 0, 32);
        memory.Write(mmioBase + Argument, value, 32);
        memory.Write(mmioBase + Command, command, 32);
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
            var value = memory.Read(mmioBase + Response, 32);
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
        lock (_gate)
        {
            return HardwareBus.Run(@"Global\Access_PCI", TimeSpan.FromSeconds(2), action);
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
