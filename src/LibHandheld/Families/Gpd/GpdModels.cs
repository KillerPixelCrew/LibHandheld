using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Gpd;

/// <summary>HC's exact SMBIOS and CPU selectors, with inherited protocol facts expanded into each row.</summary>
internal static class GpdModels
{
    internal const string FamilyId = "gpd";
    private const string Cpu6800 = "AMD Ryzen 7 6800U with Radeon Graphics";
    private const string Cpu7640 = "AMD Ryzen 5 7640U w/ Radeon 760M Graphics";
    private const string Cpu7840 = "AMD Ryzen 7 7840U w/ Radeon 780M Graphics";
    private const string Cpu8640 = "AMD Ryzen 5 8640U w/ Radeon 760M Graphics";
    private const string Cpu8840 = "AMD Ryzen 7 8840U w/ Radeon 780M Graphics";
    private const string CpuHx370 = "AMD Ryzen AI 9 HX 370 w/ Radeon 890M";

    private static readonly EcFanProfile Win4Fan = new(0x2E, 0x2F, [0xC311], 127,
        UnlockChipAddress: 0x2000, UnlockAddress: 0x1060, AutomaticDutyAddress: 0xC880);

    private static readonly EcFanProfile MaxFan = new(0x4E, 0x4F, [0x1809], 184, 0x275);
    private static readonly EcFanProfile MiniFan = new(0x4E, 0x4F, [0x047A], 244, ReadDuty: false);

    private static readonly EcFanProfile Win5Fan = new(0x4E, 0x4F, [0x047A, 0x047B], 244,
        RpmHighAddress: 0x0478, RpmLowAddress: 0x0479);

    private static readonly StandardMotionProfile Win4Motion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1));

    private static readonly StandardMotionProfile ModernMotion = new(MotionAxisMap.FromInputToOutput(1, 2, 0, 1, -1, -1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, 1));

    private static readonly StandardMotionProfile MaxMotion = new(MotionAxisMap.FromInputToOutput(1, 2, 0, 1, -1, -1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, 1, 1));

    private static readonly StandardMotionProfile MiniMotion = new(MotionAxisMap.FromInputToOutput(2, 0, 1, 1, -1, -1),
        MotionAxisMap.FromInputToOutput(2, 0, 1, 1, 1, 1));

    private static readonly StandardMotionProfile MiniHxMotion = new(MotionAxisMap.FromInputToOutput(1, 2, 0, -1, -1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, 1, 1));

    private static readonly StandardMotionProfile Mini8840Motion =
        new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1), MotionAxisMap.FromInputToOutput(0, 2, 1, -1, 1, 1));

    private static readonly StandardMotionProfile MaxIntelMotion =
        new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, 1, 1), MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, 1));

    private static readonly StandardMotionProfile MaxHxMotion = new(MotionAxisMap.FromInputToOutput(0, 2, 1, 1, -1, 1),
        MotionAxisMap.FromInputToOutput(0, 2, 1, -1, -1, 1));

    private static readonly KeyboardOemChord[] RearChords =
    [
        new("l4", "L4", [0x7A, 0x4C], CanonicalButtons.RearPaddle1),
        new("r4", "R4", [0x7B, 0x52], CanonicalButtons.RearPaddle2)
    ];

    // GPD's physical controller firmware enumerates as Xbox 360; exact SMBIOS gates the family,
    // capabilities-ex identifies the slot, and USB location joins its HID/composite nodes.
    private static readonly StandardControllerProfile Controller =
        new([
            new ControllerUsbId(0x045E, 0x028E), new ControllerUsbId(0x045E, 0x002D),
            new ControllerUsbId(0x2F24, 0x0135), new ControllerUsbId(0x2F24, 0x0137)
        ])
        {
            CompanionUsbIds =
            [
                new ControllerUsbId(0x2F24, 0x0135), new ControllerUsbId(0x2F24, 0x0137),
                new ControllerUsbId(0x045E, 0x002D)
            ]
        };

    private static readonly StandardTouchProfile Touchpad = new([
        new ControllerUsbId(0x093A, 0x0255), new ControllerUsbId(0x0911, 0x5288)
    ]);

    internal static IReadOnlyList<ProfiledDeviceModel> All { get; } = Array.AsReadOnly<ProfiledDeviceModel>(
    [
        Model("gpd-win2", "GPD WIN 2", "GPDWin2", ["WIN2"], [], 5, 15, null, null, CpuPowerKind.Intel, []),
        Model("gpd-win3", "GPD WIN 3", "GPDWin3", ["G1618-03"], [], 7, 25, null, null, CpuPowerKind.Intel),
        Model("gpd-win4-6800u", "GPD WIN 4 (6800U)", "GPDWin4", ["G1618-04"], [Cpu6800], 5, 28, Win4Fan, Win4Motion),
        Model("gpd-win4-7640u", "GPD WIN 4 (7640U)", "GPDWin4_2023_7640U", ["G1618-04"], [Cpu7640], 5, 30, MaxFan,
            ModernMotion),
        Model("gpd-win4-7840u", "GPD WIN 4 (7840U)", "GPDWin4_2023", ["G1618-04"], [Cpu7840], 5, 30, MaxFan,
            ModernMotion),
        Model("gpd-win4-8640u", "GPD WIN 4 (8640U)", "GPDWin4_2024_8640U", ["G1618-04"], [Cpu8640], 5, 30, MaxFan,
            ModernMotion),
        Model("gpd-win4-8840u", "GPD WIN 4 (8840U)", "GPDWin4_2024", ["G1618-04"], [Cpu8840], 5, 30, MaxFan,
            ModernMotion),
        Model("gpd-win4-hx370", "GPD WIN 4 (HX 370)", "GPDWin4_2024_HX370", ["G1618-04"], [CpuHx370], 5, 30, MaxFan,
            ModernMotion),
        Model("gpd-win-mini-7640u", "GPD WIN Mini (7640U)", "GPDWinMini_7640U", ["G1617-01"], [Cpu7640], 5, 18, MiniFan,
            MiniMotion),
        Model("gpd-win-mini-7840u", "GPD WIN Mini (7840U)", "GPDWinMini", ["G1617-01"], [Cpu7840], 5, 18, MiniFan,
            MiniMotion),
        Model("gpd-win-mini-8840u", "GPD WIN Mini (8840U)", "GPDWinMini_8840U", ["G1617-01"], [Cpu8840], 5, 18, MiniFan,
            Mini8840Motion),
        Model("gpd-win-mini-hx370", "GPD WIN Mini (HX 370)", "GPDWinMini_HX370", ["G1617-02"], [], 3, 28, MiniFan,
            MiniHxMotion),
        Model("gpd-win-max2-intel", "GPD WIN Max 2 (Intel)", "GPDWinMax2Intel", ["G1619-03"], [], 15, 28, MaxFan,
            MaxIntelMotion, CpuPowerKind.Intel),
        Model("gpd-win-max2-6800u", "GPD WIN Max 2 (6800U)", "GPDWinMax2_2022_6800U", ["G1619-04", "G1619-05"],
            [Cpu6800], 3, 28, MaxFan, MaxMotion),
        Model("gpd-win-max2-7640u", "GPD WIN Max 2 (7640U)", "GPDWinMax2_2023_7640U", ["G1619-04", "G1619-05"],
            [Cpu7640], 3, 28, MaxFan, MaxMotion),
        Model("gpd-win-max2-7840u", "GPD WIN Max 2 (7840U)", "GPDWinMax2_2023_7840U", ["G1619-04", "G1619-05"],
            [Cpu7840], 3, 28, MaxFan, MaxMotion),
        Model("gpd-win-max2-8640u", "GPD WIN Max 2 (8640U)", "GPDWinMax2_2024_8640U", ["G1619-04", "G1619-05"],
            [Cpu8640], 3, 28, MaxFan, MaxMotion),
        Model("gpd-win-max2-8840u", "GPD WIN Max 2 (8840U)", "GPDWinMax2_2024_8840U", ["G1619-04", "G1619-05"],
            [Cpu8840], 3, 28, MaxFan, MaxMotion),
        Model("gpd-win-max2-hx370", "GPD WIN Max 2 (HX 370)", "GPDWinMax2_2024_HX370", ["G1619-04", "G1619-05"],
            [CpuHx370], 3, 28, MaxFan, MaxHxMotion),
        Model("gpd-win5", "GPD WIN 5", "GPDWin5", ["G1618-05"], [], 8, 85, Win5Fan, ModernMotion) with
        {
            CreateOemSource = () => new GpdWin5OemSource(),
            NativeRoles =
            [
                CapabilityRole.GenericChoice, CapabilityRole.GenericRange, CapabilityRole.LightingEffect,
                CapabilityRole.LightingZoneColor, CapabilityRole.GenericReadOnly
            ],
            ServiceFactories = [_ => new GpdNativeService(), _ => new GpdWin5PowerSourceService()],
            KeyboardChords =
            [
                new KeyboardOemChord("keyboard", "Keyboard", [0xA2, 0x5B, 0x4F], CanonicalButtons.None),
                new KeyboardOemChord("home", "Home", [0x5B, 0x44], CanonicalButtons.None),
                new KeyboardOemChord("l4", "L4", [0xA2, 0xA0, 0x7D], CanonicalButtons.RearPaddle1),
                new KeyboardOemChord("r4", "R4", [0x72, 0x7E], CanonicalButtons.RearPaddle2),
                new KeyboardOemChord("gamepad", "Gamepad", [0x7C], CanonicalButtons.QuickAccess)
            ]
        }
    ]);

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return Array.AsReadOnly(All.Select(model => model.DeclaredDefinition).ToArray());
    }

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        if (All.FirstOrDefault(model => model.Matches(identity)) is { } matched)
        {
            return matched;
        }

        // HC defaults these exact WIN Max 2 boards to the 8840U variant when the brand string
        // does not select another known variant. Other boards still require their own exact row.
        var max2 = All.Single(model => model.Definition.Id == "gpd-win-max2-8840u");
        return (max2 with { Processors = [] }).Matches(identity) ? max2 : null;
    }

    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(All.Single(model => model.Definition.Id == definition.Id));
    }

    private static ProfiledDeviceModel Model(string id, string name, string source, string[] products,
        string[] processors,
        int minimum, int maximum, EcFanProfile? fan, StandardMotionProfile? motion,
        CpuPowerKind cpu = CpuPowerKind.Amd, IReadOnlyList<KeyboardOemChord>? chords = null)
    {
        return new ProfiledDeviceModel
        {
            Definition = new HandheldDefinition(id, FamilyId, name, id)
            {
                HardwareAccess = DeviceHardwareAccess.PawnIo |
                                 (cpu == CpuPowerKind.Intel ? DeviceHardwareAccess.IntelKx : DeviceHardwareAccess.None)
            },
            SourceFamilyId = source, Manufacturers = ["GPD"], Products = products, Processors = processors,
            Cpu = cpu, MinimumWatts = minimum, MaximumWatts = maximum, Fan = fan, Motion = motion,
            Controller = source is "GPDWin2" or "GPDWin3" ? Controller with { CompanionUsbIds = [] } : Controller,
            KeyboardChords = chords ?? (source.StartsWith("GPDWinMax2", StringComparison.Ordinal)
                ? [new KeyboardOemChord("menu", "Menu", [0x07], CanonicalButtons.QuickAccess), .. RearChords]
                : RearChords),
            Touchpad = source.StartsWith("GPDWinMini", StringComparison.Ordinal) ||
                       source.StartsWith("GPDWinMax2", StringComparison.Ordinal)
                ? Touchpad
                : null,
            NativeRoles = source is "GPDWin2" or "GPDWin3"
                ? []
                :
                [
                    CapabilityRole.GenericChoice, CapabilityRole.GenericRange, CapabilityRole.LightingEffect,
                    CapabilityRole.LightingZoneColor
                ],
            NativeOemReplacesChords = ["l4", "r4", "gamepad"],
            ServiceFactories = source is "GPDWin2" or "GPDWin3"
                ? []
                : [_ => new GpdNativeService()]
        };
    }
}
