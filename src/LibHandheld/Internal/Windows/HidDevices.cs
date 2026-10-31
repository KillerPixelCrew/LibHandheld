using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LibHandheld.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>One HID top-level collection, described without keeping it open.</summary>
internal sealed record HidCollection
{
    /// <summary>The interface path to open.</summary>
    public required string DevicePath { get; init; }

    /// <summary>The device instance path, which HidHide hides by.</summary>
    public required string InstancePath { get; init; }

    /// <summary>The USB vendor ID.</summary>
    public required ushort VendorId { get; init; }

    /// <summary>The USB product ID.</summary>
    public required ushort ProductId { get; init; }

    /// <summary>The device release number (<c>bcdDevice</c>), which usually tracks the firmware.</summary>
    public required ushort ReleaseNumber { get; init; }

    /// <summary>The top-level collection's usage page.</summary>
    public required ushort UsagePage { get; init; }

    /// <summary>The top-level collection's usage.</summary>
    public required ushort Usage { get; init; }

    /// <summary>Input report length in bytes, report ID included.</summary>
    public required ushort InputLength { get; init; }

    /// <summary>Output report length in bytes, report ID included.</summary>
    public required ushort OutputLength { get; init; }

    /// <summary>Feature report length in bytes, report ID included.</summary>
    public required ushort FeatureLength { get; init; }

    /// <summary>The composite USB device's location path, without the interface component.</summary>
    public required string PhysicalLocation { get; init; }

    /// <summary>The product and report shape, for a log line.</summary>
    /// <returns>For example <c>1B4C/FF31:0076 in6 out0 feat64</c>.</returns>
    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{ProductId:X4}/{UsagePage:X4}:{Usage:X4} in{InputLength} out{OutputLength} feat{FeatureLength}");
    }
}

/// <summary>A present device node of any class, for the identities a package hands to HidHide.</summary>
/// <param name="InstancePath">The device instance path.</param>
/// <param name="ClassGuid">The device setup class.</param>
/// <param name="PhysicalLocation">The composite USB device's location path.</param>
internal sealed record DeviceNode(string InstancePath, Guid ClassGuid, string PhysicalLocation);

/// <summary>Finds, opens and writes a handheld's HID collections: the one HID layer every package shares.</summary>
internal static partial class HidDevices
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    /// <summary><c>GUID_DEVCLASS_HIDCLASS</c>.</summary>
    public static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    /// <summary><c>GUID_DEVCLASS_XNACOMPOSITE</c>, the XUSB (Xbox 360 protocol) controller class.</summary>
    public static readonly Guid XnaCompositeClass = new("D61CA365-5AF4-4486-998B-9DB4734C6CA3");

    /// <summary><c>GUID_DEVCLASS_XBOXCOMPOSITE</c>, the GIP (Xbox One protocol) controller class.</summary>
    public static readonly Guid XboxCompositeClass = new("05F5CFE2-4733-4950-A6BB-07AAD01A3A84");

    private static readonly nint InvalidHandleValue = new(-1);

    private static readonly NativeDevice.DevPropKey LocationPathsKey = new()
    {
        FormatId = NativeDevice.DevicePropertyCategory,
        PropertyId = 37
    };

    internal static HidCollection? Unique(IEnumerable<HidCollection> collections, Func<HidCollection, bool> matches)
    {
        HidCollection? result = null;
        foreach (var collection in collections)
        {
            if (!matches(collection))
            {
                continue;
            }

            if (result is not null)
            {
                return null;
            }

            result = collection;
        }

        return result;
    }

    internal static HidCollection? DescribePath(string path)
    {
        var set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            DeviceInterfaceData entry = new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            return SetupDiOpenDeviceInterface(set, path, 0, ref entry)
                   && TryInterfaceDetail(set, ref entry, out var info, out _)
                ? TryDescribe(path, set, info, static _ => true, out _)
                : null;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
    }

    internal static async ValueTask<byte[]> ReadMatchingAsync(FileStream stream, int reportLength,
        Func<byte[], bool> matches, CancellationToken token)
    {
        var report = new byte[reportLength];
        while (true)
        {
            var offset = 0;
            while (offset < report.Length)
            {
                var count = await stream.ReadAsync(report.AsMemory(offset), token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The HID collection closed before its reply.");
                }

                offset += count;
            }

            if (matches(report))
            {
                return report;
            }
        }
    }

    internal static bool GetFeature(SafeFileHandle handle, byte[] report)
    {
        return NativeHid.HidD_GetFeature(handle, report, report.Length);
    }

    internal static bool GetInputReport(SafeFileHandle handle, byte[] report)
    {
        return NativeHid.HidD_GetInputReport(handle, report, report.Length);
    }

    internal static async ValueTask<byte[]> ExchangeAsync(FileStream stream, ReadOnlyMemory<byte> request,
        int responseLength, Func<byte[], bool> matches, TimeSpan timeout, CancellationToken token,
        TimeSpan settle = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(timeout);
        // Submit once. Unrelated queued reports are read without resending the command.
        await stream.WriteAsync(request, bounded.Token).ConfigureAwait(false);
        await stream.FlushAsync(bounded.Token).ConfigureAwait(false);
        if (settle > TimeSpan.Zero)
        {
            await Task.Delay(settle, bounded.Token).ConfigureAwait(false);
        }

        return await ReadMatchingAsync(stream, responseLength, matches, bounded.Token).ConfigureAwait(false);
    }

    /// <summary>Whether an interface or instance path belongs to one of the given products.</summary>
    /// <param name="path">The path.</param>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productIds">The accepted product IDs.</param>
    /// <returns>True when the path names the vendor and one of the products.</returns>
    public static bool MatchesProduct(ReadOnlySpan<char> path, ushort vendorId,
        IReadOnlyCollection<ushort> productIds)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        return UsbIds.Parse(path, "VID_") == vendorId && UsbIds.Parse(path, "PID_") is { } product
                                                      && productIds.Contains(product);
    }

    /// <summary>Every present HID collection of the given products.</summary>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productIds">The accepted product IDs.</param>
    /// <param name="requiredPath">Only this interface path, when given.</param>
    /// <returns>The collections, in enumeration order; empty when Windows cannot list them.</returns>
    public static IReadOnlyList<HidCollection> Enumerate(
        ushort vendorId,
        IReadOnlyCollection<ushort> productIds,
        string? requiredPath = null)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        return Walk(
                   path => (requiredPath is null
                            || string.Equals(path, requiredPath, StringComparison.OrdinalIgnoreCase))
                           && MatchesProduct(path, vendorId, productIds),
                   attributes => attributes.VendorId == vendorId && productIds.Contains(attributes.ProductId),
                   null,
                   out _)
               ?? [];
    }

    /// <summary>Every present HID collection of every vendor, for diagnostics that must see the whole machine.</summary>
    /// <param name="unreadable">
    ///     Receives the interface path of each collection that could not be described, and why; null skips them.
    /// </param>
    /// <returns>The collections, in enumeration order.</returns>
    /// <exception cref="Win32Exception">Windows could not list the HID collections.</exception>
    public static IReadOnlyList<HidCollection> EnumerateAll(Action<string, string>? unreadable = null)
    {
        return Walk(static _ => true, static _ => true, unreadable, out var error)
               ?? throw new Win32Exception(error, "Windows could not list the HID collections.");
    }

    /// <summary>Every present device node of the given products, of any class.</summary>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productIds">The accepted product IDs.</param>
    /// <returns>The nodes, in enumeration order.</returns>
    public static IReadOnlyList<DeviceNode> PresentNodes(ushort vendorId, IReadOnlyCollection<ushort> productIds)
    {
        return Nodes(vendorId, productIds, DigcfPresent | DigcfAllClasses);
    }

    /// <summary>Every device node Windows knows for the given products, present or not, of any class.</summary>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productIds">The accepted product IDs.</param>
    /// <returns>The nodes, in enumeration order.</returns>
    /// <remarks>
    ///     A pad that changes mode re-enumerates under another product ID, and Windows keeps the nodes of every
    ///     mode it has been in. Hiding those as well hides a mode before the pad comes back in it, which a list of
    ///     present nodes cannot do. Use it for the hide set only, never to decide what is attached.
    /// </remarks>
    public static IReadOnlyList<DeviceNode> KnownNodes(ushort vendorId, IReadOnlyCollection<ushort> productIds)
    {
        return Nodes(vendorId, productIds, DigcfAllClasses);
    }

    /// <summary>
    ///     The controller nodes to hide for one pad: its XUSB, Xbox composite and XInput-compatible HID nodes in
    ///     every mode Windows has seen it in, at the pad's composite location.
    /// </summary>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productIds">Every product ID the pad enumerates under, one per mode.</param>
    /// <param name="location">The pad's composite location; nodes elsewhere belong to another device.</param>
    /// <param name="auxiliaryFragments">
    ///     Instance-path fragments such as <c>&amp;PID_F901&amp;MI_02&amp;COL03\</c> naming the pad's auxiliary
    ///     HID leaves, which are hidden with it.
    /// </param>
    /// <returns>The identities, present or not.</returns>
    public static IReadOnlyList<PhysicalDeviceIdentity> KnownControllerNodes(ushort vendorId,
        IReadOnlyCollection<ushort> productIds, string location, IReadOnlyList<string>? auxiliaryFragments = null)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return [];
        }

        auxiliaryFragments ??= [];
        return
        [
            .. KnownNodes(vendorId, productIds)
                .Where(node => IsControllerNode(node, auxiliaryFragments)
                               && !string.IsNullOrWhiteSpace(node.PhysicalLocation)
                               && SamePhysicalLocation(node.PhysicalLocation, location))
                .Select(PhysicalIdentities.FromNode)
        ];
    }

    /// <summary>Whether a node is one of a pad's controller nodes: XUSB, Xbox composite or XInput-compatible HID.</summary>
    /// <param name="node">The node.</param>
    /// <param name="auxiliaryFragments">Instance-path fragments naming the pad's auxiliary HID leaves.</param>
    /// <returns>True for a controller node or a named auxiliary leaf.</returns>
    internal static bool IsControllerNode(DeviceNode node, IReadOnlyList<string> auxiliaryFragments)
    {
        return node.ClassGuid == XnaCompositeClass || node.ClassGuid == XboxCompositeClass
                                                   || (node.ClassGuid == HidClass
                                                       && (node.InstancePath.Contains("&IG_",
                                                               StringComparison.OrdinalIgnoreCase)
                                                           || auxiliaryFragments.Any(fragment =>
                                                               node.InstancePath.Contains(fragment,
                                                                   StringComparison.OrdinalIgnoreCase))));
    }

    private static IReadOnlyList<DeviceNode> Nodes(ushort vendorId, IReadOnlyCollection<ushort> productIds,
        uint flags)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var prefixes = productIds.Select(product => $"VID_{vendorId:X4}&PID_{product:X4}").ToArray();
        var buffer = new StringBuilder(1024);
        List<DeviceNode> nodes = [];
        foreach (var enumerator in new[] { "USB", "HID" })
        {
            var set = SetupDiGetClassDevsAll(0, enumerator, 0, flags);
            if (set == InvalidHandleValue)
            {
                continue;
            }

            try
            {
                for (uint index = 0;; index++)
                {
                    DeviceInfoData info = new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                    if (!SetupDiEnumDeviceInfo(set, index, ref info))
                    {
                        if (Marshal.GetLastPInvokeError() == ErrorNoMoreItems)
                        {
                            break;
                        }

                        continue;
                    }

                    buffer.Clear();
                    if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out _))
                    {
                        continue;
                    }

                    var instance = buffer.ToString();
                    var matches = false;
                    foreach (var prefix in prefixes)
                    {
                        matches |= instance.Contains(prefix, StringComparison.OrdinalIgnoreCase);
                    }

                    if (matches)
                    {
                        nodes.Add(new DeviceNode(instance, info.ClassGuid, ReadPhysicalLocation(info.DeviceInstance)));
                    }
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(set);
            }
        }

        return nodes;
    }

    /// <summary>Whether two location paths name the same composite USB device.</summary>
    /// <param name="left">One location path.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when normalized composite locations agree, including two empty locations.</returns>
    /// <remarks>Callers requiring a physical identity match must reject empty locations before comparison.</remarks>
    public static bool SamePhysicalLocation(string left, string right)
    {
        return string.Equals(CompositeLocation(left), CompositeLocation(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a collection for reading and writing.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="overlapped">Opens it for asynchronous I/O.</param>
    /// <returns>A shared read/write handle owned by the caller, which must dispose it.</returns>
    /// <exception cref="Win32Exception">The collection could not be opened.</exception>
    public static SafeFileHandle Open(HidCollection collection, bool overlapped)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var handle = NativeDevice.CreateFile(collection.DevicePath, GenericRead | GenericWrite,
            FileShareRead | FileShareWrite, 0,
            OpenExisting, overlapped ? FileFlagOverlapped : 0, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, $"The HID collection {collection.Describe()} could not be opened.");
    }

    /// <summary>Opens a collection as an unbuffered asynchronous stream of whole reports.</summary>
    /// <param name="collection">The collection.</param>
    /// <returns>An unbuffered asynchronous stream owned by the caller; disposing it closes the HID handle.</returns>
    /// <remarks>Use descriptor-sized reports and check read lengths; the stream does not validate report contents.</remarks>
    /// <exception cref="Win32Exception">The collection could not be opened.</exception>
    public static FileStream OpenStream(HidCollection collection)
    {
        return new FileStream(Open(collection, true), FileAccess.ReadWrite, 0, true);
    }

    /// <summary>Whether the collection answers a feature read for one report ID.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="reportId">The report ID to read.</param>
    /// <returns>True when the driver accepted the feature read; false for a short feature shape or open/read failure.</returns>
    /// <remarks>
    ///     Opens and closes a temporary handle. This performs real HID I/O, does not validate returned bytes,
    ///     and must only be used with a package-approved report whose read semantics are known.
    /// </remarks>
    public static bool AnswersFeature(HidCollection collection, byte reportId)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.FeatureLength < 2)
        {
            return false;
        }

        try
        {
            using var handle = Open(collection, false);
            var buffer = new byte[collection.FeatureLength];
            buffer[0] = reportId;
            return NativeHid.HidD_GetFeature(handle, buffer, buffer.Length);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Sends one feature report, padded to the collection's declared length.</summary>
    /// <param name="handle">An open handle to this collection, retained and disposed by the caller.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="report">Nonempty report starting with its ID; remaining declared report bytes are zero-filled.</param>
    /// <exception cref="InvalidOperationException">The report is empty or exceeds the declared report length.</exception>
    /// <exception cref="Win32Exception">The driver refused the report; its effect is unknown.</exception>
    public static void SetFeature(SafeFileHandle handle, HidCollection collection, ReadOnlySpan<byte> report)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (report.Length == 0 || report.Length > collection.FeatureLength)
        {
            throw new InvalidOperationException(
                $"A {report.Length}-byte feature report does not fit {collection.Describe()}.");
        }

        var buffer = new byte[collection.FeatureLength];
        report.CopyTo(buffer);
        if (!NativeHid.HidD_SetFeature(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "HidD_SetFeature failed; effect unknown.");
        }
    }

    /// <summary>Sends one output report, padded to the collection's declared length.</summary>
    /// <param name="handle">An open synchronous handle to this collection, retained and disposed by the caller.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="report">Nonempty report starting with its ID; remaining declared report bytes are zero-filled.</param>
    /// <exception cref="InvalidOperationException">The report is empty or exceeds the declared report length.</exception>
    /// <exception cref="IOException">The write failed or was short; its effect is unknown.</exception>
    public static void WriteOutput(SafeFileHandle handle, HidCollection collection, ReadOnlySpan<byte> report)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (report.Length == 0 || report.Length > collection.OutputLength)
        {
            throw new InvalidOperationException(
                $"A {report.Length}-byte output report does not fit {collection.Describe()}.");
        }

        var buffer = new byte[collection.OutputLength];
        report.CopyTo(buffer);
        if (!WriteFile(handle, buffer, (uint)buffer.Length, out var written, 0) || written != buffer.Length)
        {
            throw new IOException("The HID output report failed or was short; effect unknown.");
        }
    }

    // The one SetupDi walk behind both enumerations. Null, with the Windows error, when the list cannot be made.
    private static List<HidCollection>? Walk(
        Func<string, bool> acceptsPath,
        Func<NativeHid.HidAttributes, bool> acceptsAttributes,
        Action<string, string>? unreadable,
        out int listError)
    {
        NativeHid.HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue)
        {
            listError = Marshal.GetLastPInvokeError();
            return null;
        }

        listError = 0;
        List<HidCollection> collections = [];
        try
        {
            for (uint index = 0;; index++)
            {
                DeviceInterfaceData interfaceData = new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastPInvokeError() == ErrorNoMoreItems)
                    {
                        break;
                    }

                    continue;
                }

                if (!TryInterfaceDetail(set, ref interfaceData, out var info, out var path)
                    || path is null || !acceptsPath(path))
                {
                    continue;
                }

                var collection = TryDescribe(path, set, info, acceptsAttributes, out var problem);
                if (collection is not null)
                {
                    collections.Add(collection);
                }
                else if (problem is not null)
                {
                    unreadable?.Invoke(path, problem);
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }

        return collections;
    }

    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: a size query, then the detail call, whose cbSize counts the
    // fixed header only (8 on 64-bit, 6 on 32-bit) and which fills the interface's device node.
    private static bool TryInterfaceDetail(nint set, ref DeviceInterfaceData entry, out DeviceInfoData info,
        out string? path)
    {
        info = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
        path = null;
        _ = SetupDiGetDeviceInterfaceDetail(set, ref entry, 0, 0, out var required, 0);
        if (required == 0)
        {
            return false;
        }

        var detail = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref entry, detail, required, out _, ref info))
            {
                return false;
            }

            path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    // Opens with no access, which reads attributes and capabilities without claiming the device. A
    // collection the filter rejects is null with no problem.
    private static HidCollection? TryDescribe(
        string path,
        nint set,
        DeviceInfoData info,
        Func<NativeHid.HidAttributes, bool> acceptsAttributes,
        out string? problem)
    {
        using var handle = NativeDevice.CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            problem = $"could not be opened (error {Marshal.GetLastPInvokeError()})";
            return null;
        }

        NativeHid.HidAttributes attributes = new() { Size = Marshal.SizeOf<NativeHid.HidAttributes>() };
        if (!NativeHid.HidD_GetAttributes(handle, ref attributes))
        {
            problem = "its attributes could not be read";
            return null;
        }

        problem = null;
        if (!acceptsAttributes(attributes))
        {
            return null;
        }

        if (!NativeHid.HidD_GetPreparsedData(handle, out var preparsed))
        {
            problem = "no report descriptor";
            return null;
        }

        NativeHid.HidCaps caps;
        try
        {
            if (NativeHid.HidP_GetCaps(preparsed, out caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                problem = "its capabilities could not be read";
                return null;
            }
        }
        finally
        {
            _ = NativeHid.HidD_FreePreparsedData(preparsed);
        }

        return new HidCollection
        {
            DevicePath = path,
            InstancePath = ReadInstancePath(set, info),
            VendorId = attributes.VendorId,
            ProductId = attributes.ProductId,
            ReleaseNumber = attributes.VersionNumber,
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputLength = caps.InputReportByteLength,
            OutputLength = caps.OutputReportByteLength,
            FeatureLength = caps.FeatureReportByteLength,
            PhysicalLocation = ReadPhysicalLocation(info.DeviceInstance)
        };
    }

    private static string ReadInstancePath(nint set, DeviceInfoData info)
    {
        var buffer = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadPhysicalLocation(uint deviceInstance)
    {
        var current = deviceInstance;
        var key = LocationPathsKey;
        var buffer = new byte[4096];
        for (var depth = 0; depth < 6; depth++)
        {
            var length = checked((uint)buffer.Length);
            if (NativeDevice.CM_Get_DevNode_Property(current, ref key, out _, buffer, ref length, 0) == 0)
            {
                var value = Encoding.Unicode.GetString(buffer, 0, checked((int)length)).TrimEnd('\0');
                var terminator = value.IndexOf('\0');
                if (terminator >= 0)
                {
                    value = value[..terminator];
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return CompositeLocation(value);
                }
            }

            if (NativeDevice.CM_Get_Parent(out current, current, 0) != 0)
            {
                break;
            }
        }

        return string.Empty;
    }

    private static string CompositeLocation(string location)
    {
        var interfaceComponent = location.IndexOf("#USBMI(", StringComparison.OrdinalIgnoreCase);
        return interfaceComponent < 0 ? location : location[..interfaceComponent];
    }


    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, [In] byte[] buffer, uint length, out uint written,
        nint overlapped);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiCreateDeviceInfoList(nint classGuid, nint parent);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInterface(nint set, string path, uint flags,
        ref DeviceInterfaceData entry);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial nint SetupDiGetClassDevsAll(nint classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref DeviceInfoData data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData, nint detailData, uint detailDataSize, out uint requiredSize,
        nint deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData, nint detailData, uint detailDataSize, out uint requiredSize,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref DeviceInfoData deviceInfoData,
        StringBuilder instanceId, int instanceIdSize, out int requiredSize);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);


    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nuint Reserved;
    }
}
