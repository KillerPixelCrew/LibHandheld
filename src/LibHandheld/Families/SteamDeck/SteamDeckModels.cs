using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.SteamDeck;

internal static class SteamDeckModels
{
    internal const string FamilyId = "valve";

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } = Array.AsReadOnly<HandheldDefinition>(
    [
        new HandheldDefinition("steamdeck-lcd", FamilyId, "Steam Deck LCD", "steamdeck-lcd")
            { HardwareAccess = DeviceHardwareAccess.InpOut, NativeSteamController = true, DeclaredRoles = DeckRoles },
        new HandheldDefinition("steamdeck-oled", FamilyId, "Steam Deck OLED", "steamdeck-oled")
            { HardwareAccess = DeviceHardwareAccess.InpOut, NativeSteamController = true, DeclaredRoles = DeckRoles },
        new HandheldDefinition("steam-machine", FamilyId, "Steam Machine", "steam-machine")
        {
            HardwareAccess = DeviceHardwareAccess.PawnIo, HasController = false,
            DeclaredRoles =
            [
                CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit,
                CapabilityRole.LightingPower, CapabilityRole.LightingBrightness,
                CapabilityRole.LightingZoneColor, CapabilityRole.LightingEffect
            ]
        }
    ]);

    private static IReadOnlyList<CapabilityRole> DeckRoles =>
    [
        CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit,
        CapabilityRole.ControllerSource, CapabilityRole.MotionSource, CapabilityRole.HapticSink,
        CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanTargetRpm,
        CapabilityRole.FanMeasuredRpm, CapabilityRole.ChargeLimit, CapabilityRole.Telemetry,
        CapabilityRole.GenericRange, CapabilityRole.GenericAction
    ];

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

    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return definition.Id == "steam-machine"
            ? new ProfiledDeviceEngine(new ProfiledDeviceModel
            {
                Definition = definition, SourceFamilyId = "SteamMachine", Manufacturers = ["Valve"],
                Products = ["Fremont"],
                IdentitySource = ProfileIdentitySource.System, Cpu = CpuPowerKind.Amd, MinimumWatts = 35,
                MaximumWatts = 45,
                ServiceFactories = [context => new SteamMachineLighting(context)]
            })
            : new ProfiledDeviceEngine(Profile(definition));
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
            IdentitySource = ProfileIdentitySource.System, Cpu = CpuPowerKind.Amd, MinimumWatts = 4, MaximumWatts = 15,
            OpenPower = context => VangoghPower.Open(context.Resources.InpOutPath),
            ServiceFactories = [context => new SteamDeckNativeService(context, NeptuneDevice.Open)],
            OemControls = new[] { "steam", "quick-access", "l4", "r4", "l5", "r5" }.Select(id =>
                new OemControlDescriptor
                {
                    ControlId = id, Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = id },
                    Placement = id is "steam" or "quick-access" ? OemControlPlacement.Front : OemControlPlacement.Rear,
                    RequiresControllerAcquisition = true,
                    ControllerButton = id switch
                    {
                        "steam" => CanonicalButtons.Guide,
                        "quick-access" => CanonicalButtons.QuickAccess,
                        _ => CanonicalButtons.None
                    }
                }).ToArray()
        };
    }
}
