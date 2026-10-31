using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Transports;

internal interface IPawnIoModule : IDisposable
{
    long[] Execute(string function, long[] input, int outputWords);
}

// The two documented PawnIO operations. Signed modules constrain the permitted hardware accesses;
// A successful IOCTL may return fewer words than requested, as HC's PawnIO client accepts.
internal sealed class PawnIoModule : IPawnIoModule
{
    private const uint LoadCode = (41394u << 16) | (0x821 << 2);
    private const uint ExecuteCode = (41394u << 16) | (0x841 << 2);
    private readonly Lock _gate = new();
    private readonly SafeFileHandle _handle;

    private PawnIoModule(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public long[] Execute(string function, long[] input, int outputWords)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(function) || function.Length >= 32 || !Ascii.IsValid(function) || outputWords < 0)
        {
            throw new ArgumentException("Invalid PawnIO function or output length.");
        }

        var length = checked(32 + input.Length * sizeof(long));
        // Port operations carry a few words; the request is built on the stack and the reply lands in its result.
        var request = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.ASCII.GetBytes(function, request);
        MemoryMarshal.AsBytes(input.AsSpan()).CopyTo(request[32..]);
        var words = new long[outputWords];
        var response = MemoryMarshal.AsBytes(words.AsSpan());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!NativeDevice.DeviceIoControl(_handle, ExecuteCode, request, response, out var returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"PawnIO {function} failed.");
            }

            // Only whole returned words are results; anything past them stays zero.
            var returnedWords = Math.Min(returned / sizeof(long), (uint)outputWords);
            response[checked((int)returnedWords * sizeof(long))..].Clear();
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

    // Every embedded module is pinned here, by name, to the SHA-256 of its signed release.
    internal static PawnIoModule OpenModule(string module)
    {
        var digest = module switch
        {
            "LpcIO" => "B3896A1CAB0D808FCA31FE2EBCAE045D59DAC690DA87B17C858BB8DA357EB45E",
            "LpcACPIEC" => "C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906",
            "IntelMSR" => "D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F",
            "RyzenSMU" => "301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450",
            "LedsValve" => "1C1809281AE6F3E3C5B362F1536C0E6706013E6E33BFBA66CAD74636E61436D6",
            _ => throw new ArgumentOutOfRangeException(nameof(module))
        };
        return OpenResource(typeof(PawnIoModule).Assembly, $"LibHandheld.PawnIO.{module}.bin", digest);
    }

    internal static PawnIoModule OpenRyzen()
    {
        return OpenModule("RyzenSMU");
    }

    internal static PawnIoModule OpenValveLeds()
    {
        return OpenModule("LedsValve");
    }

    private static PawnIoModule OpenResource(Assembly assembly, string name, string digest)
    {
        using var source = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Required signed PawnIO module {name} is missing.");
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        var bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"PawnIO module {name} does not match the pinned release.");
        }

        var handle = NativeDevice.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The installed PawnIO driver could not be opened.");
        }

        if (!NativeDevice.DeviceIoControl(handle, LoadCode, bytes, (uint)bytes.Length, [], 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"PawnIO refused signed module {name}.");
        }

        return new PawnIoModule(handle);
    }
}
