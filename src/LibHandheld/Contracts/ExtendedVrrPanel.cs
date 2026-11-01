using System.Security.Cryptography;
using System.Text;

namespace LibHandheld.Contracts;

/// <summary>One of the EDIDs <see cref="ExtendedVrrPanel" /> writes, recognized by its hash.</summary>
/// <param name="FloorHz">The variable refresh floor every floor field carries.</param>
/// <param name="TestBuild">
///     Whether it is the earlier test build's variant, which left the second DisplayID Adaptive-Sync
///     descriptor at the physical floor.
/// </param>
public sealed record ExtendedVrrVariant(int FloorHz, bool TestBuild);

/// <summary>What the EDID Windows reports says about an <see cref="ExtendedVrrPanel" />.</summary>
/// <param name="ReportedSha256">The reported EDID's SHA-256, upper-case hex.</param>
/// <param name="Physical">The exact physical EDID behind the reported one, or null when it is not this panel's.</param>
/// <param name="Reported">The variant Windows reports, or null for the physical EDID or a refused one.</param>
/// <param name="Refusal">Why nothing may be changed, or null when the EDID is the physical one or a known variant.</param>
public sealed record ExtendedVrrEdid(string ReportedSha256, byte[]? Physical, ExtendedVrrVariant? Reported,
    string? Refusal)
{
    /// <summary>Whether the reported EDID is the validated physical one or one of the known variants.</summary>
    public bool Supported => Refusal is null && Physical is not null;
}

/// <summary>The Intel variable refresh state measured on the panel before any change.</summary>
/// <param name="ArcSyncProfile">The Intel Arc Sync profile number (<c>ctl_intel_arc_sync_profile_t</c>).</param>
/// <param name="MinimumHz">The profile's minimum refresh rate.</param>
/// <param name="MaximumHz">The profile's maximum refresh rate.</param>
/// <param name="MaxFrameTimeIncreaseUs">The profile's frame time increase limit.</param>
/// <param name="MaxFrameTimeDecreaseUs">The profile's frame time decrease limit.</param>
/// <param name="LowFpsSolution">Whether Intel's low frame rate solution (LFC) was on.</param>
/// <param name="HighFpsSolution">Whether Intel's high frame rate solution was on.</param>
public sealed record ExtendedVrrFactoryState(
    int ArcSyncProfile,
    double MinimumHz,
    double MaximumHz,
    uint MaxFrameTimeIncreaseUs,
    uint MaxFrameTimeDecreaseUs,
    bool LowFpsSolution,
    bool HighFpsSolution);

/// <summary>
///     An internal panel whose variable refresh floor an EDID override lowers: the exact panel, where its floor
///     fields sit and how the variants are built and recognized.
/// </summary>
/// <remarks>
///     Pure facts and byte arithmetic; nothing here reads or writes Windows or a driver. A variant is the physical
///     EDID with every floor field set to the new floor and the DisplayID and both block checksums recomputed.
///     Restoring every floor field to the physical floor must give back the exact physical EDID, which is how an
///     overridden EDID that Windows reports is traced back to the validated panel. The consumer owns the override,
///     the driver profile and every decision; see the family's PROVENANCE.md for the measurements.
/// </remarks>
public sealed class ExtendedVrrPanel
{
    /// <summary>The DisplayID section the extension block's own checksum covers starts here.</summary>
    private const int DisplayIdSectionStart = 129;

    /// <summary>The DisplayID section checksum.</summary>
    private const int DisplayIdChecksumOffset = 254;

    private const int BlockLength = 128;

    private readonly Lock _gate = new();
    private (string Physical, Dictionary<string, ExtendedVrrVariant> Known)? _known;

    internal ExtendedVrrPanel(string manufacturer, string productCode, string name, string physicalSha256,
        int physicalFloorHz, int maximumHz, int floorHz, IReadOnlyList<int> floorOffsets,
        IReadOnlyList<int> testBuildFloorOffsets, ExtendedVrrFactoryState factory)
    {
        Manufacturer = manufacturer;
        ProductCode = productCode;
        Name = name;
        PhysicalSha256 = physicalSha256;
        PhysicalFloorHz = physicalFloorHz;
        MaximumHz = maximumHz;
        FloorHz = floorHz;
        FloorOffsets = Array.AsReadOnly<int>([.. floorOffsets]);
        TestBuildFloorOffsets = Array.AsReadOnly<int>([.. testBuildFloorOffsets]);
        Factory = factory;
    }

    /// <summary>The EDID manufacturer, as Windows' <c>WmiMonitorID</c> spells it.</summary>
    public string Manufacturer { get; }

    /// <summary>The EDID product code as four upper-case hex digits.</summary>
    public string ProductCode { get; }

    /// <summary>The EDID monitor name.</summary>
    public string Name { get; }

    /// <summary>The length of the panel's EDID: the base block and one DisplayID extension.</summary>
    public int EdidLength => 2 * BlockLength;

    /// <summary>The SHA-256 of the validated physical EDID, upper-case hex.</summary>
    public string PhysicalSha256 { get; }

    /// <summary>The floor every floor field of the physical EDID carries.</summary>
    public int PhysicalFloorHz { get; }

    /// <summary>The panel's maximum refresh rate, which no variant changes.</summary>
    public int MaximumHz { get; }

    /// <summary>The floor a variant lowers to: the lowest at which the panel was seen not to flicker.</summary>
    public int FloorHz { get; }

    /// <summary>
    ///     The offsets of the floor fields: the base range descriptor, the DisplayID dynamic timing range limits,
    ///     the AMD FreeSync VSDB and both DisplayID Adaptive-Sync descriptors.
    /// </summary>
    public IReadOnlyList<int> FloorOffsets { get; }

    /// <summary>The offsets the earlier test build patched: every floor field but the second Adaptive-Sync descriptor.</summary>
    public IReadOnlyList<int> TestBuildFloorOffsets { get; }

    /// <summary>The Intel state measured on the panel before any change, restored when nothing else was saved.</summary>
    public ExtendedVrrFactoryState Factory { get; }

    /// <summary>Whether an EDID names this panel: its manufacturer, product code and monitor name.</summary>
    /// <param name="edid">The EDID Windows reports.</param>
    /// <returns>True for this panel's EDID or any variant of it; the physical hash is checked by <see cref="Read" />.</returns>
    public bool Identifies(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < BlockLength)
        {
            return false;
        }

        var packed = (edid[8] << 8) | edid[9];
        var manufacturer = new string([
            (char)('@' + ((packed >> 10) & 31)), (char)('@' + ((packed >> 5) & 31)), (char)('@' + (packed & 31))
        ]);
        var product = (edid[10] | (edid[11] << 8)).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        return manufacturer == Manufacturer && product == ProductCode && MonitorName(edid) == Name;
    }

    /// <summary>Builds the variant of the physical EDID whose floor fields all carry one floor.</summary>
    /// <param name="physical">The physical EDID, <see cref="EdidLength" /> bytes.</param>
    /// <param name="floorHz">The floor, 1 to 255.</param>
    /// <returns>The variant with valid DisplayID and block checksums.</returns>
    /// <exception cref="ArgumentException">The EDID length or the floor is invalid.</exception>
    public byte[] CreateEdid(ReadOnlySpan<byte> physical, int floorHz)
    {
        return Create(physical, floorHz, FloorOffsets);
    }

    /// <summary>Traces the EDID Windows reports back to the validated physical EDID.</summary>
    /// <param name="reported">The reported EDID, an override's while one is loaded.</param>
    /// <returns>The physical EDID and the variant in force, or why nothing may be changed.</returns>
    public ExtendedVrrEdid Read(ReadOnlySpan<byte> reported)
    {
        var hash = Sha256(reported);
        if (reported.Length != EdidLength)
        {
            return new ExtendedVrrEdid(hash, null, null,
                $"Unexpected EDID length {reported.Length}; expected {EdidLength}.");
        }

        // With an override loaded Windows reports the overridden EDID. Restoring every floor field to the
        // physical floor must then give back the exact physical EDID.
        var physical = hash == PhysicalSha256 ? reported.ToArray() : Create(reported, PhysicalFloorHz, FloorOffsets);
        if (Sha256(physical) != PhysicalSha256)
        {
            return new ExtendedVrrEdid(hash, null, null,
                $"Unsupported panel EDID {hash}. Only the validated {Name} panel and its own variants are accepted.");
        }

        if (hash == PhysicalSha256)
        {
            return new ExtendedVrrEdid(hash, physical, null, null);
        }

        return Known(physical).TryGetValue(hash, out var variant)
            ? new ExtendedVrrEdid(hash, physical, variant, null)
            : new ExtendedVrrEdid(hash, physical, null,
                $"The active EDID {hash} is not one of the extended range variants. Remove other EDID tools "
                + "(for example CRU's reset-all) first.");
    }

    /// <summary>Recognizes an EDID as one of the variants of a physical EDID.</summary>
    /// <param name="physical">The physical EDID, from <see cref="Read" />.</param>
    /// <param name="candidate">The EDID, such as an override's.</param>
    /// <returns>The variant, or null for anything else, the physical EDID included.</returns>
    public ExtendedVrrVariant? Recognize(ReadOnlySpan<byte> physical, ReadOnlySpan<byte> candidate)
    {
        return candidate.Length == EdidLength && Known(physical.ToArray()).TryGetValue(Sha256(candidate), out var known)
            ? known
            : null;
    }

    /// <summary>The SHA-256 of some bytes as upper-case hex.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Every variant this panel's tools can write, floors 10 to 47 with both offset sets, by hash.</summary>
    private Dictionary<string, ExtendedVrrVariant> Known(byte[] physical)
    {
        var key = Sha256(physical);
        lock (_gate)
        {
            if (_known is { } cached && cached.Physical == key)
            {
                return cached.Known;
            }
        }

        Dictionary<string, ExtendedVrrVariant> known = new(StringComparer.Ordinal);
        for (var floor = 10; floor <= 47; floor++)
        {
            known[Sha256(Create(physical, floor, FloorOffsets))] = new ExtendedVrrVariant(floor, false);
            known[Sha256(Create(physical, floor, TestBuildFloorOffsets))] = new ExtendedVrrVariant(floor, true);
        }

        lock (_gate)
        {
            _known = (key, known);
        }

        return known;
    }

    private byte[] Create(ReadOnlySpan<byte> physical, int floorHz, IReadOnlyList<int> offsets)
    {
        if (physical.Length != EdidLength)
        {
            throw new ArgumentException($"The EDID must be {EdidLength} bytes.", nameof(physical));
        }

        if (floorHz is < 1 or > 255)
        {
            throw new ArgumentException("The floor must fit one EDID byte.", nameof(floorHz));
        }

        var edid = physical.ToArray();
        foreach (var offset in offsets)
        {
            edid[offset] = (byte)floorHz;
        }

        SetChecksums(edid);
        return edid;
    }

    /// <summary>Recomputes the DisplayID section checksum, then the base and extension block checksums.</summary>
    private static void SetChecksums(byte[] edid)
    {
        edid[DisplayIdChecksumOffset] = Checksum(edid.AsSpan(DisplayIdSectionStart,
            DisplayIdChecksumOffset - DisplayIdSectionStart));
        foreach (var start in (int[]) [0, BlockLength])
        {
            edid[start + BlockLength - 1] = Checksum(edid.AsSpan(start, BlockLength - 1));
        }
    }

    private static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
        }

        return (byte)((256 - sum % 256) % 256);
    }

    /// <summary>The monitor name descriptor's text (tag FC), as Windows' <c>WmiMonitorID</c> reads it.</summary>
    private static string? MonitorName(ReadOnlySpan<byte> edid)
    {
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = edid.Slice(offset, 18);
            if (descriptor[0] == 0 && descriptor[1] == 0 && descriptor[2] == 0 && descriptor[3] == 0xfc)
            {
                var text = descriptor.Slice(5, 13);
                var end = text.IndexOf((byte)0x0a);
                return Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
            }
        }

        return null;
    }
}
