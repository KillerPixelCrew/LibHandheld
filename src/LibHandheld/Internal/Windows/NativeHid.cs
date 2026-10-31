using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>The step at which reading a HID collection's capabilities stopped.</summary>
internal enum HidCapsStatus
{
    /// <summary>The descriptor and capabilities were read.</summary>
    Ok,

    /// <summary>The collection returned no report descriptor.</summary>
    NoDescriptor,

    /// <summary>The descriptor was read but its capabilities were not.</summary>
    CapsFailed
}

/// <summary>Windows HID report descriptors shared by family decoders.</summary>
internal static unsafe partial class NativeHid
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    /// <summary>Reads a collection's report descriptor and top-level capabilities.</summary>
    /// <param name="device">An open collection handle; no access is needed.</param>
    /// <param name="preparsed">The descriptor on success, owned and disposed by the caller; otherwise null.</param>
    /// <param name="capabilities">The capabilities on success; otherwise default.</param>
    /// <returns>Which step failed, so each caller keeps its own message.</returns>
    public static HidCapsStatus ReadCaps(SafeFileHandle device, out PreparsedData? preparsed,
        out HidCaps capabilities)
    {
        preparsed = null;
        capabilities = default;
        if (!HidD_GetPreparsedData(device, out var data))
        {
            return HidCapsStatus.NoDescriptor;
        }

        PreparsedData owned = new(data);
        if (HidP_GetCaps(data, out capabilities) != HIDP_STATUS_SUCCESS)
        {
            owned.Dispose();
            capabilities = default;
            return HidCapsStatus.CapsFailed;
        }

        preparsed = owned;
        return HidCapsStatus.Ok;
    }

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, out HidCaps capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int kind, ushort page, ushort link, ushort usage, out uint value,
        nint data, byte* report, uint length);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int kind, ushort page, ushort link, ushort* usages, ref uint count,
        nint data, byte* report, uint length);

    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int kind, ushort page, nint data);

    [LibraryImport("hid.dll")]
    public static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_SetFeature(SafeFileHandle device, [In] byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetFeature(SafeFileHandle device, [In] [Out] byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetInputReport(SafeFileHandle device, [In] [Out] byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FlushQueue(SafeFileHandle device);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetManufacturerString(SafeFileHandle device, [Out] byte[] buffer, uint length);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetProductString(SafeFileHandle device, [Out] byte[] buffer, uint length);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetButtonCaps(int reportType, [Out] HidpCapsEntry[] capabilities,
        ref ushort length, nint preparsedData);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetValueCaps(int reportType, [Out] HidpCapsEntry[] capabilities,
        ref ushort length, nint preparsedData);

    /// <summary>Owns one collection's preparsed report descriptor and frees it on dispose.</summary>
    public sealed class PreparsedData : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal PreparsedData(nint data) : base(ownsHandle: true)
        {
            SetHandle(data);
        }

        /// <summary>The descriptor pointer the HidP functions take; valid until dispose.</summary>
        public nint Value => handle;

        protected override bool ReleaseHandle()
        {
            return HidD_FreePreparsedData(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS share one 72-byte layout up to offset 16, where the button's
    // ReportCount and the value's HasNull overlap; the usage range union starts at 56 in both.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HidpCapsEntry
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public ushort ButtonReportCount;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ValueReportCount;
        [FieldOffset(32)] public uint UnitsExponent;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }
}
