using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     What a capability means, independent of how any device implements it.
/// </summary>
/// <remarks>
///     Consumers receive semantic roles, units and bounds without native transport details.
///     The backend owns vendor WMI, EC, SMU or MMIO operations.
///     <para>
///         Vendor-specific roles cover features such as UMA allocation, a secondary display or USB-C routing.
///     </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityRole>))]
public enum CapabilityRole
{
    /// <summary>Sustained processor power limit.</summary>
    PowerSustainedLimit,

    /// <summary>Slow-window processor power limit.</summary>
    PowerSlowLimit,

    /// <summary>Fast-window processor power limit.</summary>
    PowerFastLimit,

    /// <summary>Peak or burst processor power limit.</summary>
    PowerPeakLimit,

    /// <summary>Vendor performance or scenario mode.</summary>
    ScenarioMode,

    /// <summary>Fan operating mode, such as automatic, custom, or full speed.</summary>
    FanMode,

    /// <summary>Fan duty cycle for one channel.</summary>
    FanDuty,

    /// <summary>Target fan speed in RPM for one channel.</summary>
    FanTargetRpm,

    /// <summary>Editable fan curve for one channel.</summary>
    FanCurve,

    /// <summary>Measured fan speed for one channel.</summary>
    FanMeasuredRpm,

    /// <summary>Battery charge ceiling.</summary>
    ChargeLimit,

    /// <summary>Battery charge protection mode.</summary>
    ChargeProtectionMode,

    /// <summary>Bypass or pass-through charging.</summary>
    ChargeBypass,

    /// <summary>Master lighting power.</summary>
    LightingPower,

    /// <summary>Lighting brightness.</summary>
    LightingBrightness,

    /// <summary>Colour of one lighting zone.</summary>
    LightingZoneColor,

    /// <summary>Lighting effect selection.</summary>
    LightingEffect,

    /// <summary>Lighting effect speed.</summary>
    LightingEffectSpeed,

    /// <summary>A hardware telemetry reading such as a temperature or a power draw.</summary>
    Telemetry,

    /// <summary>The physical controller input source.</summary>
    ControllerSource,

    /// <summary>The motion sensor source.</summary>
    MotionSource,

    /// <summary>A rumble or haptic output sink.</summary>
    HapticSink,

    /// <summary>
    ///     Variable refresh rate for a panel.
    /// </summary>
    /// <remarks>
    ///     A graphics driver normally owns adaptive sync, such as Intel IGCL's Arc Sync. A handheld
    ///     provider may expose it for a panel only it can reach. The consumer chooses its projection.
    /// </remarks>
    VariableRefreshRate,

    /// <summary>A logical OEM control the user may reassign.</summary>
    OemControl,

    /// <summary>A device-specific on/off switch with no more specific role.</summary>
    GenericToggle,

    /// <summary>A device-specific numeric range with no more specific role.</summary>
    GenericRange,

    /// <summary>A device-specific choice from a fixed set with no more specific role.</summary>
    GenericChoice,

    /// <summary>A device-specific one-shot action with no more specific role.</summary>
    GenericAction,

    /// <summary>A device-specific short text value with no more specific role.</summary>
    GenericText,

    /// <summary>A device-specific read-only value with no more specific role.</summary>
    GenericReadOnly,

    /// <summary>Which hardware path carries the CPU power limits: firmware, PawnIO MSR or KX MSR plus MCHBAR.</summary>
    PowerBackend
}

/// <summary>The shape of a capability's value, which decides how it is rendered and validated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityValueKind>))]
public enum CapabilityValueKind
{
    /// <summary>No value; the capability is invoked rather than set.</summary>
    None,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>An integer within a declared range.</summary>
    Integer,

    /// <summary>One option from a declared set.</summary>
    Choice,

    /// <summary>A colour, expressed as 24-bit RGB.</summary>
    Color,

    /// <summary>An ordered set of points, such as a fan curve.</summary>
    Curve,

    /// <summary>
    ///     Short plain text the user types.
    /// </summary>
    /// <remarks>
    ///     The only kind not constrained by construction, so it carries the same text contract as
    ///     <see cref="CapabilityDisplay.CustomText" />: a declared maximum length with control and
    ///     bidirectional formatting characters rejected.
    /// </remarks>
    Text
}

/// <summary>Units a numeric capability may carry.</summary>
/// <remarks>
///     A closed set rather than a free-text unit string: WSGM formats and localizes these, and a plugin
///     supplying "Watt " or "watts" would leak straight into the UI.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityUnit>))]
public enum CapabilityUnit
{
    /// <summary>Dimensionless.</summary>
    None,

    /// <summary>Watts.</summary>
    Watt,

    /// <summary>Percent.</summary>
    Percent,

    /// <summary>Degrees Celsius.</summary>
    Celsius,

    /// <summary>Revolutions per minute.</summary>
    Rpm,

    /// <summary>Milliamperes.</summary>
    Milliampere,

    /// <summary>Millivolts.</summary>
    Millivolt,

    /// <summary>Megahertz.</summary>
    Megahertz,

    /// <summary>Milliseconds.</summary>
    Millisecond
}
