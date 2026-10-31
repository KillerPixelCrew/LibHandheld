using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Zotac;

internal static class ZotacModels
{
    internal const string FamilyId = "zotac";

    /// <summary>ZOTAC's own handheld manager, as HC 1.3.1.6's ZotacLauncherWatcher names it.</summary>
    internal static IReadOnlyList<OemManagerApp> ManagerApps { get; } =
    [
        new("zotac-launcher", "Zotac Gaming Zone", ["ZotacHandheldQuickSetting"],
            ["ZotacHandheldDatabaseService", "ZotacHandheldService"], [])
    ];

    internal static readonly ProfiledDeviceModel Zone = new()
    {
        Definition = new HandheldDefinition("zotac-zone", FamilyId, "ZOTAC GAMING ZONE", "zotac-zone")
            { HardwareAccess = DeviceHardwareAccess.PawnIo | DeviceHardwareAccess.InpOut },
        SourceFamilyId = "GamingZone", Manufacturers = ["PC PARTNER LIMITED", "ZOTAC"], Products = ["G0A1W"],
        Cpu = CpuPowerKind.Amd, MinimumWatts = 8, MaximumWatts = 30,
        // HC GamingZone.DevicePowerProfiles, equal sustained/slow targets.
        PowerPresets =
        [
            new DevicePowerPreset("battery", "Battery", 8, 8, DevicePowerMode.BetterBattery),
            new DevicePowerPreset("balanced", "Balanced", 15, 15, DevicePowerMode.Balanced),
            new DevicePowerPreset("performance", "Performance", 28, 28, DevicePowerMode.BestPerformance)
        ],
        Fan = new EcFanProfile(0x4E, 0x4F, [0x044B], 255, 0x044A, ObserveDuty: true),
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
        CreateOemSource = ZotacWheelSource.Create, NativeOemReplacesChords = [],
        OemControls = ZotacWheelSource.Controls,
        NativeRoles =
        [
            CapabilityRole.LightingEffect, CapabilityRole.LightingBrightness, CapabilityRole.LightingEffectSpeed,
            CapabilityRole.LightingZoneColor, CapabilityRole.GenericRange
        ],
        ServiceFactories = [context => new ZotacNativeService(context)]
    };

    /// <summary>HC selects the baseboard, HHD the system product; both name the same physical model.</summary>
    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        return Zone.Matches(identity)
               || string.Equals(identity.SystemProduct, "ZOTAC GAMING ZONE", StringComparison.OrdinalIgnoreCase)
            ? Zone
            : null;
    }}
