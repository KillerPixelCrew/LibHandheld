using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Zotac;

internal static class ZotacModels
{
    internal const string FamilyId = "zotac";

    private static readonly ProfiledDeviceModel Zone = new()
    {
        Definition = new HandheldDefinition("zotac-zone", FamilyId, "ZOTAC GAMING ZONE", "zotac-zone")
            { HardwareAccess = DeviceHardwareAccess.PawnIo },
        SourceFamilyId = "GamingZone", Manufacturers = ["PC PARTNER LIMITED", "ZOTAC"], Products = ["G0A1W"],
        Cpu = CpuPowerKind.Amd, MinimumWatts = 8, MaximumWatts = 30,
        // HC GamingZone.DevicePowerProfiles, equal sustained/slow targets.
        PowerPresets =
        [
            new DevicePowerPreset("battery", "Battery", 8, 8, DevicePowerMode.BetterBattery),
            new DevicePowerPreset("balanced", "Balanced", 15, 15, DevicePowerMode.Balanced),
            new DevicePowerPreset("performance", "Performance", 28, 28, DevicePowerMode.BestPerformance)
        ],
        Fan = new EcFanProfile(0x4E, 0x4F, [0x044B], 255, 0x044A),
        Controller = new StandardControllerProfile([new ControllerUsbId(0x1EE9, 0x1590)]),
        Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1), new MotionAxisMap(0, 2, 1, 1, 1, 1)),
        KeyboardChords =
        [
            new KeyboardOemChord("zone", "ZONE", [0xA2, 0x5B, 0x80], CanonicalButtons.Guide),
            new KeyboardOemChord("dots", "Dots", [0xA2, 0x5B, 0x81], CanonicalButtons.QuickAccess),
            new KeyboardOemChord("home", "Home", [0x5B, 0x44], CanonicalButtons.None),
            new KeyboardOemChord("m1", "M1", [0xA2, 0x5B, 0x7A], CanonicalButtons.RearPaddle1),
            new KeyboardOemChord("m2", "M2", [0xA2, 0x5B, 0x7B], CanonicalButtons.RearPaddle2)
        ],
        CreateOemSource = () => new ZotacWheelSource(), NativeOemReplacesChords = [],
        OemControls = ZotacWheelSource.Controls,
        NativeRoles =
        [
            CapabilityRole.LightingEffect, CapabilityRole.LightingBrightness, CapabilityRole.LightingEffectSpeed,
            CapabilityRole.LightingZoneColor, CapabilityRole.GenericRange
        ],
        ServiceFactories = [context => new ZotacNativeService(context)]
    };

    // HHD selects the system product, HC the baseboard. Both represent the same physical model.
    private static readonly ProfiledDeviceModel HhdZone = Zone with
    {
        IdentitySource = ProfileIdentitySource.System, Products = ["ZOTAC GAMING ZONE"]
    };

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return [Zone.DeclaredDefinition];
    }

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        return Zone.Matches(identity)
            ? Zone
            : string.Equals(identity.SystemProduct, "ZOTAC GAMING ZONE", StringComparison.OrdinalIgnoreCase)
                ? HhdZone with
                {
                    Manufacturers = [identity.SystemManufacturer ?? identity.BaseboardManufacturer ?? ""],
                    Processors = [identity.ProcessorName ?? ""]
                }
                : null;
    }

    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(Zone);
    }
}
