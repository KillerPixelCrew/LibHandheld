using System.Text.Json.Serialization;

namespace LibHandheld.Internal.Capabilities;

/// <summary>
///     What a capability means to WSGM, independent of how any device implements it.
/// </summary>
/// <remarks>
///     The role is the entire basis on which the overlay and the native QAM decide what control to draw
///     and what a value means. WSGM knows "sustained power limit, 8-30 W, step 1"; whether the plugin
///     reaches that through vendor WMI, an EC transaction, AMD SMU, or Intel MMIO is not expressible
///     here and must never become expressible.
///     <para>
///         The generic roles at the end exist so an unusual feature — UMA allocation, a secondary display
///         brightness, USB-C routing, an external-GPU switch — can be exposed without a plugin shipping UI
///         code for it.
///     </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityRole>))]
internal enum CapabilityRole
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
    ///     Usually published by a <c>wsgm.gpu</c> package, because the graphics driver drives the panel's
    ///     adaptive sync: on Intel graphics that is IGCL's Arc Sync. A device package may publish it for a
    ///     panel only it can reach. WSGM projects one of them on Valve's Performance row and never learns
    ///     which driver answered.
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
    GenericReadOnly
}

/// <summary>The shape of a capability's value, which decides how it is rendered and validated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityValueKind>))]
internal enum CapabilityValueKind
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
    ///     <see cref="CapabilityDisplay.CustomLabel" />: a declared maximum length with control and
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
internal enum CapabilityUnit
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

/// <summary>Questions about a <see cref="CapabilityRole" /> that more than one layer asks.</summary>
internal static class CapabilityRoleExtensions
{
    /// <summary>
    ///     Whether the role is one of the <c>Generic*</c> roles — a control WSGM has no semantics for.
    /// </summary>
    /// <param name="role">The role to classify.</param>
    /// <returns><see langword="true" /> for a generic role.</returns>
    /// <remarks>
    ///     The distinction decides what a plugin is allowed to arrange. A semantic role has a home WSGM
    ///     gives it and keeps across every device, so its placement is not the plugin's to choose; a
    ///     generic role has no such home, which is exactly why the plugin may place it.
    ///     <para>
    ///         Written as an explicit list rather than a name-prefix check. A future role named
    ///         <c>GenericPowerLimit</c> would silently become placeable under a prefix rule; adding a role
    ///         must require an explicit decision here.
    ///     </para>
    /// </remarks>
    public static bool IsGeneric(this CapabilityRole role)
    {
        return role is
            CapabilityRole.GenericToggle
            or CapabilityRole.GenericRange
            or CapabilityRole.GenericChoice
            or CapabilityRole.GenericAction
            or CapabilityRole.GenericText
            or CapabilityRole.GenericReadOnly;
    }
}
