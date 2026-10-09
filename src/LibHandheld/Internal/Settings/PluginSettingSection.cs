using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Settings;

/// <summary>
///     How a settings section is titled on WSGM's own surfaces.
/// </summary>
/// <remarks>
///     The same ownership split as <see cref="DisplayKey" />, one level up: a plugin selects a key WSGM
///     localizes, or supplies plain text through <see cref="Custom" />. Sections use the same
///     text contract as the labels inside them.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SettingSectionKey>))]
internal enum SettingSectionKey
{
    /// <summary>Use <see cref="PluginSettingSection.CustomTitle" /> as plugin text.</summary>
    Custom,

    /// <summary>"General".</summary>
    General,

    /// <summary>"Power".</summary>
    Power,

    /// <summary>"Fans".</summary>
    Fans,

    /// <summary>"Lighting".</summary>
    Lighting,

    /// <summary>"Controller".</summary>
    Controller,

    /// <summary>"Display".</summary>
    Display,

    /// <summary>"Advanced".</summary>
    Advanced,

    /// <summary>"Diagnostics".</summary>
    Diagnostics
}

/// <summary>
///     One declared group of plugin settings. A plugin chooses placement among WSGM's sections and the
///     order within them; it never supplies layout.
/// </summary>
internal sealed record PluginSettingSection
{
    /// <summary>Stable identifier settings reference, for example <c>power.advanced</c>.</summary>
    public required string SectionId { get; init; }

    /// <summary>The WSGM-owned title key, or <see cref="SettingSectionKey.Custom" />.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public required SettingSectionKey Key { get; init; }

    /// <summary>
    ///     Plugin-supplied title, used only when <see cref="Key" /> is
    ///     <see cref="SettingSectionKey.Custom" />. Not localized: WSGM cannot translate text it did not
    ///     author.
    /// </summary>
    public string? CustomTitle { get; init; }

    /// <summary>
    ///     Placement among the other sections. Ties break on declaration order, so a manifest that
    ///     orders nothing still renders deterministically.
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>
    ///     Whether this section is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the section is safe to render.</returns>
    public bool TryValidate(out string? error)
    {
        if (!PlainText.IsIdentifier(SectionId))
        {
            error = $"sectionId '{SectionId}' is not a legal identifier.";
            return false;
        }

        if (!Enum.IsDefined(Key))
        {
            error = $"section '{SectionId}' has an undefined key '{Key}'.";
            return false;
        }

        if (Key is SettingSectionKey.Custom)
        {
            return PlainText.TryValidate(
                CustomTitle,
                $"section '{SectionId}' customTitle",
                out error
            );
        }

        // A title alongside a real key is dead weight that some surface eventually renders
        // instead of the localized string.
        if (CustomTitle is not null)
        {
            error = $"section '{SectionId}' may only carry a customTitle when key is Custom.";
            return false;
        }

        error = null;
        return true;
    }
}
