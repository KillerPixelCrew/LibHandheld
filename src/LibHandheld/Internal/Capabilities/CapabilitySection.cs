using System.Text.Json.Serialization;
using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Capabilities;

/// <summary>
///     The WSGM-owned vocabulary of section icons for the Device overlay surface.
/// </summary>
/// <remarks>
///     The same ownership split as <see cref="DisplayKey" />: a plugin selects an icon and WSGM draws it
///     with its own artwork. Adding an icon is a WSGM change with a geometry behind it, not something a
///     package can do — which is what keeps a plugin from shipping artwork through this path.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SectionIcon>))]
internal enum SectionIcon
{
    /// <summary>No specific icon; WSGM derives one from the section's title key.</summary>
    None,

    /// <summary>A power symbol.</summary>
    Power,

    /// <summary>A cooling fan.</summary>
    Fan,

    /// <summary>A battery.</summary>
    Battery,

    /// <summary>Lighting.</summary>
    Lighting,

    /// <summary>A game controller.</summary>
    Controller,

    /// <summary>A display panel.</summary>
    Display,

    /// <summary>A gauge or meter.</summary>
    Gauge,

    /// <summary>A wrench.</summary>
    Wrench
}

/// <summary>
///     One titled group of capabilities inside a declared overlay section.
/// </summary>
/// <remarks>
///     A category is a heading within a section's page, not a page of its own. It uses the same
///     title contract as <see cref="PluginSettingSection" />: the plugin selects a
///     <see cref="SettingSectionKey" /> WSGM localizes, or supplies plain text through
///     <see cref="SettingSectionKey.Custom" />.
/// </remarks>
internal sealed record CapabilityCategory
{
    /// <summary>Stable identifier descriptors reference, for example <c>fan.readings</c>.</summary>
    public required string CategoryId { get; init; }

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
    ///     Placement among the other categories of the section. Ties break on declaration order.
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>
    ///     Whether this category is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the category is safe to render.</returns>
    public bool TryValidate(out string? error)
    {
        if (!PlainText.IsIdentifier(CategoryId))
        {
            error = $"categoryId '{CategoryId}' is not a legal identifier.";
            return false;
        }

        if (!Enum.IsDefined(Key))
        {
            error = $"category '{CategoryId}' has an undefined key '{Key}'.";
            return false;
        }

        return CustomTitleRule.TryValidate(Key, CustomTitle, $"category '{CategoryId}'", out error);
    }
}

/// <summary>The custom-title rule sections and categories share.</summary>
internal static class CustomTitleRule
{
    /// <summary>A Custom key carries a plain-text title; any other key carries none.</summary>
    /// <param name="key">The declared key.</param>
    /// <param name="customTitle">The declared custom title.</param>
    /// <param name="owner">The declaration named in the failure message.</param>
    /// <param name="error">The reason it is not valid, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the title fits the key.</returns>
    internal static bool TryValidate(SettingSectionKey key, string? customTitle, string owner, out string? error)
    {
        if (key is SettingSectionKey.Custom)
        {
            return PlainText.TryValidate(customTitle, $"{owner} customTitle", out error);
        }

        // A title alongside a real key is dead weight that some surface eventually renders
        // instead of the localized string.
        if (customTitle is not null)
        {
            error = $"{owner} may only carry a customTitle when key is Custom.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
///     One declared section of the Device overlay surface: a page of capabilities the plugin lays out.
/// </summary>
/// <remarks>
///     Sections are published inside the <see cref="CapabilityDescriptorSet" /> so layout and content
///     replace atomically: a capability can never reference a section from another generation. The
///     plugin chooses custom section placement, order, title key, and icon; shared sections use
///     <see cref="DeviceSections" /> metadata. The plugin never supplies layout, markup, or
///     artwork: titles come from <see cref="SettingSectionKey" /> or plain text, and icons from
///     the closed <see cref="SectionIcon" /> vocabulary, which is what keeps every device speaking the
///     same visual language in the overlay.
/// </remarks>
internal sealed record CapabilitySection
{
    /// <summary>Stable identifier descriptors reference, for example <c>cooling</c>.</summary>
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
    ///     Plugin-supplied one-line description shown on the section's card, or null for
    ///     WSGM's own wording for <see cref="Key" />. Plain text, never markup or a format string.
    /// </summary>
    public string? CustomDescription { get; init; }

    /// <summary>The icon WSGM draws on the section's card.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public SectionIcon Icon { get; init; } = SectionIcon.None;

    /// <summary>
    ///     Placement among the other declared sections. Ties break on declaration order, so a set that
    ///     orders nothing still renders deterministically.
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>The categories capabilities of this section may reference.</summary>
    public IReadOnlyList<CapabilityCategory> Categories { get; init; } = [];

    /// <summary>
    ///     Whether this section and every category in it are usable.
    /// </summary>
    /// <param name="error">The reason they are not, when the result is <see langword="false" />.</param>
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

        if (!Enum.IsDefined(Icon))
        {
            error = $"section '{SectionId}' has an undefined icon '{Icon}'.";
            return false;
        }

        if (!CustomTitleRule.TryValidate(Key, CustomTitle, $"section '{SectionId}'", out error))
        {
            return false;
        }

        if (CustomDescription is not null
            && !PlainText.TryValidate(
                CustomDescription,
                $"section '{SectionId}' customDescription",
                out error))
        {
            return false;
        }

        if (Categories is null)
        {
            error = $"section '{SectionId}' has no categories collection.";
            return false;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var category in Categories)
        {
            if (category is null)
            {
                error = $"section '{SectionId}' contains a null category.";
                return false;
            }

            if (!category.TryValidate(out error))
            {
                return false;
            }

            if (ids.Add(category.CategoryId))
            {
                continue;
            }

            error = $"section '{SectionId}' declares category '{category.CategoryId}' twice.";
            return false;
        }

        error = null;
        return true;
    }
}
