using System.Numerics;
using LibHandheld.Contracts;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>Which of the two front-button layouts a model has.</summary>
internal enum AllyFrontLayout
{
    /// <summary>ROG Ally and Ally X: Command Center on the left, Armoury Crate on the right.</summary>
    Classic,

    /// <summary>ROG Xbox Ally and Xbox Ally X: an Xbox button, Armoury Crate and Library.</summary>
    Xbox
}

/// <summary>One sensor-to-application axis transform, in HC's <c>IMUMatrix</c> form.</summary>
/// <param name="SourceX">Raw axis (0 = X, 1 = Y, 2 = Z) that feeds application X.</param>
/// <param name="SourceY">Raw axis that feeds application Y.</param>
/// <param name="SourceZ">Raw axis that feeds application Z.</param>
/// <param name="SignX">Sign applied to application X.</param>
/// <param name="SignY">Sign applied to application Y.</param>
/// <param name="SignZ">Sign applied to application Z.</param>
/// <remarks>
///     HC's device JSON writes <c>AxisSwap {X: X, Y: Z, Z: Y}</c> and a sign per output axis
///     (<c>HandheldCompanion.Devices/IMUMatrix.cs</c>, <c>ComputeRemapIndices</c>, and
///     <c>HandheldCompanion.Sensors/IMUWindowsGyrometer.cs:58-66</c>): the raw axis named on the left
///     lands on the output axis named on the right, then the output sign applies. HC's Claw A2VM file
///     (<c>X: 1, Y: 1, Z: -1</c>) produces exactly the <c>(raw X, raw Z, -raw Y)</c> basis the Claw
///     plugin measured, so HC's output basis and WSGM's <see cref="MotionSample" /> basis are the same.
/// </remarks>
internal readonly record struct AxisMap(
    int SourceX,
    int SourceY,
    int SourceZ,
    float SignX,
    float SignY,
    float SignZ)
{
    /// <summary>HC's <c>AxisSwap {X: X, Y: Z, Z: Y}</c> with the given output signs.</summary>
    /// <param name="signX">Multiplier applied to output X from raw X.</param>
    /// <param name="signY">Multiplier applied to output Y from raw Z.</param>
    /// <param name="signZ">Multiplier applied to output Z from raw Y.</param>
    /// <returns>The requested axis transform; multipliers are retained without validation.</returns>
    public static AxisMap SwapYz(float signX, float signY, float signZ)
    {
        return new AxisMap(0, 2, 1, signX, signY, signZ);
    }

    public Vector3 Apply(Vector3 raw)
    {
        return new Vector3(Pick(raw, SourceX) * SignX, Pick(raw, SourceY) * SignY, Pick(raw, SourceZ) * SignZ);
    }

    private static float Pick(Vector3 raw, int axis)
    {
        return axis switch
        {
            0 => raw.X,
            1 => raw.Y,
            _ => raw.Z
        };
    }
}

/// <summary>A power shortcut as HC declares it: one watt target and one ASUS performance mode.</summary>
/// <param name="Id">Stable preset identifier published to the host.</param>
/// <param name="Name">User-facing preset name.</param>
/// <param name="Watts">Target package wattage associated with this shortcut.</param>
/// <param name="Scenario">ASUS performance-mode identifier.</param>
/// <param name="WindowsMode">Windows power-mode preference paired with the device settings.</param>
internal sealed record AllyPreset(string Id, string Name, int Watts, string Scenario, DevicePowerMode WindowsMode);

/// <summary>Everything that differs between the four supported Ally models.</summary>
/// <remarks>
///     Every per-model fact lives here and nowhere else, so a Device Lab report corrects one row
///     rather than a scattering of conditionals. The source of each value is in PROVENANCE.md.
/// </remarks>
internal sealed record AllyModel
{
    /// <summary>Stable device definition ID, also the glyph profile's exact device ID.</summary>
    public required string DefinitionId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>SMBIOS type 2 baseboard products that identify this model exactly.</summary>
    public required IReadOnlyList<string> BaseboardProducts { get; init; }

    /// <summary>ASUS USB product IDs the controller MCU may enumerate with.</summary>
    public required IReadOnlyList<ushort> ControllerProductIds { get; init; }

    public required AllyFrontLayout Layout { get; init; }

    public required int MinimumWatts { get; init; }

    public required int MaximumWatts { get; init; }

    public required IReadOnlyList<AllyPreset> Presets { get; init; }

    public required AxisMap Gyro { get; init; }

    public required AxisMap Accelerometer { get; init; }

    /// <summary>Glyph profile shipped for this model.</summary>
    public required string GlyphProfileId { get; init; }

    /// <summary>
    ///     Whether the lamp array must be put in autonomous mode before Aura commands take effect.
    /// </summary>
    public bool DisableDynamicLighting { get; init; }

    /// <summary>Virtual keys the firmware sends for the front buttons, when it uses the keyboard.</summary>
    public IReadOnlyList<AllyKeyboardControl> FrontKeyboardControls { get; init; } = [];

    /// <summary>
    ///     Whether M1 and M2 already send F18 and F17 before any controller table is written, so the rear
    ///     keys are watched even when the tables are refused.
    /// </summary>
    public bool RearKeysNative { get; init; }
}

/// <summary>One OEM button that arrives on the ASUS keyboard collection.</summary>
/// <param name="VirtualKey">Windows virtual key the firmware sends.</param>
/// <param name="ControlId">WSGM OEM control it becomes.</param>
/// <param name="Button">Canonical button held while the key is down.</param>
internal readonly record struct AllyKeyboardControl(uint VirtualKey, string ControlId, CanonicalButtons Button);

/// <summary>One vendor input report code and what it means on a model.</summary>
/// <param name="ControlId">WSGM OEM control it becomes.</param>
/// <param name="Press">Short or long press.</param>
/// <param name="Button">Canonical button to latch, or none for an event-only code.</param>
/// <param name="Edge">Press or release edge for controls that report both.</param>
internal readonly record struct AllyVendorAction(
    string ControlId,
    OemPressKind Press,
    CanonicalButtons Button,
    OemControlEdge Edge = OemControlEdge.Pressed);

internal static class AllyModels
{
    public const string PackageId = "rog-ally";
    public const string Manufacturer = "ASUSTeK COMPUTER INC.";
    public const ushort AsusVendorId = 0x0B05;

    /// <summary>F17. With HHD's M1/M2 table the right rear button sends it (Device Lab, RC73XA).</summary>
    public const uint VkF17 = 0x80;

    /// <summary>F18. With HHD's M1/M2 table the left rear button sends it (Device Lab, RC73XA).</summary>
    public const uint VkF18 = 0x81;

    /// <summary>F21, the Xbox models' left front button in the Device Lab run.</summary>
    public const uint VkF21 = 0x84;

    /// <summary>F22, the Xbox models' right front button in the Device Lab run.</summary>
    public const uint VkF22 = 0x85;

    // HC declares the same product IDs for every Ally class (ROGAlly.cs:213, inherited by ROGAllyX,
    // XboxROGAlly and XboxROGAllyX); HHD uses 0x1ABE only for RC71L and 0x1B4C for the rest
    // (rog_ally/base.py:29-30, 347-393). Accepting both keeps a model that re-enumerates with the other
    // ID working; exact identity is the SMBIOS gate, not the USB ID.
    private static readonly ushort[] AllyProductIds = [0x1ABE, 0x1B4C];

    // HC 1.3.1.6 ROGAlly.cs:229-259. Silent/Performance/Turbo are OEMPowerMode 2/0/1, the ASUS
    // throttle_thermal_policy values HHD writes too (adjustor/drivers/asus/__init__.py:296-303).
    private static readonly IReadOnlyList<AllyPreset> AllyPresets =
    [
        new("silent", "Silent", 10, Scenarios.Silent, DevicePowerMode.BetterBattery),
        new("performance", "Performance", 15, Scenarios.Performance, DevicePowerMode.Balanced),
        new("turbo", "Turbo", 25, Scenarios.Turbo, DevicePowerMode.BestPerformance)
    ];

    // HC 1.3.1.6 ROGAllyX.cs, XboxROGAlly.cs and XboxROGAllyX.cs override the watt targets to 13/17/25.
    private static readonly IReadOnlyList<AllyPreset> AllyXPresets =
    [
        new("silent", "Silent", 13, Scenarios.Silent, DevicePowerMode.BetterBattery),
        new("performance", "Performance", 17, Scenarios.Performance, DevicePowerMode.Balanced),
        new("turbo", "Turbo", 25, Scenarios.Turbo, DevicePowerMode.BestPerformance)
    ];

    // HC Resources/Devices/ROGAlly.json and ROGAllyX.json: both matrices X: -1, Y: -1, Z: 1.
    private static readonly AxisMap ClassicMotion = AxisMap.SwapYz(-1, -1, 1);

    // HC Resources/Devices/XboxROGAlly.json and XboxROGAllyX.json: gyro X: 1, Y: 1, Z: -1, while
    // the accelerometer keeps the classic signs. HHD maps all four identically (base.py:34-42).
    private static readonly AxisMap XboxGyro = AxisMap.SwapYz(1, 1, -1);

    // The Xbox button is the Guide (XInput), so Armoury Crate carries no Steam button: WSGM opens its
    // overlay from it as the companion-application button. Library is Steam's Quick Access.
    private static readonly IReadOnlyList<AllyKeyboardControl> XboxKeyboardFront =
    [
        new(VkF21, OemControlIds.ArmouryCrate, CanonicalButtons.None),
        new(VkF22, OemControlIds.Library, CanonicalButtons.QuickAccess)
    ];

    public static IReadOnlyList<AllyModel> All { get; } =
    [
        new()
        {
            DefinitionId = "rc71l",
            DisplayName = "ROG Ally",
            BaseboardProducts = ["RC71L"],
            ControllerProductIds = AllyProductIds,
            Layout = AllyFrontLayout.Classic,
            // HC ROGAlly.cs:215 cTDP 5-30; HHD adjustor/core/const.py:261-274 min 5, max 30.
            MinimumWatts = 5,
            MaximumWatts = 30,
            Presets = AllyPresets,
            Gyro = ClassicMotion,
            Accelerometer = ClassicMotion,
            GlyphProfileId = "rog-ally"
        },
        new()
        {
            DefinitionId = "rc72la",
            DisplayName = "ROG Ally X",
            // HC IDevice.cs:1034 matches RC72LA; HHD's product-name match is "ROG Ally X RC72L"
            // (adjustor/core/const.py:339), so the shorter board name is admitted too.
            BaseboardProducts = ["RC72LA", "RC72L"],
            ControllerProductIds = AllyProductIds,
            Layout = AllyFrontLayout.Classic,
            MinimumWatts = 5,
            MaximumWatts = 30,
            Presets = AllyXPresets,
            Gyro = ClassicMotion,
            Accelerometer = ClassicMotion,
            GlyphProfileId = "rog-ally"
        },
        new()
        {
            DefinitionId = "rc73ya",
            DisplayName = "ROG Xbox Ally",
            BaseboardProducts = ["RC73YA"],
            ControllerProductIds = AllyProductIds,
            Layout = AllyFrontLayout.Xbox,
            // HC XboxROGAlly.cs:14 declares 35 W and its Silent preset is 13 W. HC's maximum is
            // authoritative for this Windows implementation; the BIOS enforces its own limit.
            // The minimum is lowered so HC's presets validate.
            MinimumWatts = 5,
            MaximumWatts = 35,
            Presets = AllyXPresets,
            Gyro = XboxGyro,
            Accelerometer = ClassicMotion,
            GlyphProfileId = "rog-xbox-ally",
            DisableDynamicLighting = true,
            // HC reads the Xbox button as the pad's guide bit (XInputController.cs:376-377), which
            // stays the Guide. HHD's share_to_qam (base.py:415-427) sent it to QAM instead, which
            // left no button for Steam's main menu.
            FrontKeyboardControls = XboxKeyboardFront,
            // Assumed from the RC73XA Device Lab run; the Xbox Ally has not been measured.
            RearKeysNative = true
        },
        new()
        {
            DefinitionId = "rc73xa",
            DisplayName = "ROG Xbox Ally X",
            BaseboardProducts = ["RC73XA"],
            ControllerProductIds = AllyProductIds,
            Layout = AllyFrontLayout.Xbox,
            // HC XboxROGAllyX.cs:14 cTDP 15-35; HHD adjustor/core/const.py:307-320 min 4, max 35.
            MinimumWatts = 5,
            MaximumWatts = 35,
            Presets = AllyXPresets,
            Gyro = XboxGyro,
            Accelerometer = ClassicMotion,
            GlyphProfileId = "rog-xbox-ally",
            DisableDynamicLighting = true,
            FrontKeyboardControls = XboxKeyboardFront,
            // Device Lab 2026-09-25: M1 sent F18 and M2 F17 with no table written.
            RearKeysNative = true
        }
    ];

    /// <summary>Returns the exact model for an identity, or null when this package does not apply.</summary>
    /// <param name="identity">SMBIOS snapshot containing baseboard manufacturer and product.</param>
    /// <returns>The exact supported model, or null when the baseboard identity is outside this package.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity" /> is null.</exception>
    public static AllyModel? Match(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Match(identity.BaseboardManufacturer, identity.BaseboardProduct);
    }

    /// <summary>Returns the exact model for a baseboard manufacturer and product.</summary>
    /// <param name="manufacturer">Baseboard manufacturer; null or a non-ASUS value does not match.</param>
    /// <param name="product">Baseboard product, trimmed and compared case-insensitively against exact model aliases.</param>
    /// <returns>The first exact supported model, or null for missing or unmatched fields.</returns>
    public static AllyModel? Match(string? manufacturer, string? product)
    {
        if (!string.Equals(manufacturer?.Trim(), Manufacturer, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        var board = product.Trim();
        return All.FirstOrDefault(model => model.BaseboardProducts.Any(candidate =>
            string.Equals(candidate, board, StringComparison.OrdinalIgnoreCase)));
    }

    public static AllyModel? ById(string? definitionId)
    {
        return All.FirstOrDefault(model => string.Equals(model.DefinitionId, definitionId, StringComparison.Ordinal));
    }

    /// <summary>What one vendor report code means on a model, or null for a code it ignores.</summary>
    /// <remarks>
    ///     HC maps 0x93 to a separate Library control and 0xA7/0xA8 to M2 press/release
    ///     (ROGAlly.cs:53-83, 485-505). HHD merges 0x38/0x93 and ignores 0xA8; HC's Windows
    ///     behavior is followed until Device Lab can distinguish the paths on each model. On the Xbox
    ///     models the Xbox button is the Guide, Library is Quick Access, and Armoury Crate carries no
    ///     Steam button because WSGM opens its overlay from it.
    /// </remarks>
    /// <param name="model">Model whose physical front-button layout determines the action.</param>
    /// <param name="code">Event byte decoded from a 0x5A vendor input report.</param>
    /// <returns>Logical OEM control, press/release edge and canonical button contribution, or null for an ignored code.</returns>
    public static AllyVendorAction? VendorAction(AllyModel model, byte code)
    {
        var (left, right) = model.Layout is AllyFrontLayout.Xbox
            ? (OemControlIds.ArmouryCrate, OemControlIds.Library)
            : (OemControlIds.CommandCenter, OemControlIds.ArmouryCrate);
        return code switch
        {
            0xA6 => new AllyVendorAction(left, OemPressKind.Short,
                model.Layout is AllyFrontLayout.Xbox ? CanonicalButtons.None : CanonicalButtons.QuickAccess),
            0x38 => new AllyVendorAction(right, OemPressKind.Short,
                model.Layout is AllyFrontLayout.Xbox ? CanonicalButtons.QuickAccess : CanonicalButtons.Guide),
            0x93 => new AllyVendorAction(OemControlIds.Library, OemPressKind.Short,
                model.Layout is AllyFrontLayout.Xbox ? CanonicalButtons.QuickAccess : CanonicalButtons.None),
            0xA7 => new AllyVendorAction(OemControlIds.M2, OemPressKind.Short, CanonicalButtons.RearPaddle2),
            0xA8 => new AllyVendorAction(OemControlIds.M2, OemPressKind.Short, CanonicalButtons.RearPaddle2,
                OemControlEdge.Released),
            _ => null
        };
    }

    /// <summary>The OEM controls a model publishes.</summary>
    /// <param name="model">Model determining the front layout and published rear controls.</param>
    /// <returns>The model's ordered OEM descriptors; this does not probe current input availability.</returns>
    public static IReadOnlyList<OemControlDescriptor> OemControls(AllyModel model)
    {
        OemControlDescriptor[] controls = model.Layout is AllyFrontLayout.Xbox
            ?
            [
                Oem(OemControlIds.ArmouryCrate, "Armoury Crate", OemControlPlacement.Front, false) with
                {
                    CompanionApplication = true
                },
                Oem(OemControlIds.Library, "Library", OemControlPlacement.Front, false),
                Oem(OemControlIds.M1, "M1", OemControlPlacement.Rear, true),
                Oem(OemControlIds.M2, "M2", OemControlPlacement.Rear, true)
            ]
            :
            [
                Oem(OemControlIds.CommandCenter, "Command Center", OemControlPlacement.Front, false),
                Oem(OemControlIds.ArmouryCrate, "Armoury Crate", OemControlPlacement.Front, false),
                Oem(OemControlIds.Library, "Library", OemControlPlacement.Front, false),
                Oem(OemControlIds.M1, "M1", OemControlPlacement.Rear, true),
                Oem(OemControlIds.M2, "M2", OemControlPlacement.Rear, true)
            ];
        var buttons = new[] { VendorAction(model, code: 0xA6), VendorAction(model, code: 0x38),
            VendorAction(model, code: 0x93) };
        return controls.Select(control => control with
        {
            ControllerButton = buttons.FirstOrDefault(button => button?.ControlId == control.ControlId)?.Button
                               ?? CanonicalButtons.None
        }).ToArray();
    }

    /// <summary>ASUS performance modes, by the value ATKACPI and HC's OEMPowerMode use.</summary>
    /// <param name="scenario">Performance, Turbo or Silent scenario identifier.</param>
    /// <returns>The native performance-mode byte: 0, 1 or 2 respectively.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scenario" /> is unrecognized.</exception>
    public static byte ScenarioValue(string scenario)
    {
        return scenario switch
        {
            Scenarios.Performance => 0,
            Scenarios.Turbo => 1,
            Scenarios.Silent => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    public static string? ScenarioName(int value)
    {
        return value switch
        {
            0 => Scenarios.Performance,
            1 => Scenarios.Turbo,
            2 => Scenarios.Silent,
            _ => null
        };
    }

    public static IReadOnlyList<DevicePowerPreset> PowerPresets(AllyModel model)
    {
        return
        [
            .. model.Presets.Select(preset => new DevicePowerPreset(
                preset.Id,
                preset.Name,
                preset.Watts,
                preset.Watts,
                preset.WindowsMode)
            {
                ScenarioOnAc = preset.Scenario,
                ScenarioOnDc = preset.Scenario
            })
        ];
    }

    private static OemControlDescriptor Oem(
        string id,
        string label,
        OemControlPlacement placement,
        bool requiresController)
    {
        return new OemControlDescriptor
        {
            ControlId = id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = label },
            Placement = placement,
            RequiresControllerAcquisition = requiresController
        };
    }
}

internal static class OemControlIds
{
    public const string CommandCenter = "command-center";
    public const string ArmouryCrate = "armoury-crate";
    public const string Library = "library";
    public const string M1 = "m1";
    public const string M2 = "m2";
}

internal static class Scenarios
{
    public const string Silent = "silent";
    public const string Performance = "performance";
    public const string Turbo = "turbo";
}
