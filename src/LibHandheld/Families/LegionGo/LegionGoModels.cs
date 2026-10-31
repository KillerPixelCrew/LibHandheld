using LibHandheld.Contracts;

namespace LibHandheld.Families.LegionGo;

internal sealed record LegionGoModel(
    string DefinitionId,
    string DisplayName,
    string SystemModel,
    bool Slim,
    bool EcFan,
    int MinimumWatts,
    int MaximumWatts)
{
    private static readonly ushort[] SlimProducts = [0xE310, 0xE311];
    private static readonly ushort[] TabletProducts = [0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE];
    public string GlyphProfileId => DefinitionId.Replace('.', '-');
    public ushort VendorId => Slim ? (ushort)0x1A86 : (ushort)0x17EF;
    public IReadOnlyList<ushort> ProductIds => Slim ? SlimProducts : TabletProducts;
}

internal static class LegionGoModels
{
    public const string PackageId = "legion-go";

    /// <summary>Lenovo's own handheld manager, as HC 1.3.1.6's LegionSpaceWatcher names it.</summary>
    public static IReadOnlyList<OemManagerApp> ManagerApps { get; } =
    [
        new("legion-space", "Legion Space", ["LegionGoQuickSettings", "LegionSpace", "LSDaemon"], ["DAService"], [])
    ];

    public static IReadOnlyList<LegionGoModel> All { get; } = Array.AsReadOnly<LegionGoModel>(
    [
        new LegionGoModel("lenovo.legion-go.83e1", "Lenovo Legion Go", "83E1", false, false, 5, 30),
        new LegionGoModel("lenovo.legion-go.83n0", "Lenovo Legion Go 2", "83N0", false, true, 15, 35),
        new LegionGoModel("lenovo.legion-go.83n1", "Lenovo Legion Go 2", "83N1", false, true, 15, 35),
        new LegionGoModel("lenovo.legion-go-s.83l3", "Lenovo Legion Go S Z2 Go", "83L3", true, false, 5, 30),
        new LegionGoModel("lenovo.legion-go-s.83n6", "Lenovo Legion Go S Z1 Extreme", "83N6", true, false, 5, 30),
        new LegionGoModel("lenovo.legion-go-s.83q2", "Lenovo Legion Go S Z1 Extreme", "83Q2", true, false, 5, 30),
        new LegionGoModel("lenovo.legion-go-s.83q3", "Lenovo Legion Go S Z1 Extreme", "83Q3", true, false, 5, 30)
    ]);

    public static LegionGoModel? ById(string id)
    {
        return All.FirstOrDefault(m => m.DefinitionId == id);
    }

    public static LegionGoModel? Match(DeviceIdentitySnapshot identity)
    {
        if (!string.Equals(identity.SystemManufacturer, "LENOVO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return All.FirstOrDefault(m => string.Equals(identity.SystemProduct, m.SystemModel,
            StringComparison.OrdinalIgnoreCase));
    }
}
