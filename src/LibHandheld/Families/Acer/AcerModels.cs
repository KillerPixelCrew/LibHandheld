using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Acer;

/// <summary>Exact Atlas model facts and the composition of its firmware-specific services.</summary>
/// <remarks>
///     PA08-I51 uses native EC power, WMI fans/battery and USB controller lighting, rather than the
///     generic CPU power/fan services. Standard XInput input/rumble and standard Windows motion remain
///     shared implementations. BIOS and controller-release restrictions belong to the individual
///     protocol/recovery bindings. The definition is hardware verified on a PA08-I51.
/// </remarks>
internal static class AcerModels
{
    internal const string FamilyId = "acer";

    private const string AtlasDefinitionId = "acer-predator-atlas-8-pa08-i51";

    private const string Atlas7DefinitionId = "acer-predator-atlas-7-pa07-i51";

    /// <summary>The Atlas fan presets, from the 2026-10-29 calibration in PROVENANCE.md.</summary>
    /// <remarks>
    ///     Near-inaudible shapes up to 60 °C with slow, averaged smoothing against the noisy EC package
    ///     temperature; "Fan curve presets (2026-10-29)" in PROVENANCE.md derives every value.
    /// </remarks>
    private static FanCurvePresetDuties AtlasFanCurvePresets { get; } = new()
    {
        Quiet = [10, 10, 10, 10, 10, 10, 15, 25, 45, 75, 100],
        Default = [15, 15, 15, 15, 15, 20, 30, 45, 65, 85, 100],
        Aggressive = [20, 20, 20, 20, 25, 35, 45, 60, 80, 100, 100],
        HysteresisCelsius = 3,
        MaximumRisePerTick = 5,
        MaximumFallPerTick = 2,
        AverageSamples = 10
    };

    /// <summary>The BOE NS080WUM-LX1 panel and its extended variable refresh range.</summary>
    /// <remarks>
    ///     The physical EDID hash, the five floor fields, the 28 Hz floor and the factory Intel state come from
    ///     the Atlas8Tweaks VRR Fix testing; "Extended VRR range" in PROVENANCE.md records them.
    /// </remarks>
    internal static ExtendedVrrPanel AtlasPanel { get; } = new("BOE", "0CFF", "NS080WUM-LX1",
        "F05B01B33B82B9597AE7BB15F5EF43CAEDB9194BF4A81B3008D34E8DBAC6ECCE", 48, 120, 28,
        [95, 142, 154, 174, 180], [95, 142, 154, 174],
        // Measured before any change: CUSTOM 48-120 Hz, 0/0 us, both Intel frame rate solutions on.
        new ExtendedVrrFactoryState(7, 48, 120, 0, 0, true, true));

    /// <summary>Predator Sense for Handheld, as the attended 2026-10-27 disable measured it.</summary>
    /// <remarks>
    ///     The services, three tasks, the widget's MSIX family and dedicated controller keyboard leaf of
    ///     OEM_INPUT_AND_DISABLE.md. They are exact PA08-I51 findings, so that entry applies to this definition only.
    ///     The desktop app (Handheld.Desktop, its own MSIX family) is left out since 2026-11-07, so the user
    ///     can start it: Acer delivers Atlas BIOS updates through it. Focus Zoom is disabled by the
    ///     maintainer's 2026-11-08 decision despite a tester's reported controller dependency; see provenance.
    /// </remarks>
    internal static IReadOnlyList<OemManagerApp> ManagerApps { get; } =
    [
        new("acer-predator-handheld", "Predator Sense for Handheld",
            ["Handheld.Widget", "FocusZoom", "FocusZoomUserService"],
            ["AASHHSvc", "AcerCCAgentSvis", "AcerDeviceEnablingServiceV2", "AcerHandheld.Service",
                "AcerLightingServiceHH", "AcerORDService", "AcerServiceSvc", "AFZsvc", "FocusZoomUserService",
                "ALSSvc", "UEIPSvc"],
            ["PredatorHandheldLauncher", "UEIPInvitation", "UbtFrameworkService"])
        {
            PackageFamilies = ["ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt"],
            DedicatedKeyboardDeviceId = @"HID\VID_0502&PID_F901&MI_02&COL01",
            DefinitionId = AtlasDefinitionId
        },
        // The same Acer stack on the Atlas 7, with its own controller's keyboard leaf.
        new("acer-predator-handheld-atlas-7", "Predator Sense for Handheld",
            ["Handheld.Widget", "FocusZoom", "FocusZoomUserService"],
            ["AASHHSvc", "AcerCCAgentSvis", "AcerDeviceEnablingServiceV2", "AcerHandheld.Service",
                "AcerLightingServiceHH", "AcerORDService", "AcerServiceSvc", "AFZsvc", "FocusZoomUserService",
                "ALSSvc", "UEIPSvc"],
            ["PredatorHandheldLauncher", "UEIPInvitation", "UbtFrameworkService"])
        {
            PackageFamilies = ["ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt"],
            DedicatedKeyboardDeviceId = @"HID\VID_0502&PID_F310&MI_02&COL01",
            DefinitionId = Atlas7DefinitionId
        }
    ];

    /// <summary>The Acer system/PTL board model; marketing name or controller VID/PID alone never selects it.</summary>
    internal static ProfiledDeviceModel Atlas8 { get; } = new()
    {
        Definition = new HandheldDefinition(AtlasDefinitionId, FamilyId,
            "Acer Predator Atlas 8 (PA08-I51)", "acer-predator-atlas-8")
        {
            // Power, fans, battery, lighting, controller, rear buttons, motion and rumble were accepted on
            // a PA08-I51 in attended sessions; see PROVENANCE.md.
            HardwareVerified = true,
            FanCurvePresets = AtlasFanCurvePresets,
            ExtendedVrrPanel = AtlasPanel
        },
        SourceFamilyId = "PredatorAtlas8",
        IdentitySource = ProfileIdentitySource.System,
        Manufacturers = ["Acer"],
        Products = ["Predator PA08-I51"],
        Cpu = CpuPowerKind.Intel,
        UseStandardServices = false,
        NativeRoles = [CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ScenarioMode,
            CapabilityRole.PowerBackend,
            CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanMeasuredRpm,
            CapabilityRole.ChargeProtectionMode, CapabilityRole.ChargeLimit, CapabilityRole.LightingBrightness,
            CapabilityRole.LightingEffect,
            CapabilityRole.LightingEffectSpeed, CapabilityRole.LightingZoneColor, CapabilityRole.Telemetry],
        // Generic bounds are unused: AcerPowerService discovers PL1/PL2 bounds from the EC itself.
        MinimumWatts = 0,
        MaximumWatts = 0,
        Controller = new StandardControllerProfile([new ControllerUsbId(0x0502, 0xF901), new ControllerUsbId(0x0502, 0xF90A)])
        {
            // Analog Rx/Ry triggers run 0..255; buttons 7/8 switch on at value 2 (2026-10-28 capture),
            // so they must not force the trigger to full.
            HidFallback = new HidControllerProfile([new ControllerUsbId(0x0502, 0xF90A)],
            [
                CanonicalButtons.X, CanonicalButtons.A, CanonicalButtons.B, CanonicalButtons.Y,
                CanonicalButtons.LeftShoulder, CanonicalButtons.RightShoulder,
                CanonicalButtons.None, CanonicalButtons.None, CanonicalButtons.View, CanonicalButtons.Menu,
                CanonicalButtons.LeftStick, CanonicalButtons.RightStick,
                CanonicalButtons.None, CanonicalButtons.None, CanonicalButtons.None,
                CanonicalButtons.None, CanonicalButtons.None
            ], LeftTriggerButton: 0, RightTriggerButton: 0),
            // Steam's SDL lists these XInput-topology leaves as extra Xbox pads. Each was hidden one
            // at a time on 2026-10-28 with the keyboard confirmed after every step; see PROVENANCE.md.
            AuxiliaryHidCollections =
            [
                "&PID_F901&MI_01&COL01\\", "&PID_F901&MI_02&COL03\\", "&PID_F901&MI_02&COL04\\",
                "&PID_F901&MI_02&COL05\\"
            ],
            OpenHidRumble = collection => new AcerDInputRumble(collection),
            HidCompanions = AcerInputProtocol.DInputCompanions,
            // Attended calibration; see PROVENANCE.md for the report and trial limits.
            // XInput low/high reach the left/right grips without swapping. The host applies the 2%
            // floor and 5 ms minimum only to bounded haptic events; continuous rumble is unchanged.
            // 5 ms was the shortest offered trial, not a measured hardware limit below that value.
            Haptics = new HapticCapabilities
            {
                LowFrequency = OutputChannelSupport.Native,
                HighFrequency = OutputChannelSupport.Native,
                MinimumStartIntensity = 0.02f,
                MinimumPulse = TimeSpan.FromMilliseconds(5)
            }
        },
        // The standard Windows sensor frame is landscape X right, Y up, Z out of the screen.
        // Canonical handheld input uses X right, Y out of the screen, Z down.
        // Both transforms were checked with isolated rotations and six gravity poses. The Windows
        // source supplies dps/g; controller tilt-to-stick packets are not raw calibrated IMU input.
        Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1),
            new MotionAxisMap(0, 2, 1, 1, 1, -1)),
        CreateOemSource = () => new AcerButtonSource(),
        OemControls = AcerButtonSource.Controls
    };

    /// <summary>The Atlas 7 PA07-I51, composed like the Atlas 8 over its own controller and firmware table.</summary>
    /// <remarks>
    ///     Its EC endpoint, command lane, OEM collections, BIOS V1.03 and Acer service binaries match the
    ///     measured Atlas 8; "PA07-I51 evidence" in PROVENANCE.md records the comparison. Extended VRR is absent
    ///     because that override needs a 256-byte EDID with a DisplayID block, and this panel's is 128 bytes.
    ///     Unverified until an attended pass accepts every control.
    /// </remarks>
    internal static ProfiledDeviceModel Atlas7 { get; } = new()
    {
        Definition = new HandheldDefinition(Atlas7DefinitionId, FamilyId,
            "Acer Predator Atlas 7 (PA07-I51)", "acer-predator-atlas-8")
        {
            HardwareVerified = false,
            FanCurvePresets = AtlasFanCurvePresets
        },
        SourceFamilyId = "PredatorAtlas7",
        IdentitySource = ProfileIdentitySource.System,
        Manufacturers = ["Acer"],
        Products = ["Predator PA07-I51"],
        Cpu = CpuPowerKind.Intel,
        UseStandardServices = false,
        NativeRoles = [CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ScenarioMode,
            CapabilityRole.PowerBackend,
            CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanMeasuredRpm,
            CapabilityRole.ChargeProtectionMode, CapabilityRole.ChargeLimit, CapabilityRole.LightingBrightness,
            CapabilityRole.LightingEffect,
            CapabilityRole.LightingEffectSpeed, CapabilityRole.LightingZoneColor, CapabilityRole.Telemetry],
        // Generic bounds are unused: AcerPowerService discovers PL1/PL2 bounds from the EC itself.
        MinimumWatts = 0,
        MaximumWatts = 0,
        // Only the XInput topology is measured. Its DInput product is unknown, so no HID fallback exists.
        Controller = new StandardControllerProfile([new ControllerUsbId(0x0502, AcerControllerTransport.Atlas7ProductId)])
        {
            // The same usages at the same interface and collection indexes as the Atlas 8's four leaves.
            AuxiliaryHidCollections =
            [
                "&PID_F310&MI_01&COL01\\", "&PID_F310&MI_02&COL03\\", "&PID_F310&MI_02&COL04\\",
                "&PID_F310&MI_02&COL05\\"
            ],
            Haptics = new HapticCapabilities
            {
                LowFrequency = OutputChannelSupport.Native,
                HighFrequency = OutputChannelSupport.Native,
                MinimumStartIntensity = 0.02f,
                MinimumPulse = TimeSpan.FromMilliseconds(5)
            }
        },
        Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1),
            new MotionAxisMap(0, 2, 1, 1, 1, -1)),
        CreateOemSource = () => new AcerButtonSource(),
        OemControls = AcerButtonSource.Controls
    };

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } =
        [Atlas8.DeclaredDefinition, Atlas7.DeclaredDefinition];

    /// <summary>The profile that owns a detected definition.</summary>
    internal static ProfiledDeviceModel Profile(HandheldDefinition definition)
    {
        return definition.Id == Atlas7DefinitionId ? Atlas7 : Atlas8;
    }

    /// <summary>Pure detection against a supplied snapshot: the exact Acer system plus its PTL baseboard.</summary>
    /// <returns>The one built-in definition ID, or null without opening hardware or probing OEM software.</returns>
    internal static string? Match(DeviceIdentitySnapshot identity)
    {
        // The system and board are independent selectors in the published inventory. Do not match
        // other Acer PCs or a future Atlas variant solely because it shares the controller VID/PID.
        if (!string.Equals(identity.BaseboardManufacturer?.Trim(), "PTL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var board = identity.BaseboardProduct?.Trim();
        if (Atlas8.Matches(identity) && string.Equals(board, "F900_PTG", StringComparison.OrdinalIgnoreCase))
        {
            return Atlas8.Definition.Id;
        }

        return Atlas7.Matches(identity) && string.Equals(board, "G310_PTG", StringComparison.OrdinalIgnoreCase)
            ? Atlas7.Definition.Id
            : null;
    }

    /// <summary>Creates the profiled lifecycle after exact detection; construction itself acquires no hardware.</summary>
    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        // One EC lane for power and fans, one controller lane for mode and lighting: each endpoint is
        // enumerated once, and lighting keeps working when a mode change re-enumerates the controller.
        var ec = new AcerLease<IAcerEcTransport>(AcerEcTransport.Open);
        var controller = new AcerLease<AcerControllerTransport>(AcerControllerTransport.Open);
        if (definition.Id == Atlas7DefinitionId)
        {
            var rings = new AcerRingLights();
            return new ProfiledDeviceEngine(Atlas7 with
            {
                // The Atlas 8's services in its order. The firmware's mode table and the ring output differ.
                ServiceFactories = [context => new AcerControllerModeService(context, controller),
                    context => new AcerPowerService(context, ec, IntelMsrPowerTransport.Open, AcerPowerTable.Atlas7),
                    context => new AcerFanService(context, ec),
                    context => new AcerBatteryService(context, ec), context => new AcerLightingService(context, controller, rings),
                    _ => new AcerLogoLightingService(),
                    context => new IntelPdcPowerButtonService(context)]
            });
        }

        return new ProfiledDeviceEngine(Atlas8 with
        {
            // The mode owner supplies Combination rear reports; it must precede the dependent OEM readers.
            // This mode is lifecycle-owned, not a user-visible remapping option or a permanent ROM setting.
            ServiceFactories = [context => new AcerControllerModeService(context, controller),
                context => new AcerPowerService(context, ec, IntelMsrPowerTransport.Open),
                context => new AcerFanService(context, ec),
                context => new AcerBatteryService(context, ec), context => new AcerLightingService(context, controller),
                _ => new AcerLogoLightingService(),
                context => new IntelPdcPowerButtonService(context)]
        });
    }
}
