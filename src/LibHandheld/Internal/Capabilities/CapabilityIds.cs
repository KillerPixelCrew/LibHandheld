namespace LibHandheld.Internal.Capabilities;

/// <summary>The capability IDs the first-party device packages declare.</summary>
/// <remarks>
///     WSGM persists these in saved profiles, so a value never changes: a renamed ID orphans every saved
///     per-game value for it. A package declaring one of these capabilities uses the constant. Section,
///     instance and effect names stay the package's own.
/// </remarks>
internal static class CapabilityIds
{
    /// <summary>The sustained power limit (PL1, SPL).</summary>
    public const string PowerSustained = "power.primary-limit";

    /// <summary>The boost power limit (PL2, SPPT and FPPT).</summary>
    public const string PowerBoost = "power.boost-limit";

    /// <summary>The independent AMD fast PPT limit.</summary>
    public const string PowerFast = "power.fast-limit";

    /// <summary>The firmware's performance scenario or mode.</summary>
    public const string Scenario = "power.scenario";

    /// <summary>The hardware path that carries the CPU power limits.</summary>
    public const string PowerBackend = "power.backend";

    /// <summary>The battery charge ceiling.</summary>
    public const string ChargeLimit = "battery.charge-limit";

    /// <summary>Whether the fans follow the firmware or a custom curve.</summary>
    public const string FanMode = "fan.mode";

    /// <summary>The custom fan curve.</summary>
    public const string FanCurve = "fan.curve";

    /// <summary>A fan's measured speed as the MSI Claw reports it.</summary>
    public const string FanRpm = "fan.measured-rpm";

    /// <summary>A fan's reading as the ASUS ROG Ally reports it.</summary>
    /// <remarks>Distinct from <see cref="FanRpm" /> because both IDs are already persisted.</remarks>
    public const string FanReading = "fan.reading";

    /// <summary>A temperature reading.</summary>
    public const string Temperature = "telemetry.temperature";

    /// <summary>The lighting brightness.</summary>
    public const string LightingBrightness = "lighting.brightness";

    /// <summary>The lighting effect.</summary>
    public const string LightingEffect = "lighting.effect";

    /// <summary>The speed of the lighting effect.</summary>
    public const string LightingSpeed = "lighting.effect-speed";

    /// <summary>A lighting zone's colour.</summary>
    public const string LightingColor = "lighting.zone-color";

    /// <summary>Who owns the physical controller.</summary>
    public const string Controller = "controller.source";

    /// <summary>Who owns the motion sensors.</summary>
    public const string Motion = "motion.source";

    /// <summary>The rumble sink.</summary>
    public const string Rumble = "haptic.rumble";
}

/// <summary>The choices of a controller or motion source-ownership capability.</summary>
internal static class SourceOwnership
{
    /// <summary>The device's own firmware still has the source; the resting state.</summary>
    public const string Device = "device";

    /// <summary>The plugin acquired the source.</summary>
    public const string Managed = "managed";

    /// <summary>The acquisition failed or this unit does not expose the source.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The three choices, resting state first.</summary>
    public static IReadOnlyList<string> Choices { get; } = Array.AsReadOnly<string>([Device, Managed, Unavailable]);
}
