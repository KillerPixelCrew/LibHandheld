using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Transports;

internal interface IPawnIoModule : IDisposable
{
    long[] Execute(string function, long[] input, int outputWords);
}

// The two documented PawnIO operations. Signed modules constrain the permitted hardware accesses;
// a failed/short IOCTL is an error, never a successful read of a fabricated zero.
internal sealed class PawnIoModule : IPawnIoModule
{
    private const uint LoadCode = (41394u << 16) | (0x821 << 2);
    private const uint ExecuteCode = (41394u << 16) | (0x841 << 2);
    internal const string RyzenDigest = "301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450";
    private readonly Lock _gate = new();
    private readonly SafeFileHandle _handle;

    private PawnIoModule(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public long[] Execute(string function, long[] input, int outputWords)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(function) || function.Length >= 32 || function.Any(c => c > 127) || outputWords < 0)
        {
            throw new ArgumentException("Invalid PawnIO function or output length.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            var request = new byte[checked(32 + input.Length * sizeof(long))];
            Encoding.ASCII.GetBytes(function, request);
            Buffer.BlockCopy(input, 0, request, 32, input.Length * sizeof(long));
            var response = new byte[checked(outputWords * sizeof(long))];
            if (!DeviceIoControl(_handle, ExecuteCode, request, (uint)request.Length, response,
                    (uint)response.Length, out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"PawnIO {function} failed.");
            }

            if (returned != response.Length)
            {
                throw new InvalidDataException(
                    $"PawnIO {function} returned {returned} bytes; expected {response.Length}.");
            }

            var words = new long[outputWords];
            Buffer.BlockCopy(response, 0, words, 0, response.Length);
            return words;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handle.Dispose();
        }
    }

    internal static PawnIoModule OpenLhm(string module)
    {
        if (module is not ("LpcIO" or "LpcACPIEC" or "IntelMSR"))
        {
            throw new ArgumentOutOfRangeException(nameof(module));
        }

        return OpenResource(typeof(Computer).Assembly, $"LibreHardwareMonitor.Resources.PawnIo.{module}.bin", null);
    }

    internal static PawnIoModule OpenRyzen()
    {
        return OpenResource(typeof(PawnIoModule).Assembly, "LibHandheld.PawnIO.RyzenSMU.bin", RyzenDigest);
    }

    internal static PawnIoModule OpenValveLeds()
    {
        return OpenResource(typeof(PawnIoModule).Assembly, "LibHandheld.PawnIO.LedsValve.bin",
            "1C1809281AE6F3E3C5B362F1536C0E6706013E6E33BFBA66CAD74636E61436D6");
    }

    private static PawnIoModule OpenResource(Assembly assembly, string name, string? digest)
    {
        using var source = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Required signed PawnIO module {name} is missing.");
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        var bytes = memory.ToArray();
        if (digest is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"PawnIO module {name} does not match the pinned release.");
        }

        var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The installed PawnIO driver could not be opened.");
        }

        if (!DeviceIoControl(handle, LoadCode, bytes, (uint)bytes.Length, [], 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"PawnIO refused signed module {name}.");
        }

        return new PawnIoModule(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes,
        byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
}
