using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Windows;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Generic;

/// <summary>Exact remaining HC selectors and HHD-only Windows equivalents.</summary>
internal static class GenericModels
{
    internal const string FamilyId = "generic";
    private static readonly StandardControllerProfile Xbox = new([new ControllerUsbId(0x045E, 0x028E)]);

    private static readonly StandardMotionProfile LokiMotion = new(MotionAxisMap.FromInputToOutput(1, 2, 0, 1, -1, -1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, 1, -1));

    private static readonly StandardMotionProfile HhdLokiMotion =
        new(new MotionAxisMap(0, 2, 1, 1, -1, 1), new MotionAxisMap(0, 2, 1, 1, -1, 1));

    private static readonly StandardMotionProfile HhdDefaultMotion =
        new(new MotionAxisMap(1, 2, 0, -1, -1, 1), new MotionAxisMap(1, 2, 0, -1, -1, 1));

    private static readonly KeyboardOemChord[] LokiChords =
        [new("lcc", "LCC", [0xA2, 0xA0, 0xA4, 0x54], CanonicalButtons.QuickAccess)];

    internal static IReadOnlyList<ProfiledDeviceModel> All { get; } = Array.AsReadOnly<ProfiledDeviceModel>(
    [
        Loki("ayn-loki-mini-pro", "AYN Loki Mini Pro", "LokiMiniPro", "Loki MiniPro", 5, 15),
        Loki("ayn-loki-zero", "AYN Loki Zero", "LokiZero", "Loki Zero", 5, 15),
        Loki("ayn-loki-max-6600u", "AYN Loki Max (6600U)", "LokiMax6600U", "Loki Max", 5, 28) with
        {
            Processors = ["AMD Ryzen 5 6600U with Radeon Graphics"]
        },
        Loki("ayn-loki-max-6800u", "AYN Loki Max (6800U)", "LokiMax6800U", "Loki Max", 5, 28) with
        {
            Processors = ["AMD Ryzen 7 6800U with Radeon Graphics"]
        },
        Model("minisforum-v3", "MINISFORUM V3", "MinisforumV3", ["SHENZHEN MEIGAO ELECTRONIC EQUIPMENT CO.,LTD"],
                ["HPPAC"], 3, 28) with
            {
                SystemProducts = ["V3"],
                // HC MinisforumV3 profiles; its OEM mode is not a Windows control implemented here.
                PowerPresets =
                [
                    new DevicePowerPreset("battery", "Battery", 15, 15, DevicePowerMode.BetterBattery)
                        { FastWatts = 15 },
                    new DevicePowerPreset("balanced", "Balanced", 22, 22, DevicePowerMode.Balanced)
                        { FastWatts = 22 },
                    new DevicePowerPreset("performance", "Performance", 28, 28, DevicePowerMode.BestPerformance)
                        { FastWatts = 28 }
                ],
                Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1),
                    new MotionAxisMap(0, 2, 1, -1, -1, 1))
            },
        Hhd("tectoy-zeenix-pro", "TecToy Zeenix Pro", "hhd:generic:Zeenix Pro", "Zeenix Pro", 8, 25) with
        {
            Definition = Definition("tectoy-zeenix-pro", "TecToy Zeenix Pro", controller: true),
            Controller = Xbox, Motion = HhdLokiMotion, KeyboardChords = LokiChords
        },
        Hhd("anbernic-win600", "Anbernic Win600", "hhd:generic:Win600", "Win600", 10, 25) with
        {
            Definition = Definition("anbernic-win600", "Anbernic Win600", controller: true),
            Controller = Xbox, Motion = HhdDefaultMotion,
            KeyboardChords = [new KeyboardOemChord("guide", "Guide", [0x5B, 0x47], CanonicalButtons.Guide)]
        },
        Hhd("tecno-pocket-go", "TECNO Pocket Go", "hhd:generic:Pocket Go", "Pocket Go", 10, 25) with
        {
            Definition = Definition("tecno-pocket-go", "TECNO Pocket Go", controller: true),
            Controller = new StandardControllerProfile([new ControllerUsbId(0x2993, 0x2001)]),
            CreateOemSource = () => new TecnoButtonSource(), NativeOemReplacesChords = [],
            OemControls = TecnoButtonSource.Controls,
            KeyboardChords =
            [new KeyboardOemChord("center", "Center", [0xA0, 0xA4, 0xA2, 0x70], CanonicalButtons.QuickAccess)]
        },
        Hhd("orangepi-neo", "OrangePi NEO", "hhd:orangePi:NEO-01", "NEO-01", 8, 25) with
        {
            Definition = Definition("orangepi-neo", "OrangePi NEO", controller: true),
            Controller =
            new StandardControllerProfile([new ControllerUsbId(0x045E, 0x028E), new ControllerUsbId(0x0079, 0x181C)]),
            Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, -1, 1),
                new MotionAxisMap(0, 2, 1, 1, -1, 1)),
            KeyboardChords =
            [
                new KeyboardOemChord("guide", "Guide", [0x7F], CanonicalButtons.Guide),
                new KeyboardOemChord("quick-access", "Quick access", [0x7E], CanonicalButtons.QuickAccess),
                new KeyboardOemChord("left-rear", "Left rear", [0x80], CanonicalButtons.RearPaddle1),
                new KeyboardOemChord("right-rear", "Right rear", [0x81], CanonicalButtons.RearPaddle2)
            ]
        },
        Model("asus-flow-z13-gz302", "ASUS ROG Flow Z13 GZ302", "hhd:power:GZ302", ["ASUSTeK COMPUTER INC."], ["GZ302"],
                40, 65) with
            {
                ServiceFactories = [context => new GenericAsusService(context)],
                NativeRoles =
                [
                    CapabilityRole.ScenarioMode, CapabilityRole.ChargeLimit, CapabilityRole.FanDuty,
                    CapabilityRole.FanMode, CapabilityRole.GenericToggle, CapabilityRole.GenericChoice,
                    CapabilityRole.LightingPower, CapabilityRole.FanCurve, CapabilityRole.LightingBrightness,
                    CapabilityRole.LightingEffect, CapabilityRole.LightingEffectSpeed, CapabilityRole.LightingZoneColor
                ]
            },
        Model("ayaneo-flip-power", "AYANEO FLIP power controls", "hhd:power:FLIP", ["AYANEO"], ["FLIP"], 8, 25),
        Hhd("gpd-g1617-02-l", "GPD G1617-02-L", "hhd:power:G1617-02-L", "G1617-02-L", 8, 25),
        Hhd("gpd-g1622-01-l", "GPD G1622-01-L", "hhd:power:G1622-01-L", "G1622-01-L", 8, 25),
        Hhd("gpd-g1622-01", "GPD G1622-01", "hhd:power:G1622-01", "G1622-01", 8, 25),
        Hhd("gpd-g1628-04-l", "GPD G1628-04-L", "hhd:power:G1628-04-L", "G1628-04-L", 8, 25),
        Hhd("gpd-g1628-04", "GPD G1628-04", "hhd:power:G1628-04", "G1628-04", 8, 25)
    ]);

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        foreach (var model in All)
        {
            if (model.Manufacturers.Count != 0
                    ? model.Matches(identity)
                    : model.Products.Any(product => ProfiledDeviceModel.Equal(product, identity.SystemProduct)))
            {
                return model;
            }
        }

        return null;
    }

    private static ProfiledDeviceModel Loki(string id, string name, string source, string product, int minimum,
        int maximum)
    {
        return Model(id, name, source, ["AYN"], [product], minimum, maximum) with
        {
            Definition = Definition(id, name, controller: true), Controller = Xbox, Motion = LokiMotion,
            KeyboardChords = LokiChords,
            CreateOemSource = () => new OemMouseChordSource(), NativeOemReplacesChords = [],
            OemControls =
            [
                CapabilityDescriptors.OemControl("guide-mouse", "Guide", OemControlPlacement.Front)
            ],
            ServiceFactories = [context => new AynNativeService(context)],
            NativeRoles = [CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.LightingZoneColor]
        };
    }

    private static ProfiledDeviceModel Hhd(string id, string name, string source, string product, int minimum,
        int maximum)
    {
        return Model(id, name, source, [], [product], minimum, maximum) with
        {
            IdentitySource = ProfileIdentitySource.System
        };
    }

    private static ProfiledDeviceModel Model(string id, string name, string source, string[] manufacturers,
        string[] products,
        int minimum, int maximum, CpuPowerKind cpu = CpuPowerKind.Amd)
    {
        return new ProfiledDeviceModel
        {
            Definition = Definition(id, name), SourceFamilyId = source, Manufacturers = manufacturers,
            Products = products,
            MinimumWatts = minimum, MaximumWatts = maximum, Cpu = cpu
        };
    }

    private static HandheldDefinition Definition(string id, string name, bool controller = false)
    {
        return new HandheldDefinition(id, id.Split('-')[0] == "asus" ? "asus-flow" : id.Split('-')[0], name, id)
        {
            HasController = controller,
            HardwareAccess = DeviceHardwareAccess.PawnIo
        };
    }
}
