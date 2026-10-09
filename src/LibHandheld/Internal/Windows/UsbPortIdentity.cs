using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

internal sealed record UsbHubIdentity(string InterfacePath, string PhysicalLocation, string DriverKey);

internal readonly record struct UsbPortEvidence(bool UserConnectable, string DriverKey);

internal readonly record struct UsbPortStep(string HubLocation, uint Port, string ChildLocation);

/// <summary>Read-only proof that an unanchored controller occupies an internal USB port chain.</summary>
/// <remarks>
///     ABI: Microsoft Windows SDK 10.0.26100.0 shared/usbioctl.h and usbiodef.h. The connector structure
///     is packed: ULONG connection, ULONG actual length, ULONG flags, two USHORTs, then WCHAR[1].
///     Only GET_PORT_CONNECTOR_PROPERTIES and GET_NODE_CONNECTION_DRIVERKEY_NAME are issued. Driver-key
///     correlation proves that the queried port actually names this devnode, rather than trusting a location's numeric
///     text.
/// </remarks>
internal static partial class UsbPortIdentity
{
    internal const uint ConnectorIoctl = (0x22U << 16) | (278U << 2);
    internal const uint DriverKeyIoctl = (0x22U << 16) | (264U << 2);
    private static readonly Guid HubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    private static readonly Guid DeviceProperties = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly nint InvalidHandle = new(-1);

    internal static bool IsInternal(string instancePath, string physicalLocation)
    {
        if (!OperatingSystem.IsWindows() || !TryPath(physicalLocation, out _))
        {
            return false;
        }

        var driverKey = PhysicalDriverKey(instancePath, physicalLocation);
        if (driverKey is null)
        {
            return false;
        }

        var hubs = PresentHubs();
        return ProvesInternal(physicalLocation, driverKey, hubs, QueryPort);
    }

    /// <summary>Every parent port must be internal, including the upstream port of any nested hub.</summary>
    internal static bool ProvesInternal(string location, string controllerDriverKey, IReadOnlyList<UsbHubIdentity> hubs,
        Func<UsbHubIdentity, uint, UsbPortEvidence?> query)
    {
        if (string.IsNullOrWhiteSpace(controllerDriverKey) || !TryPath(location, out var steps))
        {
            return false;
        }

        foreach (var step in steps)
        {
            var matches = hubs.Where(hub => Equal(hub.PhysicalLocation, step.HubLocation)).ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            var childHubs = hubs.Where(hub => Equal(hub.PhysicalLocation, step.ChildLocation)).ToArray();
            if (childHubs.Length > 1)
            {
                return false;
            }

            var expected = Equal(step.ChildLocation, location)
                ? controllerDriverKey
                : childHubs.SingleOrDefault()?.DriverKey;
            if (string.IsNullOrWhiteSpace(expected) || query(matches[0], step.Port) is not { } evidence
                                                    || evidence.UserConnectable || !Equal(evidence.DriverKey, expected))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryPath(string? location, out IReadOnlyList<UsbPortStep> steps)
    {
        steps = [];
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var root = location.IndexOf("#USBROOT(", StringComparison.OrdinalIgnoreCase);
        if (root < 0)
        {
            return false;
        }

        var end = location.IndexOf(')', root + 9);
        if (end < 0 || end == location.Length - 1)
        {
            return false;
        }

        var parent = location[..(end + 1)];
        var result = new List<UsbPortStep>();
        for (var index = end + 1; index < location.Length;)
        {
            if (!location.AsSpan(index).StartsWith("#USB(", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var close = location.IndexOf(')', index + 5);
            if (close < 0 || !uint.TryParse(location.AsSpan(index + 5, close - index - 5), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var port) || port is 0 or > 255)
            {
                return false;
            }

            var child = location[..(close + 1)];
            result.Add(new UsbPortStep(parent, port, child));
            parent = child;
            index = close + 1;
        }

        steps = Array.AsReadOnly(result.ToArray());
        return result.Count != 0;
    }

    internal static bool? UserConnectable(ReadOnlySpan<byte> response, uint expectedPort)
    {
        if (response.Length < 18 || BinaryPrimitives.ReadUInt32LittleEndian(response) != expectedPort)
        {
            return null;
        }

        var actual = BinaryPrimitives.ReadUInt32LittleEndian(response[4..]);
        if (actual < 18 || actual > response.Length)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(response[8..]);
        return (flags & ~15U) != 0 ? null : (flags & 1) != 0;
    }

    private static UsbPortEvidence? QueryPort(UsbHubIdentity hub, uint port)
    {
        // FILE_ANY_ACCESS GET requests need no read/write endpoint ownership.
        using var handle = CreateFile(hub.InterfacePath, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var request = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(request, port);
        var response = new byte[4096];
        if (!DeviceIoControl(handle, ConnectorIoctl, request, (uint)request.Length, response, (uint)response.Length,
                out var count, 0)
            || count > response.Length || UserConnectable(response.AsSpan(0, (int)count), port) is not { } user)
        {
            return null;
        }

        var keyRequest = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(keyRequest, port);
        if (!DeviceIoControl(handle, DriverKeyIoctl, keyRequest, (uint)keyRequest.Length, response,
                (uint)response.Length, out count, 0)
            || count < 10 || count > response.Length || BinaryPrimitives.ReadUInt32LittleEndian(response) != port)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4));
        if (length < 10 || length > count || (length & 1) != 0)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(response, 8, checked((int)length - 8));
        var terminator = name.IndexOf('\0');
        if (terminator < 0)
        {
            return null;
        }

        name = name[..terminator];
        return string.IsNullOrWhiteSpace(name) ? null : new UsbPortEvidence(user, name);
    }

    private static string? PhysicalDriverKey(string instancePath, string location)
    {
        if (CM_Locate_DevNode(out var node, instancePath, 0) != 0)
        {
            return null;
        }

        for (var depth = 0; depth < 8; depth++)
        {
            var id = new StringBuilder(1024);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) == 0
                && id.ToString().StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
                && !id.ToString().Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                && Equal(Property(node, 37, true), location))
            {
                return Property(node, 11, false);
            }

            if (CM_Get_Parent(out node, node, 0) != 0)
            {
                break;
            }
        }

        return null;
    }

    private static IReadOnlyList<UsbHubIdentity> PresentHubs()
    {
        var guid = HubInterface;
        var set = SetupDiGetClassDevs(ref guid, null, 0, 0x12);
        if (set == InvalidHandle)
        {
            return [];
        }

        List<UsbHubIdentity> hubs = [];
        try
        {
            for (uint index = 0;; index++)
            {
                InterfaceData entry = new() { Size = checked((uint)Marshal.SizeOf<InterfaceData>()) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref entry))
                {
                    break;
                }

                _ = SetupDiGetDeviceInterfaceDetail(set, ref entry, 0, 0, out var size, 0);
                if (size is 0 or > 65536)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    DeviceData device = new() { Size = checked((uint)Marshal.SizeOf<DeviceData>()) };
                    if (!SetupDiGetDeviceInterfaceDetailWithNode(set, ref entry, buffer, size, out _, ref device))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(buffer + 4);
                    var location = Property(device.Node, 37, true);
                    var key = Property(device.Node, 11, false);
                    if (path is not null && location is not null && key is not null)
                    {
                        hubs.Add(new UsbHubIdentity(path, location, key));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return hubs;
    }

    private static string? Property(uint node, uint property, bool list)
    {
        PropertyKey key = new() { Format = DeviceProperties, Id = property };
        var buffer = new byte[4096];
        var size = checked((uint)buffer.Length);
        if (CM_Get_DevNode_Property(node, ref key, out var type, buffer, ref size, 0) != 0 || size > buffer.Length
            || type != (list ? 0x2012U : 0x12U) || (size & 1) != 0)
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(buffer, 0, checked((int)size));
        var end = value.IndexOf('\0');
        if (end >= 0)
        {
            value = value[..end];
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool Equal(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, nint overlapped);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, uint index,
        ref InterfaceData entry);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData entry, nint detail,
        uint size, out uint required, nint device);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailWithNode(nint set, ref InterfaceData entry, nint detail,
        uint size, out uint required, ref DeviceData device);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int size, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        public uint Size;
        public Guid Class;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceData
    {
        public uint Size;
        public Guid Class;
        public uint Node;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }
}
