using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Families.Acer;

/// <summary>Exact Atlas model facts and the composition of its firmware-specific services.</summary>
/// <remarks>
///     PA08-I51 uses native EC power, WMI fans/battery and USB controller lighting, rather than the
///     generic CPU power/fan services. Standard XInput input/rumble and standard Windows motion remain
///     shared implementations. BIOS and controller-release restrictions belong to the individual
///     protocol/recovery bindings; the definition deliberately remains not fully HardwareVerified.
/// </remarks>
internal static class AcerModels
{
    internal const string FamilyId = "acer";

    /// <summary>The Acer system/PTL board model; marketing name or controller VID/PID alone never selects it.</summary>
    internal static ProfiledDeviceModel Atlas8 { get; } = new()
    {
        Definition = new HandheldDefinition("acer-predator-atlas-8-pa08-i51", FamilyId,
            "Acer Predator Atlas 8 (PA08-I51)", "acer-predator-atlas-8"),
        SourceFamilyId = "PredatorAtlas8",
        IdentitySource = ProfileIdentitySource.System,
        Manufacturers = ["Acer"],
        Products = ["Predator PA08-I51"],
        Cpu = CpuPowerKind.Intel,
        UseStandardServices = false,
        NativeRoles = [CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ScenarioMode,
            CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanMeasuredRpm,
            CapabilityRole.ChargeProtectionMode, CapabilityRole.LightingBrightness, CapabilityRole.LightingEffect,
            CapabilityRole.LightingEffectSpeed, CapabilityRole.LightingZoneColor, CapabilityRole.Telemetry],
        // The mode owner supplies Combination rear reports; it must precede the dependent OEM readers.
        // This mode is lifecycle-owned, not a user-visible remapping option or a permanent ROM setting.
        ServiceFactories = [context => new AcerControllerModeService(context),
            context => new AcerPowerService(context), context => new AcerFanService(context),
            context => new AcerBatteryService(context), context => new AcerLightingService(context)],
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

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } = [Atlas8.DeclaredDefinition];

    /// <summary>Pure detection against a supplied snapshot: Acer PA08-I51 plus PTL/F900_PTG baseboard.</summary>
    /// <returns>The one built-in definition ID, or null without opening hardware or probing OEM software.</returns>
    internal static string? Match(DeviceIdentitySnapshot identity)
    {
        // The system and board are independent selectors in the published inventory. Do not match
        // other Acer PCs or a future Atlas variant solely because it shares the controller VID/PID.
        return Atlas8.Matches(identity)
               && string.Equals(identity.BaseboardManufacturer?.Trim(), "PTL", StringComparison.OrdinalIgnoreCase)
               && string.Equals(identity.BaseboardProduct?.Trim(), "F900_PTG", StringComparison.OrdinalIgnoreCase)
            ? Atlas8.Definition.Id
            : null;
    }

    /// <summary>Creates the profiled lifecycle after exact detection; construction itself acquires no hardware.</summary>
    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(Atlas8);
    }
}
