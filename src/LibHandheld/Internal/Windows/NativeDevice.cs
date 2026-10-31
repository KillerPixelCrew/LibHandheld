using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>A <c>DEVPROPKEY</c> or <c>PROPERTYKEY</c>: both are a format GUID followed by a property id.</summary>
/// <param name="formatId">The property category.</param>
/// <param name="propertyId">The property within it.</param>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

/// <summary>Shared Windows device-handle and device-node ABI; transports retain their paths, flags and ownership.</summary>
internal static partial class NativeDevice
{
    /// <summary>The <c>DEVPKEY_Device_*</c> property category, which numbers address and location properties.</summary>
    internal static readonly Guid DevicePropertyCategory = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, nint overlapped);

    /// <summary>A synchronous buffered IOCTL over caller-owned spans, sized by the span lengths.</summary>
    internal static unsafe bool DeviceIoControl(SafeFileHandle device, uint code, ReadOnlySpan<byte> input,
        Span<byte> output, out uint returned)
    {
        fixed (byte* inputBytes = input)
        fixed (byte* outputBytes = output)
        {
            return DeviceIoControl(device, code, inputBytes, (uint)input.Length, outputBytes, (uint)output.Length,
                out returned, 0);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input,
        uint inputSize, byte* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial int CM_Get_Parent(out uint parent, uint deviceInstance, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    internal static partial int CM_Get_DevNode_Property(uint deviceInstance, ref PropertyKey propertyKey,
        out uint propertyType, [Out] byte[] buffer, ref uint bufferLength, uint flags);
}
