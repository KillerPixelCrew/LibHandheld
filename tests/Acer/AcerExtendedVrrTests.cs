using LibHandheld.Contracts;
using LibHandheld.Families.Acer;

namespace LibHandheld.Tests.Acer;

/// <summary>The Atlas panel's extended VRR variants, against hashes the Atlas8Tweaks script itself produced.</summary>
/// <remarks>
///     The fixture is a synthetic 256-byte EDID named BOE 0CFF NS080WUM-LX1 with every floor field at 48 Hz.
///     Its hash and the variant hashes were computed by running the script's own Set-EdidChecksums,
///     New-EdidVariant and Get-Sha256 functions on the same bytes, so a match proves byte parity with it.
///     The real panel's physical EDID is not in the repository; only its hash is.
/// </remarks>
public sealed class AcerExtendedVrrTests
{
    private const string PhysicalHex =
        "00FFFFFFFFFFFF0009E5FF0CCCF11B40658AAFD4F923486D92B7DC062B50759ABFE40E33587DA2C7EC163B6085AACFF41E43688DB2D7"
        + "000000FC004E5330383057554D2D4C58310AA5CAEF193E6388ADD2F721466B90B5DA04294E7398BDE23031567BA0C5EA14395E83A8"
        + "CDF21C41668BB0D5FA24496E93B8DD072C51769B8CE50F34597EA3C8ED173C6186ABD0301F44698EB3D802274C719630E00A2F5479"
        + "9EC3E812375C81A6CBF01A3F648930D3F822476C30B6DB052A4F7499BEE30D32577CA1C6EB153A5F84A9CEF31D42678CB1D600254A"
        + "6F94B9DE082D52779CC1E610355A7FA4C9EE183D6287ACD1F620456A8FB4D903284D7297BCE10B3055791B";

    private const string PhysicalSha256 = "8D1C1A531475FF3D3DE4D19E305D4B084F4867DE7D53C698D030FA9D137EFABB";
    private const string Variant28Sha256 = "2F2E57AEED1D3A1928DBDCA9033F7A29B6A1153AA878A2E83B48631FADCD9E04";
    private const string TestBuild28Sha256 = "7E51F0F50F10CCAF20891B11A2EDA166D8DCA98A802EC17A35AABD57DF853572";
    private const string Variant30Sha256 = "DFFCACE48E838C7E55EE6AA4BE1FA0D49577340A1F503C56E4877117801B34C1";

    private static readonly byte[] Physical = Convert.FromHexString(PhysicalHex);

    [Fact]
    public void TheAtlasCarriesTheMeasuredPanelFacts()
    {
        var panel = Assert.IsType<ExtendedVrrPanel>(HandheldDevice.Definitions
            .Single(item => item.Id == AcerModels.Atlas8.Definition.Id).ExtendedVrrPanel);

        Assert.Equal(("BOE", "0CFF", "NS080WUM-LX1", 256), (panel.Manufacturer, panel.ProductCode, panel.Name,
            panel.EdidLength));
        Assert.Equal("F05B01B33B82B9597AE7BB15F5EF43CAEDB9194BF4A81B3008D34E8DBAC6ECCE", panel.PhysicalSha256);
        Assert.Equal((48, 120, 28), (panel.PhysicalFloorHz, panel.MaximumHz, panel.FloorHz));
        Assert.Equal([95, 142, 154, 174, 180], panel.FloorOffsets);
        Assert.Equal([95, 142, 154, 174], panel.TestBuildFloorOffsets);
        Assert.Equal(new ExtendedVrrFactoryState(7, 48, 120, 0, 0, true, true), panel.Factory);
        Assert.All(HandheldDevice.Definitions.Where(item => item.Id != AcerModels.Atlas8.Definition.Id),
            item => Assert.Null(item.ExtendedVrrPanel));
    }

    [Fact]
    public void VariantsMatchTheScriptByteForByte()
    {
        var panel = Synthetic();

        var variant = panel.CreateEdid(Physical, 28);

        Assert.Equal(Variant28Sha256, ExtendedVrrPanel.Sha256(variant));
        Assert.Equal(Variant30Sha256, ExtendedVrrPanel.Sha256(panel.CreateEdid(Physical, 30)));
        Assert.All(panel.FloorOffsets, offset => Assert.Equal(28, variant[offset]));
        Assert.Equal((160, 201, 27), (variant[127], variant[254], variant[255]));
    }

    [Fact]
    public void AReportedVariantTracesBackToTheExactPhysicalEdid()
    {
        var panel = Synthetic();

        var reading = panel.Read(panel.CreateEdid(Physical, 28));

        Assert.True(reading.Supported);
        Assert.Equal(Physical, reading.Physical);
        Assert.Equal(new ExtendedVrrVariant(28, false), reading.Reported);
        Assert.Equal(Variant28Sha256, reading.ReportedSha256);
    }

    [Fact]
    public void ThePhysicalEdidReadsAsNoVariant()
    {
        var reading = Synthetic().Read(Physical);

        Assert.True(reading.Supported);
        Assert.Null(reading.Reported);
        Assert.Equal(PhysicalSha256, reading.ReportedSha256);
    }

    [Fact]
    public void TheTestBuildsVariantIsRecognizedAsOurs()
    {
        var panel = Synthetic();
        var testBuild = Physical.ToArray();
        foreach (var offset in panel.TestBuildFloorOffsets)
        {
            testBuild[offset] = 28;
        }

        Fix(testBuild);
        Assert.Equal(TestBuild28Sha256, ExtendedVrrPanel.Sha256(testBuild));

        Assert.Equal(new ExtendedVrrVariant(28, true), panel.Read(testBuild).Reported);
        Assert.Equal(new ExtendedVrrVariant(28, true), panel.Recognize(Physical, testBuild));
    }

    [Fact]
    public void AnotherPanelsEdidIsRefused()
    {
        var other = Physical.ToArray();
        other[20] ^= 1;

        var reading = Synthetic().Read(other);

        Assert.False(reading.Supported);
        Assert.Contains("Unsupported panel EDID", reading.Refusal);
    }

    [Fact]
    public void AForeignEdidOfThisPanelIsRefusedAndAForeignOverrideIsNotOurs()
    {
        var panel = Synthetic();
        // Another tool lowered only the base range descriptor's floor.
        var foreign = Physical.ToArray();
        foreign[95] = 30;
        Fix(foreign);

        Assert.Contains("Remove other EDID tools", panel.Read(foreign).Refusal);
        Assert.Null(panel.Recognize(Physical, foreign));
        Assert.Null(panel.Recognize(Physical, Physical));
        Assert.Contains("Unexpected EDID length", panel.Read(Physical.AsSpan(0, 128)).Refusal);
    }

    [Fact]
    public void ThePanelIsIdentifiedByManufacturerProductAndName()
    {
        var panel = Synthetic();
        Assert.True(panel.Identifies(Physical));
        Assert.True(panel.Identifies(panel.CreateEdid(Physical, 28)));

        var renamed = Physical.ToArray();
        renamed[60] = (byte)'X';
        Assert.False(panel.Identifies(renamed));
        var otherProduct = Physical.ToArray();
        otherProduct[10] = 0x00;
        Assert.False(panel.Identifies(otherProduct));
    }

    private static ExtendedVrrPanel Synthetic()
    {
        var atlas = AcerModels.AtlasPanel;
        return new ExtendedVrrPanel(atlas.Manufacturer, atlas.ProductCode, atlas.Name, PhysicalSha256,
            atlas.PhysicalFloorHz, atlas.MaximumHz, atlas.FloorHz, atlas.FloorOffsets, atlas.TestBuildFloorOffsets,
            atlas.Factory);
    }

    /// <summary>The script's checksum order: DisplayID section, then both blocks.</summary>
    private static void Fix(byte[] edid)
    {
        var sum = 0;
        for (var index = 129; index < 254; index++)
        {
            sum += edid[index];
        }

        edid[254] = (byte)((256 - sum % 256) % 256);
        foreach (var start in new[] { 0, 128 })
        {
            sum = 0;
            for (var index = start; index < start + 127; index++)
            {
                sum += edid[index];
            }

            edid[start + 127] = (byte)((256 - sum % 256) % 256);
        }
    }
}
