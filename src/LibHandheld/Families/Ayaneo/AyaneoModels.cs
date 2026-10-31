using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Windows;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Ayaneo;

internal enum AyaneoEcProtocol
{
    None,
    CEc,
    CEii
}

internal enum AyaneoLighting
{
    None,
    CEc,
    CEii,
    Kun,
    SuperJoy
}

/// <summary>Variants are immutable data; the two EC codecs and one HID codec own the native mechanics.</summary>
internal sealed record AyaneoModel(
    ProfiledDeviceModel Profile,
    AyaneoEcProtocol Protocol,
    AyaneoLighting Lighting,
    bool RgbConfirmation = true,
    bool SecondaryDisplay = false,
    bool ModularController = false);

internal static class AyaneoModels
{
    internal const string FamilyId = "ayaneo";
    private static readonly string[] Manufacturers = ["AYADEVICE", "AYANEO", "AYA"];
    private static readonly StandardControllerProfile Controller = new([new ControllerUsbId(0x045E, 0x028E)]);

    private static readonly StandardControllerProfile ModularController = Controller with
    {
        HidFallback = new HidControllerProfile([new ControllerUsbId(0x1C4F, 0x0002)],
        [
            CanonicalButtons.X, CanonicalButtons.A, CanonicalButtons.B, CanonicalButtons.Y,
            CanonicalButtons.LeftShoulder, CanonicalButtons.RightShoulder, CanonicalButtons.None,
            CanonicalButtons.None, CanonicalButtons.View, CanonicalButtons.Menu,
            CanonicalButtons.LeftStick, CanonicalButtons.RightStick
        ])
    };

    private static readonly StandardMotionProfile CecMotion = Motion(1, -1, 1, -1, -1, 1);
    private static readonly StandardMotionProfile CeiiMotion = Motion(1, 1, -1, 1, 1, -1);
    private static readonly StandardMotionProfile AirMotion = Motion(1, -1, 1, 1, -1, -1);
    private static readonly StandardMotionProfile FlipMotion = Motion(1, 1, 1, 1, -1, -1);

    // HC a250b19: IDevice.GetCurrent + Devices/AYANEO and inherited resource matrices.
    // HHD a87fb: generic/const.py supplies only the extra exact "AYA NEO 2021" and "AYANEO KUN" aliases.
    internal static IReadOnlyList<AyaneoModel> All { get; } = Array.AsReadOnly<AyaneoModel>(
    [
        Model("2021", "AYANEO 2021", ["AYA NEO FOUNDER", "AYA NEO 2021", "AYANEO 2021"], 3, 25,
            AyaneoEcProtocol.None, chords: Legacy2021()),
        Model("2021-pro", "AYANEO 2021 Pro", ["AYANEO 2021 Pro", "AYANEO 2021 Pro Retro Power"], 3, 25,
            AyaneoEcProtocol.None, chords: Legacy2021()),
        Model("next", "AYANEO NEXT", ["NEXT", "NEXT Pro", "NEXT Advance"], 10, 25,
            AyaneoEcProtocol.None, chords: Next()),
        Model("next-lite-4500u", "AYANEO NEXT Lite 4500U", ["NEXT Lite"], 3, 25,
            AyaneoEcProtocol.None, processors: ["AMD Ryzen 5 4500U with Radeon Graphics"], chords: Next()),
        Model("next-lite", "AYANEO NEXT Lite 4800U", ["NEXT Lite"], 3, 25,
            AyaneoEcProtocol.None, processors: ["AMD Ryzen 7 4800U with Radeon Graphics"], chords: Next()),
        Model("air", "AYANEO AIR", ["AIR"], 3, 15, motion: AirMotion, chords: Air()),
        Model("air-lite", "AYANEO AIR Lite", ["AIR Lite"], 3, 12, motion: AirMotion, chords: Air()),
        Model("air-pro", "AYANEO AIR Pro", ["AIR Pro"], 3, 18, motion: AirMotion, chords: Air()),
        Model("air-1s", "AYANEO AIR 1S", ["AIR 1S", "AIR 1S Limited"], 4, 28, motion: AirMotion),
        Model("air-pro-1s", "AYANEO AIR Pro 1S", ["AIR Pro 1S", "AYANEO AIR Pro 1S"], 4, 28, motion: AirMotion),
        Model("air-2", "AYANEO AIR 2", ["AIR 2", "AIR2", "AYANEO AIR 2", "AB10"], 5, 30,
            fan: new EcFanProfile(0x4E, 0x4F, [0x1809], 0xB8, 0x02F1), confirmation: false),
        Model("2", "AYANEO 2 / GEEK", ["AYANEO 2", "GEEK"], 3, 33, motion: Motion(1, 1, 1, 1, 1, 1)),
        Model("2s", "AYANEO 2S / GEEK 1S", ["AYANEO 2S", "GEEK 1S"], 3, 30, motion: Motion(1, 1, 1, 1, 1, 1)),
        Model("air-plus-amd", "AYANEO AIR Plus AMD", ["AB05-AMD"], 3, 33, AyaneoEcProtocol.CEii),
        Model("air-plus-mendocino", "AYANEO AIR Plus Mendocino", ["AB05-Mendocino"], 5, 15,
            AyaneoEcProtocol.CEii, fan: CeiiFan(0x1802)),
        Model("air-plus-intel", "AYANEO AIR Plus Intel", ["AB05-Intel"], 5, 55,
            AyaneoEcProtocol.CEii, cpu: CpuPowerKind.Intel, fan: CeiiFan(0x1802)),
        Model("air-plus-nxx", "AYANEO AIR Plus Nxx", ["AB05N", "AB05NXX", "AIR Plus Nxx"], 5, 15,
            AyaneoEcProtocol.CEii, cpu: CpuPowerKind.Intel, fan: CeiiFan(0x1802)),
        // HHD's AIR Plus selector omits the CPU distinction. Exact CPU brands select the HC codec/range.
        Model("air-plus-amd-hhd", "AYANEO AIR Plus AMD", ["AIR Plus"], 3, 33,
            AyaneoEcProtocol.CEii, processors: ["AMD Ryzen 7 6800U with Radeon Graphics"]),
        Model("air-plus-mendocino-hhd", "AYANEO AIR Plus Mendocino", ["AIR Plus"], 5, 15,
            AyaneoEcProtocol.CEii, processors: ["AMD Ryzen 3 7320U with Radeon Graphics"], fan: CeiiFan(0x1802)),
        Model("air-plus-intel-hhd", "AYANEO AIR Plus Intel", ["AIR Plus"], 5, 55,
            AyaneoEcProtocol.CEii, processors: ["12th Gen Intel(R) Core(TM) i3-1215U"],
            cpu: CpuPowerKind.Intel, fan: CeiiFan(0x1802)),
        Model("slide", "AYANEO SLIDE", ["AS01", "SLIDE"], 3, 28, AyaneoEcProtocol.CEii,
            motion: Motion(1, 1, -1, -1, 1, -1)),
        Model("kun", "AYANEO KUN", ["KUN", "AYANEO KUN"], 3, 54,
            lighting: AyaneoLighting.Kun, chords: Kun()),
        Model("flip-kb", "AYANEO FLIP KB", ["FLIP KB"], 3, 28, motion: FlipMotion),
        Model("flip-ds", "AYANEO FLIP DS", ["FLIP DS"], 3, 28, lighting: AyaneoLighting.None,
            confirmation: false, secondary: true, motion: FlipMotion, chords: FlipScreen(false)),
        Model("flip-1s-kb", "AYANEO FLIP 1S KB", ["FLIP 1S KB"], 3, 28,
            motion: CeiiMotion, chords: Modern()),
        Model("flip-1s-ds", "AYANEO FLIP 1S DS", ["FLIP 1S DS"], 3, 28,
            lighting: AyaneoLighting.None, confirmation: false, secondary: true,
            motion: CeiiMotion, chords: FlipScreen(true)),
        Model("flip-11", "AYANEO FLIP 11", ["FLIP 11", "AYANEO FLIP 11"], 5, 54,
            confirmation: false, motion: CeiiMotion, chords: Modern()),
        Model("3-8840u", "AYANEO 3 8840U", ["AYANEO 3", "AB07"], 5, 30,
            lighting: AyaneoLighting.SuperJoy, confirmation: false,
            processors: ["AMD Ryzen 7 8840U w/ Radeon 780M Graphics", "AMD Ryzen 7 8840U with Radeon Graphics"],
            chords: Modular(), modular: true),
        Model("3-hx370", "AYANEO 3 HX 370", ["AYANEO 3", "AB07"], 5, 54,
            lighting: AyaneoLighting.SuperJoy, confirmation: false,
            processors: ["AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "AMD Ryzen AI 9 HX 370 w/ Radeon 890M Graphics"],
            chords: Modular(), modular: true),
        Model("next-2", "AYANEO NEXT 2", ["NEXT II", "NEXT 2", "AYANEO NEXT II", "AYANEO NEXT 2", "AB09"],
            5, 85, lighting: AyaneoLighting.SuperJoy, motion: Motion(-1, -1, 1, -1, -1, 1),
            chords: Modular(true)),
        Model("suiplay-0x1", "SuiPlay 0X1", ["SuiPlay0X1"], 3, 54,
            manufacturers: ["MYSTEN LABS, INC."], chords: Modern(true))
    ]);

    internal static string? Match(DeviceIdentitySnapshot identity)
    {
        string? selected = null;
        foreach (var model in All)
        {
            if (!model.Profile.Matches(identity))
            {
                continue;
            }

            if (selected is not null)
            {
                return null;
            }

            selected = model.Profile.Definition.Id;
        }

        return selected;
    }

    private static int PresetTarget(string id, DevicePowerPreset preset)
    {
        return id is "3-8840u" or "3-hx370" or "next-2"
            ? preset.Id switch { "battery" => 15, "balanced" => 30, _ => 45 }
            : preset.SustainedWatts;
    }

    private static AyaneoModel Model(string id, string name, string[] products, int minimum, int maximum,
        AyaneoEcProtocol protocol = AyaneoEcProtocol.CEc, AyaneoLighting? lighting = null,
        bool confirmation = true, bool secondary = false, bool modular = false,
        CpuPowerKind cpu = CpuPowerKind.Amd, string[]? processors = null, string[]? manufacturers = null,
        EcFanProfile? fan = null, StandardMotionProfile? motion = null, IReadOnlyList<KeyboardOemChord>? chords = null)
    {
        var light = lighting ?? (protocol == AyaneoEcProtocol.None ? AyaneoLighting.None
            : protocol == AyaneoEcProtocol.CEii ? AyaneoLighting.CEii : AyaneoLighting.CEc);
        var model = new AyaneoModel(new ProfiledDeviceModel
        {
            Definition = new HandheldDefinition("ayaneo." + id, FamilyId, name, "ayaneo-" + id)
            {
                HardwareAccess = DeviceHardwareAccess.PawnIo
            },
            SourceFamilyId = FamilyId, Manufacturers = manufacturers ?? Manufacturers,
            Products = products, Processors = processors ?? [], ProcessorSkus = id switch
            {
                "next-lite-4500u" => ["4500U"], "next-lite" => ["4800U"],
                "3-8840u" => ["8840U"], "3-hx370" => ["HX 370", "HX370"],
                "air-plus-amd-hhd" => ["6800U"], "air-plus-mendocino-hhd" => ["7320U"],
                "air-plus-intel-hhd" => ["1215U"], _ => []
            },
            Cpu = cpu,
            MinimumWatts = minimum, MaximumWatts = maximum,
            // HC AYANEODevice authors equal STAPM/slow/fast targets; AYANEO3/NEXT2 replace all three.
            PowerPresets = new[]
            {
                new DevicePowerPreset("battery", "Battery", 8, 8, DevicePowerMode.BetterBattery),
                new DevicePowerPreset("balanced", "Balanced", 15, 15, DevicePowerMode.Balanced),
                new DevicePowerPreset("performance", "Performance", 28, 28, DevicePowerMode.BestPerformance)
            }.Select(preset => ProfiledDeviceModel.ClampToModel(
                preset with { SustainedWatts = PresetTarget(id, preset), SlowWatts = PresetTarget(id, preset) },
                minimum, maximum, cpu)).ToArray(),
            Fan = protocol == AyaneoEcProtocol.None
                ? null
                : fan ?? (protocol == AyaneoEcProtocol.CEii
                    ? CeiiFan(0x1804)
                    : new EcFanProfile(0x4E, 0x4F, [0x044B], 100, 0x044A)),
            Controller = light == AyaneoLighting.SuperJoy ? ModularController : Controller,
            Motion = motion ?? (protocol == AyaneoEcProtocol.CEii ? CeiiMotion : CecMotion),
            KeyboardChords = chords ?? Cec(protocol == AyaneoEcProtocol.CEii),
            CreateOemSource = id == "kun" ? () => new OemMouseChordSource() : null,
            NativeOemReplacesChords = []
        }, protocol, light, confirmation, secondary, modular);
        return model with
        {
            Profile = model.Profile with
            {
                NativeRoles = protocol == AyaneoEcProtocol.None
                    ? []
                    : AyaneoNativeService.Describe(model).Select(item => item.Role).Distinct().ToArray(),
                ServiceFactories = protocol == AyaneoEcProtocol.None
                    ? []
                    : [context => new AyaneoNativeService(model, context)]
            }
        };
    }

    private static EcFanProfile CeiiFan(ushort duty)
    {
        return new EcFanProfile(0x4E, 0x4F, [duty], 255, 0xD1C8, 0xA5);
    }

    private static StandardMotionProfile Motion(float gx, float gy, float gz, float ax, float ay, float az)
    {
        return new StandardMotionProfile(new MotionAxisMap(0, 2, 1, gx, gy, gz),
            new MotionAxisMap(0, 2, 1, ax, ay, az));
    }

    private static KeyboardOemChord Chord(string id, string label, CanonicalButtons button, params uint[] keys)
    {
        return new KeyboardOemChord(id, label, keys, button);
    }

    private static IReadOnlyList<KeyboardOemChord> Cec(bool leftControl = false)
    {
        return
        [
            Chord("aya", "AYANEO", CanonicalButtons.Guide, leftControl ? 0xA2u : 0xA3u, 0x5B, 0x80),
            Chord("quick", "Quick access", CanonicalButtons.QuickAccess, 0x5B, 0x44),
            Chord("lc", "Left custom", CanonicalButtons.None, leftControl ? 0xA2u : 0xA3u, 0x5B, 0x7E),
            Chord("rc", "Right custom", CanonicalButtons.None, leftControl ? 0xA2u : 0xA3u, 0x5B, 0x7F)
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Air()
    {
        return
        [
            Chord("aya", "AYANEO", CanonicalButtons.Guide, 0xA3, 0x5B, 0x7B),
            Chord("quick", "Quick access", CanonicalButtons.QuickAccess, 0x5B, 0x44),
            Chord("lc", "Left custom", CanonicalButtons.None, 0xA3, 0x5B, 0x7A),
            Chord("rc", "Right custom", CanonicalButtons.None, 0xA3, 0x5B, 0x79)
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Legacy2021()
    {
        return
        [
            Chord("win", "Windows", CanonicalButtons.Guide, 0x5B),
            Chord("escape", "Escape", CanonicalButtons.QuickAccess, 0x1B),
            Chord("keyboard", "Keyboard", CanonicalButtons.None, 0xA3, 0x5B, 0x4F)
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Next()
    {
        return
        [
            Chord("aya", "AYANEO", CanonicalButtons.Guide, 0xA3, 0x5B, 0x7B),
            Chord("quick", "Quick access", CanonicalButtons.QuickAccess, 0x5B, 0x44)
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Modern(bool includeT = false)
    {
        return
        [
            Chord("aya", "AYANEO", CanonicalButtons.Guide, 0x86),
            Chord("quick", "Quick access", CanonicalButtons.QuickAccess, 0x87),
            Chord("lc", "Left custom", CanonicalButtons.None, 0x84),
            Chord("rc", "Right custom", CanonicalButtons.None, 0x85),
            .. includeT ? new[] { Chord("t", "T", CanonicalButtons.None, 0x81) } : Array.Empty<KeyboardOemChord>()
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Kun()
    {
        return
        [
            .. Cec(), .. Modern(true), Chord("t-old", "T", CanonicalButtons.None, 0xA3, 0x5B, 0x81),
            Chord("guide-mouse", "Guide", CanonicalButtons.Guide)
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> FlipScreen(bool modern)
    {
        return
        [
            .. Cec(), .. modern ? Modern() : Array.Empty<KeyboardOemChord>(),
            Chord("screen", "Secondary display", CanonicalButtons.None, 0xA3, 0x5B, 0x81),
            .. modern
                ? new[] { Chord("screen-new", "Secondary display", CanonicalButtons.None, 0x81) }
                : Array.Empty<KeyboardOemChord>()
        ];
    }

    private static IReadOnlyList<KeyboardOemChord> Modular(bool next = false)
    {
        return
        [
            .. Modern(), Chord("t", "T", CanonicalButtons.None, 0x7F),
            Chord("l4", "Left rear 1", CanonicalButtons.RearPaddle1, 0x82),
            Chord("l5", "Left rear 2", CanonicalButtons.RearPaddle3, 0x80),
            Chord("r4", "Right rear 1", CanonicalButtons.RearPaddle2, 0x83),
            Chord("r5", "Right rear 2", CanonicalButtons.RearPaddle4, 0x81),
            .. next
                ? new[] { Chord("quick-win", "Quick access", CanonicalButtons.QuickAccess, 0x5B, 0x44) }
                : Array.Empty<KeyboardOemChord>()
        ];
    }
}
