using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Devices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>Re-enumerates a built-in USB controller by cycling its port on the parent hub.</summary>
/// <remarks>
///     The software equivalent of unplugging the controller. It works while handles are open, unlike a
///     device restart, which open handles veto into a reboot. Windows surprise-removes the device and
///     enumerates it again, so a read blocked in its driver fails and the ordinary disconnect path runs.
/// </remarks>
internal static partial class UsbPortCycle
{
    private const uint IoctlUsbHubCyclePort = 0x220444;
    private const uint CmGetIdListFilterEnumerator = 0x1;
    private const uint CmGetIdListFilterPresent = 0x100;
    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    /// <summary>Cycles the parent hub port of the first present composite USB device among the identities.</summary>
    /// <param name="ids">The controller's measured USB identities.</param>
    /// <returns>What was cycled, for the log.</returns>
    /// <exception cref="IOException">No device was present, or Windows refused the cycle.</exception>
    internal static string Cycle(IReadOnlyList<ControllerUsbId> ids)
    {
        var device = CompositeDevice(PresentUsbDevices(), ids)
                     ?? throw new IOException("No matching controller USB device is present.");
        Check(CM_Locate_DevNode(out var instance, device, 0), "locate the controller");
        Check(NativeDevice.CM_Get_Parent(out var parent, instance, 0), "find its hub");
        var key = new PropertyKey(NativeDevice.DevicePropertyCategory, propertyId: 30);
        var port = new byte[4];
        uint size = 4;
        Check(NativeDevice.CM_Get_DevNode_Property(instance, ref key, out _, port, ref size, 0), "read its port");
        var hub = Id(parent);
        var guid = HubInterface;
        Check(CM_Get_Device_Interface_List_Size(out var length, ref guid, hub, 0), "find the hub interface");
        var paths = new char[length];
        Check(CM_Get_Device_Interface_List(ref guid, hub, paths, length, 0), "read the hub interface");
        var path = new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                   ?? throw new IOException("The controller's hub has no interface.");
        var number = BitConverter.ToUInt32(port);
        using var handle = NativeDevice.CreateFile(path, 0x40000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("The controller's hub could not be opened.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        Span<uint> parameters = [number, 0];
        if (!DeviceIoControl(handle, IoctlUsbHubCyclePort, parameters, 8, parameters, 8, out _, 0))
        {
            throw new IOException("Windows refused to cycle the controller's port.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return $"{device} on {hub} port {number}";
    }

    /// <summary>The composite device itself, not one of its interface or collection children.</summary>
    internal static string? CompositeDevice(IEnumerable<string> present, IReadOnlyList<ControllerUsbId> ids)
    {
        foreach (var id in ids)
        {
            var prefix = $@"USB\VID_{id.Vendor:X4}&PID_{id.Product:X4}\";
            var match = present.FirstOrDefault(device =>
                device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && device.IndexOf('\\', prefix.Length) < 0);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string[] PresentUsbDevices()
    {
        const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        Check(CM_Get_Device_ID_List_Size(out var length, "USB", flags), "list USB devices");
        var buffer = new char[length];
        Check(CM_Get_Device_ID_List("USB", buffer, length, flags), "list USB devices");
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Id(uint instance)
    {
        var buffer = new char[400];
        Check(CM_Get_Device_ID(instance, buffer, buffer.Length, 0), "name the hub");
        return new string(buffer).TrimEnd('\0');
    }

    private static void Check(int result, string step)
    {
        if (result != 0)
        {
            throw new IOException($"Could not {step} (CONFIGRET 0x{result:X}).");
        }
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNode(out uint instance, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID(uint instance, [Out] char[] buffer, int length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_Size(out uint length, ref Guid guid, string deviceId,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List(ref Guid guid, string deviceId, [Out] char[] buffer,
        uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, Span<uint> input, int inputSize,
        Span<uint> output, int outputSize, out int returned, nint overlapped);
}
