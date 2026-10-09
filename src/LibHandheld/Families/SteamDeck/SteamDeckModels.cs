using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Plugin;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.SteamDeck;

internal static class SteamDeckModels
{
    internal const string FamilyId = "valve.steamdeck";

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } = Array.AsReadOnly<HandheldDefinition>(
    [
        new HandheldDefinition("steamdeck-lcd", FamilyId, "Steam Deck LCD", "generic")
            { HardwareAccess = DeviceHardwareAccess.InpOut },
        new HandheldDefinition("steamdeck-oled", FamilyId, "Steam Deck OLED", "generic")
            { HardwareAccess = DeviceHardwareAccess.InpOut },
        new HandheldDefinition("steam-machine", FamilyId, "Steam Machine", "generic")
            { HardwareAccess = DeviceHardwareAccess.PawnIo, HasController = false }
    ]);

    internal static string? Match(DeviceIdentitySnapshot identity)
    {
        if (!string.Equals(identity.SystemManufacturer?.Trim(), "Valve", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return identity.SystemProduct?.Trim().ToUpperInvariant() switch
        {
            "JUPITER" => Definitions[0].Id,
            "GALILEO" => Definitions[1].Id,
            "FREMONT" => Definitions[2].Id,
            _ => null
        };
    }

    internal static IDeviceEngine CreateEngine(HandheldDefinition definition)
    {
        return definition.Id == "steam-machine"
            ? new ProfiledDeviceEngine(new ProfiledDeviceModel
            {
                Definition = definition, SourceFamilyId = "SteamMachine", Manufacturers = ["Valve"],
                Products = ["Fremont"],
                IdentitySource = ProfileIdentitySource.System, Cpu = CpuPowerKind.Amd, MinimumWatts = 35,
                MaximumWatts = 45,
                CreateNativeService = context => new SteamMachineLighting(context)
            })
            : new SteamDeckDeviceEngine(definition);
    }

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return Definitions;
    }

    internal static ProfiledDeviceModel Profile(HandheldDefinition definition)
    {
        return new ProfiledDeviceModel
        {
            Definition = definition, SourceFamilyId = "SteamDeck", Manufacturers = ["Valve"],
            Products = [definition.Id == "steamdeck-oled" ? "Galileo" : "Jupiter"],
            IdentitySource = ProfileIdentitySource.System, Cpu = CpuPowerKind.Amd, MinimumWatts = 4, MaximumWatts = 15
        };
    }
}
