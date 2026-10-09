using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>Shared Windows device-handle ABI; transports retain their paths, flags and ownership.</summary>
internal static partial class NativeDevice
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, nint overlapped);
}
