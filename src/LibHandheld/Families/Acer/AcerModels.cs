using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Families.Acer;

internal static class AcerModels
{
    internal const string FamilyId = "acer";

    internal static ProfiledDeviceModel Atlas8 { get; } = new()
    {
        Definition = new HandheldDefinition("acer-predator-atlas-8-pa08-i51", FamilyId,
            "Acer Predator Atlas 8 (PA08-I51)", "acer-predator-atlas-8")
        {
            HardwareAccess = DeviceHardwareAccess.IntelKx
        },
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
        ServiceFactories = [context => new AcerPowerService(context), context => new AcerFanService(context),
            context => new AcerBatteryService(context), context => new AcerLightingService(context)],
        MinimumWatts = 0,
        MaximumWatts = 0,
        Controller = new StandardControllerProfile([new ControllerUsbId(0x0502, 0xF901)]),
        // The standard Windows sensor frame is landscape X right, Y up, Z out of the screen.
        // Canonical handheld input uses X right, Y out of the screen, Z down.
        Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1),
            new MotionAxisMap(0, 2, 1, 1, 1, -1)),
        CreateOemSource = () => new AcerButtonSource(),
        OemControls = AcerButtonSource.Controls
    };

    internal static IReadOnlyList<HandheldDefinition> Definitions { get; } = [Atlas8.DeclaredDefinition];

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

    internal static NativeEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(Atlas8);
    }
}
