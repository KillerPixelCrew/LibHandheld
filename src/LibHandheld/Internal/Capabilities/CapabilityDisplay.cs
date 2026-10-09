using System.Text.Json.Serialization;

namespace LibHandheld.Internal.Capabilities;

/// <summary>
///     How a capability is labelled in WSGM's own surfaces.
/// </summary>
/// <remarks>
///     Presentation is WSGM-owned. A plugin selects a display key from a WSGM-defined set, which WSGM
///     then localizes; it does not supply the words. That is what keeps every device speaking the same
///     language in the overlay and the native QAM, and it is why a plugin cannot ship markup,
///     formatting, localization resources, or anything executable through this path.
///     <para>
///         <see cref="CustomLabel" /> is the escape hatch for a genuinely device-specific control that no key
///         covers, such as an unusual vendor toggle. It is plugin-supplied plain text, never a format string,
///         markup, or localization key.
///     </para>
/// </remarks>
internal sealed record CapabilityDisplay
{
    /// <summary>The WSGM-owned display key, or <see cref="DisplayKey.Custom" />.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public required DisplayKey Key { get; init; }

    /// <summary>
    ///     Plugin-supplied label, used only when <see cref="Key" /> is
    ///     <see cref="DisplayKey.Custom" />. Not localized: WSGM cannot translate text it did not author.
    /// </summary>
    public string? CustomLabel { get; init; }

    /// <summary>
    ///     Whether this display metadata is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the label is safe to render.</returns>
    public bool TryValidate(out string? error)
    {
        if (!Enum.IsDefined(Key))
        {
            error = $"display key '{Key}' is not defined.";
            return false;
        }

        if (Key is not DisplayKey.Custom)
        {
            // A label alongside a real key would be dead weight that some surface eventually renders
            // instead of the localized string.
            if (CustomLabel is not null)
            {
                error = "customLabel is only permitted when key is Custom.";
                return false;
            }

            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(CustomLabel))
        {
            return PlainText.TryValidate(CustomLabel, "customLabel", out error);
        }

        error = "key Custom requires a customLabel.";
        return false;
    }
}

/// <summary>
///     The WSGM-owned vocabulary of capability labels.
/// </summary>
/// <remarks>
///     Adding a key is a WSGM change with a localized string behind it, not something a package can do.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayKey>))]
internal enum DisplayKey
{
    /// <summary>Use <see cref="CapabilityDisplay.CustomLabel" /> as plugin text.</summary>
    Custom,

    /// <summary>"TDP".</summary>
    Tdp,

    /// <summary>"Sustained power limit".</summary>
    SustainedPowerLimit,

    /// <summary>"Boost power limit".</summary>
    BoostPowerLimit,

    /// <summary>"Performance profile".</summary>
    PerformanceProfile,

    /// <summary>"Fan mode".</summary>
    FanMode,

    /// <summary>"Fan speed".</summary>
    FanSpeed,

    /// <summary>"Fan curve".</summary>
    FanCurve,

    /// <summary>"Left fan".</summary>
    FanLeft,

    /// <summary>"Right fan".</summary>
    FanRight,

    /// <summary>"Charge limit".</summary>
    ChargeLimit,

    /// <summary>"Bypass charging".</summary>
    BypassCharging,

    /// <summary>"Lighting".</summary>
    Lighting,

    /// <summary>"Brightness".</summary>
    Brightness,

    /// <summary>"Effect".</summary>
    LightingEffect,

    /// <summary>"Effect speed".</summary>
    LightingEffectSpeed,

    /// <summary>"CPU temperature".</summary>
    CpuTemperature,

    /// <summary>"Battery".</summary>
    Battery,

    /// <summary>"Controller".</summary>
    Controller,

    /// <summary>"Motion".</summary>
    Motion,

    /// <summary>"Rumble".</summary>
    Rumble,

    /// <summary>"Variable refresh rate".</summary>
    VariableRefreshRate
}
