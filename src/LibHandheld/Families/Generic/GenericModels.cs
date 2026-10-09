using LibHandheld.Families.Ayaneo;
using LibHandheld.Families.Gpd;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Generic;

/// <summary>Exact remaining HC selectors and HHD-only Windows equivalents precede the CPU-only fallback.</summary>
internal static class GenericModels
{
    internal const string FamilyId = "generic";
    private static readonly StandardControllerProfile Xbox = new([new ControllerUsbId(0x045E, 0x028E)]);

    private static readonly StandardMotionProfile LokiMotion = new(new MotionAxisMap(1, 2, 0, 1, -1, -1),
        new MotionAxisMap(0, 2, 1, -1, 1, -1));

    private static readonly StandardMotionProfile HhdLokiMotion =
        new(new MotionAxisMap(0, 2, 1, 1, -1, 1), new MotionAxisMap(0, 2, 1, 1, -1, 1));

    private static readonly StandardMotionProfile HhdDefaultMotion =
        new(new MotionAxisMap(1, 2, 0, -1, -1, 1), new MotionAxisMap(1, 2, 0, -1, -1, 1));

    private static readonly KeyboardOemChord[] LokiChords =
        [new("lcc", "LCC", [0xA2, 0xA0, 0xA4, 0x54], CanonicalButtons.QuickAccess)];

    private static readonly HandheldDefinition[] Fallbacks =
    [
        Definition("generic-amd", "Generic AMD CPU controls", CpuPowerKind.Amd),
        Definition("generic-intel", "Generic Intel CPU controls", CpuPowerKind.Intel),
        Definition("asus-amd", "ASUS AMD firmware controls", CpuPowerKind.Amd),
        Definition("asus-intel", "ASUS Intel firmware controls", CpuPowerKind.Intel),
        Definition("hhd-ayn-range", "AYN standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-tectoy-range", "TecToy standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-ayaneo-range", "AYANEO standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-gpd-range", "GPD standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-onexplayer-range", "ONEXPLAYER standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-aokzoe-range", "AOKZOE standard Windows sources", CpuPowerKind.Amd, true),
        Definition("hhd-orangepi-range", "OrangePi standard Windows sources", CpuPowerKind.Amd, true)
    ];

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
                Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1),
                    new MotionAxisMap(0, 2, 1, -1, -1, 1))
            },
        Hhd("tectoy-zeenix-pro", "TecToy Zeenix Pro", "hhd:generic:Zeenix Pro", "Zeenix Pro", 8, 25) with
        {
            Definition = Definition("tectoy-zeenix-pro", "TecToy Zeenix Pro", CpuPowerKind.Amd, true),
            Controller = Xbox, Motion = HhdLokiMotion, KeyboardChords = LokiChords
        },
        Hhd("anbernic-win600", "Anbernic Win600", "hhd:generic:Win600", "Win600", 10, 25) with
        {
            Definition = Definition("anbernic-win600", "Anbernic Win600", CpuPowerKind.Amd, true),
            Controller = Xbox, Motion = HhdDefaultMotion,
            KeyboardChords = [new KeyboardOemChord("guide", "Guide", [0x5B, 0x47], CanonicalButtons.Guide)]
        },
        Hhd("tecno-pocket-go", "TECNO Pocket Go", "hhd:generic:Pocket Go", "Pocket Go", 10, 25) with
        {
            Definition = Definition("tecno-pocket-go", "TECNO Pocket Go", CpuPowerKind.Amd, true),
            Controller = new StandardControllerProfile([new ControllerUsbId(0x2993, 0x2001)]),
            CreateOemSource = () => new TecnoButtonSource(), NativeOemReplacesChords = [],
            OemControls = TecnoButtonSource.Controls,
            KeyboardChords =
            [new KeyboardOemChord("center", "Center", [0xA0, 0xA4, 0xA2, 0x70], CanonicalButtons.QuickAccess)]
        },
        Hhd("orangepi-neo", "OrangePi NEO", "hhd:orangePi:NEO-01", "NEO-01", 8, 25) with
        {
            Definition = Definition("orangepi-neo", "OrangePi NEO", CpuPowerKind.Amd, true),
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
                CreateNativeService = context => new GenericAsusService(context)
            },
        Model("ayaneo-flip-power", "AYANEO FLIP power controls", "hhd:power:FLIP", ["AYANEO"], ["FLIP"], 8, 25),
        Hhd("gpd-g1617-02-l", "GPD G1617-02-L", "hhd:power:G1617-02-L", "G1617-02-L", 8, 25),
        Hhd("gpd-g1622-01-l", "GPD G1622-01-L", "hhd:power:G1622-01-L", "G1622-01-L", 8, 25),
        Hhd("gpd-g1622-01", "GPD G1622-01", "hhd:power:G1622-01", "G1622-01", 8, 25),
        Hhd("gpd-g1628-04-l", "GPD G1628-04-L", "hhd:power:G1628-04-L", "G1628-04-L", 8, 25),
        Hhd("gpd-g1628-04", "GPD G1628-04", "hhd:power:G1628-04", "G1628-04", 8, 25)
    ]);

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return Array.AsReadOnly(All.Select(model => model.Definition)
            .Concat(Fallbacks).ToArray());
    }

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        foreach (var model in All)
        {
            if (model.Manufacturers.Count != 0
                    ? model.Matches(identity)
                    : model.Products.Any(product => Equal(product, identity.SystemProduct)))
            {
                return model.Manufacturers.Count == 0 ? Bind(model, identity, ProfileIdentitySource.System) : model;
            }
        }

        return MatchFallback(identity);
    }

    internal static ProfiledDeviceModel? MatchFallback(DeviceIdentitySnapshot identity)
    {
        if (Cpu(identity.ProcessorName) is not { } cpu)
        {
            return null;
        }

        var vendor = identity.BaseboardManufacturer ?? identity.SystemManufacturer;
        var systemVendor = identity.SystemManufacturer ?? identity.BaseboardManufacturer;
        var id = Equal(vendor, "ASUSTeK COMPUTER INC.") ? cpu == CpuPowerKind.Amd ? "asus-amd" : "asus-intel"
            : cpu == CpuPowerKind.Amd ? "generic-amd" : "generic-intel";
        if (cpu == CpuPowerKind.Amd)
        {
            if (Equal(systemVendor, "AYN"))
            {
                id = "hhd-ayn-range";
            }
            else if (Equal(systemVendor, "TecToy"))
            {
                id = "hhd-tectoy-range";
            }
            else if (vendor?.Contains("AYANEO", StringComparison.OrdinalIgnoreCase) == true
                     && identity.SystemProduct?.Contains("AYANEO 3", StringComparison.OrdinalIgnoreCase) != true)
            {
                id = "hhd-ayaneo-range";
            }
            else if (Equal(systemVendor, "GPD"))
            {
                id = "hhd-gpd-range";
            }
            else if (identity.SystemProduct?.Contains("ONEXPLAYER", StringComparison.OrdinalIgnoreCase) == true)
            {
                id = "hhd-onexplayer-range";
            }
            else if (identity.SystemProduct?.Contains("AOKZOE", StringComparison.OrdinalIgnoreCase) == true)
            {
                id = "hhd-aokzoe-range";
            }
            else if (systemVendor?.Contains("OrangePi", StringComparison.OrdinalIgnoreCase) == true)
            {
                id = "hhd-orangepi-range";
            }
        }

        var definition = Fallbacks.Single(item => item.Id == id);
        var source = id.StartsWith("hhd-", StringComparison.Ordinal)
            ? ProfileIdentitySource.System
            : ProfileIdentitySource.Baseboard;
        var profile = Model(id, definition.Name,
                id.StartsWith("asus-", StringComparison.Ordinal) ? "ASUS" : "DefaultDevice", [], [], 10, 25, cpu)
            with
            {
                Definition = definition
            };
        if (id.StartsWith("asus-", StringComparison.Ordinal))
        {
            profile = profile with { CreateNativeService = context => new GenericAsusService(context) };
        }

        if (definition.HasController)
        {
            profile = profile with { Controller = Xbox, Motion = HhdDefaultMotion };
        }

        if (id is "hhd-ayn-range" or "hhd-tectoy-range")
        {
            profile = profile with { Motion = HhdLokiMotion, KeyboardChords = LokiChords };
        }

        if (id == "hhd-gpd-range")
        {
            profile = profile with
            {
                Controller = new StandardControllerProfile([
                    new ControllerUsbId(0x045E, 0x028E), new ControllerUsbId(0x045E, 0x002D)
                ])
                {
                    CompanionUsbIds =
                    [
                        new ControllerUsbId(0x2F24, 0x0135), new ControllerUsbId(0x2F24, 0x0137),
                        new ControllerUsbId(0x045E, 0x002D)
                    ]
                },
                Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, -1, 1),
                    new MotionAxisMap(0, 2, 1, 1, -1, 1)),
                Touchpad = new StandardTouchProfile([
                    new ControllerUsbId(0x093A, 0x0255), new ControllerUsbId(0x0911, 0x5288)
                ]),
                KeyboardChords =
                [
                    new KeyboardOemChord("l4", "L4", [0x7A, 0x4C], CanonicalButtons.RearPaddle1),
                    new KeyboardOemChord("r4", "R4", [0x7B, 0x52], CanonicalButtons.RearPaddle2),
                    new KeyboardOemChord("quick-access", "Quick access", [0x7C], CanonicalButtons.QuickAccess)
                ],
                CreateNativeService = context => new GpdNativeService(context)
            };
        }

        if (id == "hhd-ayaneo-range")
        {
            profile = profile with
            {
                KeyboardChords =
                [
                    new KeyboardOemChord("guide", "Guide", [0xA2, 0x5B, 0x80], CanonicalButtons.Guide),
                    new KeyboardOemChord("quick-access", "Quick access", [0xA2, 0x5B, 0x81],
                        CanonicalButtons.QuickAccess),
                    new KeyboardOemChord("left-rear", "Left rear", [0xA2, 0x5B, 0x7E], CanonicalButtons.RearPaddle1),
                    new KeyboardOemChord("right-rear", "Right rear", [0xA2, 0x5B, 0x7F], CanonicalButtons.RearPaddle2),
                    new KeyboardOemChord("next-guide", "Guide", [0xA2, 0x5B, 0x7B], CanonicalButtons.Guide)
                ]
            };
        }

        if (id is "hhd-onexplayer-range" or "hhd-aokzoe-range")
        {
            profile = profile with
            {
                KeyboardChords =
                [
                    new KeyboardOemChord("guide", "Guide", [0x5B, 0x44], CanonicalButtons.Guide),
                    new KeyboardOemChord("quick-access", "Quick access", [0xA3, 0x5B, 0x4F],
                        CanonicalButtons.QuickAccess)
                ]
            };
        }

        if (id == "hhd-orangepi-range")
        {
            profile = profile with
            {
                Controller = new StandardControllerProfile([
                    new ControllerUsbId(0x045E, 0x028E), new ControllerUsbId(0x0079, 0x181C)
                ]),
                Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, -1, 1),
                    new MotionAxisMap(0, 2, 1, 1, -1, 1)),
                KeyboardChords =
                [
                    new KeyboardOemChord("guide", "Guide", [0x7F], CanonicalButtons.Guide),
                    new KeyboardOemChord("quick-access", "Quick access", [0x7E], CanonicalButtons.QuickAccess),
                    new KeyboardOemChord("left-rear", "Left rear", [0x80], CanonicalButtons.RearPaddle1),
                    new KeyboardOemChord("right-rear", "Right rear", [0x81], CanonicalButtons.RearPaddle2)
                ]
            };
        }

        return Bind(profile, identity, source);
    }

    internal static IDeviceEngine CreateEngine(HandheldDefinition definition)
    {
        return new GenericEngine(definition);
    }

    private static ProfiledDeviceModel Loki(string id, string name, string source, string product, int minimum,
        int maximum)
    {
        return Model(id, name, source, ["AYN"], [product], minimum, maximum) with
        {
            Definition = Definition(id, name, CpuPowerKind.Amd, true), Controller = Xbox, Motion = LokiMotion,
            KeyboardChords = LokiChords,
            CreateOemSource = () => new AyaneoKunMouseSource(), NativeOemReplacesChords = [],
            OemControls =
            [
                new OemControlDescriptor
                {
                    ControlId = "guide-mouse",
                    Display =
                        new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Guide" },
                    Placement = OemControlPlacement.Front, RequiresControllerAcquisition = true
                }
            ],
            CreateNativeService = context => new AynNativeService(context)
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
            Definition = Definition(id, name, cpu), SourceFamilyId = source, Manufacturers = manufacturers,
            Products = products,
            MinimumWatts = minimum, MaximumWatts = maximum, Cpu = cpu
        };
    }

    private static HandheldDefinition Definition(string id, string name, CpuPowerKind cpu, bool controller = false)
    {
        return new HandheldDefinition(id, FamilyId, name, "generic")
        {
            HasController = controller,
            HardwareAccess = DeviceHardwareAccess.PawnIo |
                             (cpu == CpuPowerKind.Intel ? DeviceHardwareAccess.IntelKx : DeviceHardwareAccess.None)
        };
    }

    private static ProfiledDeviceModel Bind(ProfiledDeviceModel model, DeviceIdentitySnapshot identity,
        ProfileIdentitySource source)
    {
        var manufacturer = source == ProfileIdentitySource.System
            ? identity.SystemManufacturer ?? identity.BaseboardManufacturer
            : identity.BaseboardManufacturer ?? identity.SystemManufacturer;
        var product = source == ProfileIdentitySource.System
            ? identity.SystemProduct ?? identity.BaseboardProduct
            : identity.BaseboardProduct ?? identity.SystemProduct;
        return model with
        {
            Manufacturers = [manufacturer ?? ""], Products = [product ?? ""],
            Processors = [identity.ProcessorName ?? ""], IdentitySource = source
        };
    }

    private static bool Equal(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CpuPowerKind? Cpu(string? name)
    {
        return name?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true
               && (name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Athlon", StringComparison.OrdinalIgnoreCase)) ? CpuPowerKind.Amd
            : name?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true &&
              name.Contains("Core", StringComparison.OrdinalIgnoreCase) ? CpuPowerKind.Intel : null;
    }

    private sealed class GenericEngine(HandheldDefinition definition) : ProfiledEngineSelector(definition)
    {
        protected override ProfiledDeviceModel? Select(DeviceIdentitySnapshot identity)
        {
            return Match(identity);
        }
    }
}
