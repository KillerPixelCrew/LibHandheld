using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Contracts;

/// <summary>
///     A semantic label key or native control's custom label.
/// </summary>
/// <remarks>
///     The consumer owns presentation and localization. Native providers select a semantic key;
///     controls without a shared key supply ordinary text through CustomText.
///     <para>
///         <see cref="CustomText" /> describes a device-specific control that no key
///         covers, such as a vendor toggle. It is ordinary text, never a format string,
///         markup, or localization key.
///     </para>
/// </remarks>
public sealed record CapabilityDisplay
{
    /// <summary>The semantic display key, or <see cref="DisplayKey.Custom" />.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public required DisplayKey Key { get; init; }

    /// <summary>
    ///     Native control label, used only when <see cref="Key" /> is
    ///     <see cref="DisplayKey.Custom" />. The consumer can localize shared keys.
    /// </summary>
    public string? CustomText { get; init; }

    /// <summary>
    ///     Optional ordinary text explaining what invoking or changing the control does, with any key.
    /// </summary>
    /// <remarks>
    ///     For example, an action that clears several settings says which ones and what it cannot restore.
    ///     Like <see cref="CustomText" />, it is never a format string, markup, or localization key.
    /// </remarks>
    public string? Description { get; init; }

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

        if (Description is not null && !PlainText.TryValidate(Description, "description", out error))
        {
            return false;
        }

        if (Key is not DisplayKey.Custom)
        {
            // A label alongside a real key would be dead weight that some surface eventually renders
            // instead of the localized string.
            if (CustomText is not null)
            {
                error = "customLabel is only permitted when key is Custom.";
                return false;
            }

            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(CustomText))
        {
            error = null;
            return true;
        }

        error = "key Custom requires a customLabel.";
        return false;
    }
}

/// <summary>
///     Shared semantic capability labels.
/// </summary>
/// <remarks>
///     Consumers choose localized text for these shared keys.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayKey>))]
public enum DisplayKey
{
    /// <summary>Use <see cref="CapabilityDisplay.CustomText" /> as plugin text.</summary>
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
