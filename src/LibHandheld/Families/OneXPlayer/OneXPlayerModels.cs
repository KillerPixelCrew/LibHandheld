using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>Model facts select one codec; no model duplicates acquisition, CPU control or input lifetime.</summary>
internal static class OneXPlayerModels
{
    internal const string FamilyId = "onexplayer";
    private static readonly string[] OneNetbook = ["ONE-NETBOOK TECHNOLOGY CO., LTD.", "ONE-NETBOOK"];

    private static readonly StandardControllerProfile Controller = new([new ControllerUsbId(0x045E, 0x028E)])
    {
        CompanionUsbIds =
        [
            new ControllerUsbId(0x1A86, 0xFE00), new ControllerUsbId(0x1A86, 0x1305),
            new ControllerUsbId(0x1A2C, 0xB001)
        ]
    };

    private static readonly StandardMotionProfile DefaultMotion =
        new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1), MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, -1));

    private static readonly StandardMotionProfile MiniMotion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1));

    private static readonly StandardMotionProfile MiniIntelMotion =
        new(MotionAxisMap.FromInputToOutput(1, 2, 0, 1, -1, -1), MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, -1));

    private static readonly StandardMotionProfile MiniProMotion =
        new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1), MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1));

    private static readonly StandardMotionProfile FlyMotion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, 1));

    private static readonly StandardMotionProfile TwoMotion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, -1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, -1));

    private static readonly StandardMotionProfile X2Motion = new(MotionAxisMap.FromInputToOutput(1, 2, 0, 1, 1, -1),
        MotionAxisMap.FromInputToOutput(1, 2, 0, 1, 1, -1));

    private static readonly StandardMotionProfile X2MiniMotion = new(MotionAxisMap.FromInputToOutput(2, 0, 1, 1, -1, -1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, 1));

    private static readonly StandardMotionProfile ThreeMotion = new(MotionAxisMap.FromInputToOutput(1, 0, 2, 1, 1, 1),
        MotionAxisMap.FromInputToOutput(1, 0, 2, 1, 1, 1));

    private static readonly StandardMotionProfile G1Motion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1));

    internal static IReadOnlyList<ProfiledDeviceModel> All { get; } = Array.AsReadOnly<ProfiledDeviceModel>(
    [
        Model("oxp-mini-amd", "ONEXPLAYER Mini (5800U)", "OneXPlayerMiniAMD", ["ONE XPLAYER", "ONEXPLAYER Mini Pro"],
            10, 25,
            new OneXProtocolProfile(OneXEcKind.Acpi, 0xC4, 0xC5, 0, 100, OneXLightingKind.None, 0x1E),
            MiniMotion),
        Model("oxp-mini-intel", "ONEXPLAYER Mini (Intel)", "OneXPlayerMiniIntel",
                ["ONE XPLAYER", "ONEXPLAYER Mini Pro"], 20, 64,
                new OneXProtocolProfile(OneXEcKind.Acpi, 0xC4, 0xC5, 0, 255, OneXLightingKind.None, 0x1E),
                MiniIntelMotion,
                CpuPowerKind.Intel) with
            {
                Versions = ["1002-C"]
            },
        Model("oxp-mini-pro", "ONEXPLAYER Mini Pro (6800U)", "OneXPlayerMiniPro",
                ["ONE XPLAYER", "ONEXPLAYER Mini Pro"], 4, 28,
                new OneXProtocolProfile(OneXEcKind.Acpi, 0xC4, 0xC5, 0, 255, OneXLightingKind.HidV2, 0x1E),
                MiniProMotion) with
            {
                Versions = ["V03"]
            },
        Model("oxp-mini-a07", "ONEXPLAYER Mini A07", "OneXPlayerMiniAMD", ["ONEXPLAYER mini A07"], 10, 25,
                new OneXProtocolProfile(OneXEcKind.Acpi, 0xC4, 0xC5, 0, 100, OneXLightingKind.None, 0x1E),
                MiniMotion) with
            {
                SystemProducts = ["ONEXPLAYER mini A07"]
            },
        Model("oxp-2", "ONEXPLAYER 2", "OneXPlayer2", ["ONEXPLAYER 2 ARP23", "ONEXPLAYER 2", "ONEXPLAYER 2 GA18"], 4,
            28,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.None, 0xEB, 0xEB),
            TwoMotion),
        Model("oxp-2-pro", "ONEXPLAYER 2 Pro", "OneXPlayer2Pro",
            [
                "ONEXPLAYER 2 PRO ARP23P", "ONEXPLAYER 2 PRO ARP23", "ONEXPLAYER 2 PRO ARP23P EVA-01",
                "ONEXPLAYER 2 PRO ARP23 EVA-01"
            ], 4, 30,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.None, 0xEB, 0xEB),
            TwoMotion),
        Model("oxp-f1", "ONEXPLAYER OneXFly", "OneXPlayerOneXFly",
            ["ONEXPLAYER F1", "ONEXPLAYER F1 EVA-01", "ONEXPLAYER F1L", "ONEXPLAYER F1 OLED"], 5, 30,
            new OneXProtocolProfile(OneXEcKind.Acpi, 0x4A, 0x4B, 11, 255, OneXLightingKind.HidV2, SerialButtons: true),
            FlyMotion),
        Model("oxp-f1-pro", "ONEXPLAYER OneXFly F1 Pro", "OneXPlayerOneXFlyF1Pro",
            ["ONEXPLAYER F1Pro", "ONEXPLAYER F1 EVA-02"], 5, 30,
            new OneXProtocolProfile(OneXEcKind.Acpi, 0x4A, 0x4B, 11, 255, OneXLightingKind.HidV2, SerialButtons: true),
            FlyMotion),
        Model("oxp-g1-intel", "ONEXPLAYER G1 (Intel)", "OneXPlayerG1Intel", ["ONEXPLAYER G1 i"], 25, 80,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.HidV1G1), G1Motion,
            CpuPowerKind.Intel),
        Model("oxp-g1-8840u", "ONEXPLAYER G1 (8840U)", "OneXPlayerG1AMD", ["ONEXPLAYER G1 A"], 15, 35,
                new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 255, OneXLightingKind.HidV1G1),
                G1Motion) with
            {
                ProcessorSkus = ["8840U"]
            },
        Model("oxp-g1-hx370", "ONEXPLAYER G1 (HX 370)", "OneXPlayerG1AMD", ["ONEXPLAYER G1 A"], 25, 65,
                new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 255, OneXLightingKind.HidV1G1),
                G1Motion) with
            {
                ProcessorSkus = ["HX 370"]
            },
        Model("oxp-x1-amd", "ONEXPLAYER X1 (AMD)", "OneXPlayerX1AMD", ["ONEXPLAYER X1 A", "ONEXPLAYER X1z"], 15, 30,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial,
                VendorButtons: true, Charge: OneXChargeKind.Amd), DefaultMotion),
        Model("oxp-x1-intel", "ONEXPLAYER X1 (Intel)", "OneXPlayerX1Intel", ["ONEXPLAYER X1 i"], 6, 35,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial,
                VendorButtons: true, Charge: OneXChargeKind.Intel), DefaultMotion, CpuPowerKind.Intel),
        Model("oxp-x1-mini", "ONEXPLAYER X1 Mini", "OneXPlayerX1Mini", ["ONEXPLAYER X1 mini", "ONEXPLAYER X1Mini Pro"],
            15, 30,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.HidV1,
                VendorButtons: true, Vibration: true, SimpleVibration: true, Charge: OneXChargeKind.Amd),
            MiniProMotion),
        Model("oxp-x1-pro", "ONEXPLAYER X1 Pro", "OneXPlayerX1Pro", ["ONEXPLAYER X1Pro"], 15, 30,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial,
                VendorButtons: true, Charge: OneXChargeKind.Amd), DefaultMotion),
        Model("oxp-x1-pro-eva02", "ONEXPLAYER X1 Pro EVA-02 (Intel)", "HHD:X1Pro EVA-02", ["ONEXPLAYER X1Pro EVA-02"],
                6, 35,
                new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial,
                    VendorButtons: true, Charge: OneXChargeKind.Intel), DefaultMotion, CpuPowerKind.Intel) with
            {
                IdentitySource = ProfileIdentitySource.System
            },
        Model("oxp-x1-air", "ONEXPLAYER X1 Air", "HHD:X1Air", ["ONEXPLAYER X1Air"], 6, 35,
                new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.HidV1Secondary,
                    VendorButtons: true), DefaultMotion, CpuPowerKind.Intel) with
            {
                IdentitySource = ProfileIdentitySource.System
            },
        // HC's Apex matrix repeats output Y and is not a permutation. Its orientation
        // requires a raw three-axis hardware capture before the inherited map can be corrected.
        Model("oxp-apex", "ONEXPLAYER Apex", "OneXPlayerApex", ["ONEXPLAYER APEX", "ONEXPLAYERAPEX"], 25, 65,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial,
                VendorButtons: true, Apex: true, Vibration: true, Charge: OneXChargeKind.Amd),
            // HC's Apex map assigns both X and Z to output Y: the later Z wins and output Z remains zero.
            // Preserve that published mapping rather than inventing a physical orientation.
            new StandardMotionProfile(new MotionAxisMap(1, 2, -1, 1, -1, 1),
                new MotionAxisMap(1, 2, -1, -1, 1, -1))),
        Model("oxp-x2", "ONEXPLAYER X2", "OneXPlayerX2", ["ONEXPLAYER X2"], 3, 35,
            new OneXProtocolProfile(OneXEcKind.Wmi, 0x44A, 0x44B, 0, 184, OneXLightingKind.HidV1, VendorButtons: true,
                X2: true, Temperature: true), X2Motion, CpuPowerKind.Intel),
        Model("oxp-x2-mini-pro", "ONEXPLAYER X2 Mini Pro", "OneXPlayerX2MiniPro", ["ONEXPLAYER X2Mini PRO"], 6, 80,
            new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 255, OneXLightingKind.HidV1X2,
                VendorButtons: true, X2: true, Temperature: true, Mini: true), X2MiniMotion),
        Model("oxp-3", "ONEXPLAYER 3", "OneXPlayer3", ["ONEXPLAYER 3", "ONEXPLAYER-3"], 3, 35,
            new OneXProtocolProfile(OneXEcKind.Wmi, 0x44A, 0x44B, 0, 184, OneXLightingKind.HidV1X2, VendorButtons: true,
                X2: true, Temperature: true, Three: true), ThreeMotion, CpuPowerKind.Intel),
        Aok("aokzoe-a1", "AOKZOE A1", "AOKZOEA1", "AOKZOE A1 AR07", 255),
        Aok("aokzoe-a1-pro", "AOKZOE A1 Pro", "AOKZOEA1Pro", "AOKZOE A1 Pro", 184),
        Aok("aokzoe-a1x", "AOKZOE A1X", "AOKZOEA1X", "AOKZOE A1X", 184),
        Aok("aokzoe-a2", "AOKZOE A2 Pro", "AOKZOEA2", "AOKZOE A2 Pro", 184)
    ]);

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } =
        Array.AsReadOnly(All.Select(model => model.DeclaredDefinition).ToArray());

    private static KeyboardOemChord[] FrontChords =>
    [
        new("turbo", "Turbo", [0xA2, 0x5B, 0xA4], CanonicalButtons.QuickAccess),
        new("keyboard", "Keyboard", [0xA3, 0x5B, 0x4F], CanonicalButtons.None),
        new("home", "Home", [0x5B, 0x44], CanonicalButtons.Guide)
    ];

    private static KeyboardOemChord[] AokChords =>
    [
        .. FrontChords,
        new("home-long", "Home (hold)", [0x5B, 0x47], CanonicalButtons.None),
        new("home-keyboard", "Home + Keyboard", [0xA5, 0xA3, 0x2E], CanonicalButtons.None),
        new("home-turbo", "Home + Turbo", [0x5B, 0x2C], CanonicalButtons.None)
    ];

    private static OemControlDescriptor[] NativeControls =>
    [
        Oem("turbo", "Turbo", OemControlPlacement.Front), Oem("keyboard", "Keyboard", OemControlPlacement.Front),
        Oem("home", "Home", OemControlPlacement.Front), Oem("l4", "M1", OemControlPlacement.Rear),
        Oem("r4", "M2", OemControlPlacement.Rear)
    ];

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return Definitions;
    }

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        return All.FirstOrDefault(model => model.IdentitySource == ProfileIdentitySource.Baseboard
                                           && Matches(model with { SystemProducts = [] }, identity))
               ?? All.FirstOrDefault(model => Matches(model, identity));
    }

    private static bool Matches(ProfiledDeviceModel model, DeviceIdentitySnapshot identity)
    {
        if (model.Definition.Id is "oxp-mini-amd" or "oxp-mini-intel" or "oxp-mini-pro")
        {
            // HC's exact Mini product selector has two special revisions and defaults to AMD.
            var id = identity.BaseboardVersion?.Trim().ToUpperInvariant() switch
            {
                "1002-C" => "oxp-mini-intel", "V03" => "oxp-mini-pro", _ => "oxp-mini-amd"
            };
            return model.Definition.Id == id && (model with { Versions = [] }).Matches(identity);
        }

        return model.Matches(identity);
    }

    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(All.Single(model => model.Definition.Id == definition.Id));
    }

    private static ProfiledDeviceModel Aok(string id, string name, string source, string product, byte maximum)
    {
        return Model(id, name, source, [product], 4, 28, new OneXProtocolProfile(OneXEcKind.Acpi, 0x4A, 0x4B, 0,
                maximum,
                OneXLightingKind.HidV2,
                VendorButtons: source == "AOKZOEA1X", Vibration: source == "AOKZOEA1X",
                HasRearButtons: source != "AOKZOEA1X"), DefaultMotion)
            with
            {
                Manufacturers = ["AOKZOE"], KeyboardChords = AokChords
            };
    }

    private static ProfiledDeviceModel Model(string id, string name, string source, string[] products, int minimum,
        int maximum,
        OneXProtocolProfile protocol, StandardMotionProfile motion, CpuPowerKind cpu = CpuPowerKind.Amd)
    {
        return new ProfiledDeviceModel
        {
            Definition = new HandheldDefinition(id, FamilyId, name, id)
            {
                HardwareAccess = DeviceHardwareAccess.PawnIo
            },
            SourceFamilyId = source, Manufacturers = OneNetbook, Products = products,
            SystemProducts = HhdProducts(source), Cpu = cpu,
            MinimumWatts = minimum, MaximumWatts = maximum, Controller = Controller, Motion = motion,
            PowerPresets = Presets(source, minimum, maximum, cpu),
            KeyboardChords = Chords(source, protocol),
            NativeRoles =
            [
                CapabilityRole.FanMode, CapabilityRole.FanDuty,
                .. protocol.Temperature ? new[] { CapabilityRole.Telemetry } : [],
                .. protocol.Charge != OneXChargeKind.None
                    ? new[] { CapabilityRole.ChargeLimit, CapabilityRole.ChargeBypass }
                    : [],
                .. protocol.Lighting != OneXLightingKind.None
                    ? new[]
                    {
                        CapabilityRole.LightingBrightness, CapabilityRole.LightingEffect,
                        CapabilityRole.LightingZoneColor
                    }
                    : [],
                .. protocol.Vibration ? new[] { CapabilityRole.GenericRange } : []
            ],
            ServiceFactories = [context => new OneXNativeService(context, protocol)],
            CreateOemSource = protocol.VendorButtons || protocol.SerialButtons
                ? () => new OneXOemSource(protocol)
                : null,
            NativeOemReplacesChords = protocol.X2 ? ["turbo", "keyboard", "home", "l4", "r4"]
                : protocol.VendorButtons || protocol.SerialButtons ? ["keyboard", "home", "l4", "r4"] : [],
            OemControls = protocol.VendorButtons || protocol.SerialButtons
                ? NativeControls
                    .Where(control => protocol.HasRearButtons || control.Placement != OemControlPlacement.Rear)
                    .ToArray()
                : []
        };
    }

    private static IReadOnlyList<DevicePowerPreset> Presets(string source, int minimum, int maximum, CpuPowerKind cpu)
    {
        // HC IDevice's default profile uses its 15 W sustained/slow pair. X1AMD/Intel
        // additionally author 15/30/35 W profiles. PerformanceManager:414-423 clamps every
        // profile target to the model cTDP (X1AMD:15-30 W, X1Intel:6-35 W), including its 64 W peak.
        List<DevicePowerPreset> presets =
        [
            new("default", "Default", 15, 15, DevicePowerMode.Balanced) { FastWatts = 20 }
        ];
        if (source is "OneXPlayerX1AMD" or "OneXPlayerX1Intel" or "OneXPlayerX1Pro")
        {
            presets.AddRange(
            [
                new DevicePowerPreset("battery", "Battery", 15, 15, DevicePowerMode.BetterBattery)
                    { FastWatts = 15 },
                new DevicePowerPreset("balanced", "Balanced", 30, 30, DevicePowerMode.Balanced)
                    { FastWatts = 30 },
                new DevicePowerPreset("performance", "Performance", 35, 35, DevicePowerMode.BestPerformance)
                    { FastWatts = 64 }
            ]);
        }

        return presets.Select(preset => preset with
        {
            SustainedWatts = Math.Clamp(preset.SustainedWatts, minimum, maximum),
            SlowWatts = Math.Clamp(preset.SlowWatts, minimum, maximum),
            FastWatts = cpu == CpuPowerKind.Amd
                ? Math.Clamp(preset.FastWatts ?? preset.SlowWatts, minimum, maximum)
                : null
        }).ToArray();
    }

    private static IReadOnlyList<string> HhdProducts(string source)
    {
        return source switch
        {
            "OneXPlayerMiniAMD" or "OneXPlayerMiniIntel" => ["ONE XPLAYER", "ONEXPLAYER mini A07"],
            "OneXPlayerMiniPro" => ["ONEXPLAYER Mini Pro"],
            "OneXPlayer2" => ["ONEXPLAYER 2", "ONEXPLAYER 2 ARP23", "ONEXPLAYER 2 GA18"],
            "OneXPlayer2Pro" => ["ONEXPLAYER 2 PRO ARP23", "ONEXPLAYER 2 PRO ARP23 EVA-01"],
            "OneXPlayerOneXFly" => ["ONEXPLAYER F1", "ONEXPLAYER F1 EVA-01", "ONEXPLAYER F1L", "ONEXPLAYER F1 OLED"],
            "OneXPlayerOneXFlyF1Pro" => ["ONEXPLAYER F1Pro", "ONEXPLAYER F1 EVA-02"],
            "OneXPlayerX1Mini" => ["ONEXPLAYER X1 mini", "ONEXPLAYER X1Mini Pro"],
            "OneXPlayerX1AMD" => ["ONEXPLAYER X1 A", "ONEXPLAYER X1z"],
            "OneXPlayerX1Intel" => ["ONEXPLAYER X1 i"],
            "OneXPlayerX1Pro" => ["ONEXPLAYER X1Pro"],
            "OneXPlayerApex" => ["ONEXPLAYER APEX"],
            "OneXPlayerX2MiniPro" => ["ONEXPLAYER X2Mini PRO"],
            "OneXPlayer3" => ["ONEXPLAYER 3"],
            "OneXPlayerG1Intel" => ["ONEXPLAYER G1 i"], "OneXPlayerG1AMD" => ["ONEXPLAYER G1 A"],
            "AOKZOEA1" => ["AOKZOE A1 AR07"], "AOKZOEA1Pro" => ["AOKZOE A1 Pro"],
            "AOKZOEA1X" => ["AOKZOE A1X"], "AOKZOEA2" => ["AOKZOE A2 Pro"], _ => []
        };
    }

    private static IReadOnlyList<KeyboardOemChord> Chords(string source, OneXProtocolProfile protocol)
    {
        if (source.StartsWith("OneXPlayer2", StringComparison.Ordinal) ||
            source.StartsWith("OneXPlayerG1", StringComparison.Ordinal))
        {
            return [FrontChords[0]];
        }

        if (protocol.Apex)
        {
            return
            [
                FrontChords[0], new KeyboardOemChord("keyboard", "Keyboard", [0xA2, 0x5B, 0x4F], CanonicalButtons.None),
                new KeyboardOemChord("home", "Orange", [0x5B, 0x47], CanonicalButtons.Guide),
                new KeyboardOemChord("home-long", "Orange (hold)", [0x5B, 0x44], CanonicalButtons.None),
                new KeyboardOemChord("home-keyboard", "Orange + Keyboard", [0xA3, 0xA5, 0x2E], CanonicalButtons.None),
                new KeyboardOemChord("home-turbo", "Orange + Turbo", [0x5B, 0x2C], CanonicalButtons.None)
            ];
        }

        if (protocol.X2)
        {
            return
            [
                FrontChords[0], new KeyboardOemChord("keyboard", "Keyboard", [0xA2, 0x5B, 0x4F], CanonicalButtons.None),
                new KeyboardOemChord("l4", "M1", [0x7E], CanonicalButtons.RearPaddle1),
                new KeyboardOemChord("r4", "M2", [0x7F], CanonicalButtons.RearPaddle2)
            ];
        }

        if (protocol.VendorButtons)
        {
            return
            [
                new KeyboardOemChord("turbo", "Turbo", [0xA3, 0x5B, 0xA4], CanonicalButtons.QuickAccess), FrontChords[1]
            ];
        }

        return FrontChords;
    }

    private static OemControlDescriptor Oem(string id, string label, OemControlPlacement placement)
    {
        return new OemControlDescriptor
        {
            ControlId = id, Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = label },
            Placement = placement, RequiresControllerAcquisition = true
        };
    }
}

internal enum OneXEcKind
{
    Acpi,
    Indexed,
    Wmi
}

internal enum OneXLightingKind
{
    None,
    Serial,
    HidV1,
    HidV1G1,
    HidV1X2,
    HidV2,
    HidV1Secondary
}

internal enum OneXChargeKind
{
    None,
    Amd,
    Intel
}

internal sealed record OneXProtocolProfile(
    OneXEcKind Ec,
    ushort FanMode,
    ushort FanDuty,
    byte MinimumDuty,
    byte MaximumDuty,
    OneXLightingKind Lighting,
    ushort? ExtraTakeover = null,
    byte ExtraValue = 1,
    bool VendorButtons = false,
    bool SerialButtons = false,
    bool X2 = false,
    bool Mini = false,
    bool Three = false,
    bool Apex = false,
    bool Temperature = false,
    OneXChargeKind Charge = OneXChargeKind.None,
    bool Vibration = false,
    bool HasRearButtons = true,
    bool SimpleVibration = false);
