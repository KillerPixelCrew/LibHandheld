using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>The report a HID capability belongs to, as <c>HIDP_REPORT_TYPE</c> numbers it.</summary>
internal enum HidReportType
{
    /// <summary>Input reports, which the device sends.</summary>
    Input = 0,

    /// <summary>Output reports, which the host writes.</summary>
    Output = 1,

    /// <summary>Feature reports, which the host reads and writes.</summary>
    Feature = 2
}

/// <summary>One button or value capability of a collection's report descriptor.</summary>
internal sealed record HidCapability
{
    /// <summary>The report it belongs to.</summary>
    public required HidReportType ReportType { get; init; }

    /// <summary>True for a button (<c>HIDP_BUTTON_CAPS</c>), false for a value (<c>HIDP_VALUE_CAPS</c>).</summary>
    public required bool IsButton { get; init; }

    /// <summary>The report ID.</summary>
    public required byte ReportId { get; init; }

    /// <summary>The usage page.</summary>
    public required ushort UsagePage { get; init; }

    /// <summary>The usage, or the first usage of a range.</summary>
    public required ushort UsageMin { get; init; }

    /// <summary>The last usage of a range; the same as <see cref="UsageMin" /> for a single usage.</summary>
    public required ushort UsageMax { get; init; }

    /// <summary>The link collection the capability sits in.</summary>
    public required ushort LinkCollection { get; init; }

    /// <summary>Whether the data is absolute rather than relative.</summary>
    public required bool IsAbsolute { get; init; }

    /// <summary>Bits per value; 1 for a button.</summary>
    public required ushort BitSize { get; init; }

    /// <summary>How many fields the report holds for it.</summary>
    public required ushort ReportCount { get; init; }

    /// <summary>Whether the value has a null state; false for a button.</summary>
    public bool HasNull { get; init; }

    /// <summary>The logical minimum; zero for a button.</summary>
    public int LogicalMin { get; init; }

    /// <summary>The logical maximum; zero for a button.</summary>
    public int LogicalMax { get; init; }

    /// <summary>The physical minimum; zero for a button.</summary>
    public int PhysicalMin { get; init; }

    /// <summary>The physical maximum; zero for a button.</summary>
    public int PhysicalMax { get; init; }

    /// <summary>The HID unit code; zero for a button.</summary>
    public uint Units { get; init; }

    /// <summary>The HID unit exponent; zero for a button.</summary>
    public uint UnitsExponent { get; init; }
}

/// <summary>What <see cref="HidDevices.Inspect" /> reads beyond enumeration: strings and the report descriptor.</summary>
internal sealed record HidCollectionDetails
{
    /// <summary>The manufacturer string, or null when the device has none.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>The product string, or null when the device has none.</summary>
    public string? Product { get; init; }

    /// <summary>The number of link collection nodes in the report descriptor.</summary>
    public required ushort LinkCollections { get; init; }

    /// <summary>Every button and value capability: input, then output, then feature; buttons before values.</summary>
    public required IReadOnlyList<HidCapability> Capabilities { get; init; }
}

internal static partial class HidDevices
{
    // USB string descriptors hold at most 126 UTF-16 characters and a terminator.
    private const int StringBufferBytes = 256;

    /// <summary>
    ///     Reads a collection's manufacturer and product strings and its report descriptor's button and value
    ///     capabilities, for diagnostics. Enumeration skips these because the strings are a request to the device.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The details.</returns>
    /// <exception cref="Win32Exception">The collection could not be opened.</exception>
    /// <exception cref="IOException">Its report descriptor could not be read.</exception>
    /// <remarks>The collection is opened with no access, so nothing is read from or written to its reports.</remarks>
    public static HidCollectionDetails Inspect(HidCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        using var handle = NativeDevice.CreateFile(collection.DevicePath, 0, FileShareRead | FileShareWrite, 0,
            OpenExisting, 0,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                $"The HID collection {collection.Describe()} could not be opened.");
        }

        if (!NativeHid.HidD_GetPreparsedData(handle, out var preparsed))
        {
            throw new IOException($"The HID collection {collection.Describe()} has no report descriptor.");
        }

        try
        {
            if (NativeHid.HidP_GetCaps(preparsed, out var caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                throw new IOException($"The capabilities of {collection.Describe()} could not be read.");
            }

            List<HidCapability> capabilities = [];
            AddCapabilities(capabilities, preparsed, HidReportType.Input, true, caps.NumberInputButtonCaps);
            AddCapabilities(capabilities, preparsed, HidReportType.Input, false, caps.NumberInputValueCaps);
            AddCapabilities(capabilities, preparsed, HidReportType.Output, true, caps.NumberOutputButtonCaps);
            AddCapabilities(capabilities, preparsed, HidReportType.Output, false, caps.NumberOutputValueCaps);
            AddCapabilities(capabilities, preparsed, HidReportType.Feature, true, caps.NumberFeatureButtonCaps);
            AddCapabilities(capabilities, preparsed, HidReportType.Feature, false, caps.NumberFeatureValueCaps);
            return new HidCollectionDetails
            {
                Manufacturer = ReadString(handle, NativeHid.HidD_GetManufacturerString),
                Product = ReadString(handle, NativeHid.HidD_GetProductString),
                LinkCollections = caps.NumberLinkCollectionNodes,
                Capabilities = capabilities
            };
        }
        finally
        {
            _ = NativeHid.HidD_FreePreparsedData(preparsed);
        }
    }

    private static void AddCapabilities(
        List<HidCapability> capabilities,
        nint preparsed,
        HidReportType reportType,
        bool buttons,
        ushort count)
    {
        if (count == 0)
        {
            return;
        }

        var entries = new NativeHid.HidpCapsEntry[count];
        var length = count;
        var status = buttons
            ? NativeHid.HidP_GetButtonCaps((int)reportType, entries, ref length, preparsed)
            : NativeHid.HidP_GetValueCaps((int)reportType, entries, ref length, preparsed);
        if (status != NativeHid.HIDP_STATUS_SUCCESS)
        {
            throw new IOException($"The {reportType} {(buttons ? "button" : "value")} capabilities could not be read.");
        }

        foreach (var entry in entries.AsSpan(0, Math.Min(length, count)))
        {
            capabilities.Add(buttons
                ? new HidCapability
                {
                    ReportType = reportType,
                    IsButton = true,
                    ReportId = entry.ReportId,
                    UsagePage = entry.UsagePage,
                    UsageMin = entry.UsageMin,
                    UsageMax = entry.IsRange != 0 ? entry.UsageMax : entry.UsageMin,
                    LinkCollection = entry.LinkCollection,
                    IsAbsolute = entry.IsAbsolute != 0,
                    BitSize = 1,
                    ReportCount = entry.ButtonReportCount
                }
                : new HidCapability
                {
                    ReportType = reportType,
                    IsButton = false,
                    ReportId = entry.ReportId,
                    UsagePage = entry.UsagePage,
                    UsageMin = entry.UsageMin,
                    UsageMax = entry.IsRange != 0 ? entry.UsageMax : entry.UsageMin,
                    LinkCollection = entry.LinkCollection,
                    IsAbsolute = entry.IsAbsolute != 0,
                    BitSize = entry.BitSize,
                    ReportCount = entry.ValueReportCount,
                    HasNull = entry.HasNull != 0,
                    LogicalMin = entry.LogicalMin,
                    LogicalMax = entry.LogicalMax,
                    PhysicalMin = entry.PhysicalMin,
                    PhysicalMax = entry.PhysicalMax,
                    Units = entry.Units,
                    UnitsExponent = entry.UnitsExponent
                });
        }
    }

    internal static HidCapability[] InputValues(nint data, ushort count)
    {
        List<HidCapability> capabilities = [];
        AddCapabilities(capabilities, data, HidReportType.Input, false, count);
        return capabilities.ToArray();
    }

    private static string? ReadString(SafeFileHandle handle, Func<SafeFileHandle, byte[], uint, bool> read)
    {
        var buffer = new byte[StringBufferBytes];
        if (!read(handle, buffer, (uint)buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }
}
