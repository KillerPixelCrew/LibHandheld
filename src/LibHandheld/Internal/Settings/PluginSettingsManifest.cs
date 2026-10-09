using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Settings;

/// <summary>
///     One declared plugin setting: a preference WSGM stores and hands back, not a hardware control.
/// </summary>
/// <remarks>
///     The distinction from <see cref="CapabilityDescriptor" /> is what decides where a control lives and
///     is not a judgement call. Changing a setting configures how the plugin behaves and WSGM keeps the
///     value; changing a capability writes hardware state and the device keeps it. A control that writes
///     to the device when the user moves it is a capability, however much it reads like a preference.
/// </remarks>
internal sealed record PluginSettingDescriptor
{
    /// <summary>Stable identifier, for example <c>ec.poll-interval</c>.</summary>
    public required string SettingId { get; init; }

    /// <summary>The shape of the value, which decides the control WSGM draws.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public required CapabilityValueKind ValueKind { get; init; }

    /// <summary>How WSGM labels it.</summary>
    public required CapabilityDisplay Display { get; init; }

    /// <summary>The value used before the user chooses one, and after an invalid stored value.</summary>
    public required CapabilityValue Default { get; init; }

    /// <summary>
    ///     Which declared section this belongs to. An unknown or absent section places the setting in a
    ///     WSGM-owned fallback rather than dropping it.
    /// </summary>
    public string? SectionId { get; init; }

    /// <summary>Placement within its section. Ties break on declaration order.</summary>
    public int SortOrder { get; init; }

    /// <summary>Inclusive minimum for an integer setting.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive maximum for an integer setting.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values.</summary>
    public int? Step { get; init; }

    /// <summary>Unit of a numeric value.</summary>
    /// <remarks>Undefined numeric enum values are rejected by <see cref="TryValidate" />.</remarks>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>
    ///     Legal options for a choice setting. The collection, its items, and every item's display
    ///     metadata must be non-null.
    /// </summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>Longest accepted value for a text setting. Required for text, rejected otherwise.</summary>
    public int? MaximumLength { get; init; }

    /// <summary>
    ///     Whether this setting is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the setting is safe to render and store.</returns>
    public bool TryValidate(out string? error)
    {
        if (!PlainText.IsIdentifier(SettingId))
        {
            error = $"settingId '{SettingId}' is not a legal identifier.";
            return false;
        }

        if (SectionId is not null
            && !PlainText.IsIdentifier(SectionId))
        {
            error = $"setting '{SettingId}' names a sectionId that is not a legal identifier.";
            return false;
        }

        if (!Enum.IsDefined(ValueKind))
        {
            error = $"setting '{SettingId}' has an undefined valueKind '{ValueKind}'.";
            return false;
        }

        if (!Enum.IsDefined(Unit))
        {
            error = $"setting '{SettingId}' has an undefined unit '{Unit}'.";
            return false;
        }

        if (Display is null)
        {
            error = $"setting '{SettingId}' has no display metadata.";
            return false;
        }

        if (!Display.TryValidate(out var displayError))
        {
            error = $"setting '{SettingId}' has invalid display metadata: {displayError}";
            return false;
        }

        if (Choices is null)
        {
            error = $"setting '{SettingId}' has no choices collection.";
            return false;
        }

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (ValueKind)
        {
            // A setting is something the user changes, so a value shape that cannot be changed or has
            // no value at all has nothing to draw.
            case CapabilityValueKind.None:
                error = $"setting '{SettingId}' must carry a value; use a capability for an action.";
                return false;
            // A curve is authored, not toggled: it belongs to a named profile with its own storage and
            // its own editor, so letting one masquerade as a preference would give curves two homes.
            case CapabilityValueKind.Curve:
                error = $"setting '{SettingId}' may not be a curve; declare a profile instead.";
                return false;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (ValueKind is CapabilityValueKind.Integer
            && (Minimum is null || Maximum is null || Minimum > Maximum || Step is <= 0))
        {
            error = $"setting '{SettingId}' needs a minimum, a maximum, and a positive step.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Choice
            && Choices.Count is 0)
        {
            error = $"setting '{SettingId}' has an empty choices collection.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Choice)
        {
            var choiceValues = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < Choices.Count; index++)
            {
                var choice = Choices[index];
                if (choice is null)
                {
                    error = $"setting '{SettingId}' has a null choice at index {index}.";
                    return false;
                }

                if (!PlainText.IsIdentifier(choice.Value))
                {
                    error = $"setting '{SettingId}' has an invalid choice value at index {index}.";
                    return false;
                }

                if (!choiceValues.Add(choice.Value))
                {
                    error = $"setting '{SettingId}' declares choice '{choice.Value}' more than once.";
                    return false;
                }

                if (choice.Display is null)
                {
                    error = $"setting '{SettingId}' choice '{choice.Value}' has no display metadata.";
                    return false;
                }

                if (choice.Display.TryValidate(out var choiceDisplayError))
                {
                    continue;
                }

                error =
                    $"setting '{SettingId}' choice '{choice.Value}' has invalid display metadata: {choiceDisplayError}";
                return false;
            }
        }

        if (ValueKind is not CapabilityValueKind.Choice && Choices.Count != 0)
        {
            error = $"setting '{SettingId}' may not carry choices.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Text && MaximumLength is not > 0)
        {
            error = $"setting '{SettingId}' needs a positive maximumLength.";
            return false;
        }

        if (ValueKind is not CapabilityValueKind.Text && MaximumLength is not null)
        {
            error = $"setting '{SettingId}' may not carry a maximumLength.";
            return false;
        }

        if (Default is null || Default.Kind != ValueKind)
        {
            error = $"setting '{SettingId}' has a default of the wrong value kind.";
            return false;
        }

        if (!TryValidateValue(Default, out var defaultError))
        {
            error = $"setting '{SettingId}' has an invalid default: {defaultError}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Whether a value satisfies what this setting currently declares.
    /// </summary>
    /// <param name="value">The value, typically one restored from configuration.</param>
    /// <param name="error">Why it does not, naming the value and the declared bound.</param>
    /// <returns><see langword="true" /> when the value is usable as-is.</returns>
    /// <remarks>
    ///     Separate from <see cref="TryValidate" /> because the two answer different questions at
    ///     different times: whether the plugin's declaration is coherent, and whether a value stored
    ///     under a possibly older declaration still fits the current one.
    /// </remarks>
    public bool TryValidateValue(CapabilityValue? value, out string? error)
    {
        if (!Enum.IsDefined(ValueKind))
        {
            error = $"declared value kind {ValueKind} is undefined.";
            return false;
        }

        if (value is null)
        {
            error = $"value kind none does not match {ValueKind}.";
            return false;
        }

        if (!Enum.IsDefined(value.Kind))
        {
            error = $"value kind {value.Kind} is undefined.";
            return false;
        }

        if (value.Kind != ValueKind)
        {
            error = $"value kind {value.Kind} does not match {ValueKind}.";
            return false;
        }

        switch (ValueKind)
        {
            case CapabilityValueKind.Boolean when value.BooleanValue is null:
                error = "boolean value is missing.";
                return false;

            case CapabilityValueKind.Integer:
                if (value.IntegerValue is not { } integer)
                {
                    error = "integer value is missing.";
                    return false;
                }

                if (integer < Minimum || integer > Maximum)
                {
                    error = $"{integer} is outside the declared range {Minimum}-{Maximum}.";
                    return false;
                }

                if (Step is > 0
                    && ((long)integer - (Minimum ?? 0)) % Step.Value != 0)
                {
                    error = $"{integer} is not on the declared step of {Step}.";
                    return false;
                }

                break;

            case CapabilityValueKind.Choice:
                if (value.ChoiceValue is not { Length: > 0 } choice)
                {
                    error = "choice value is missing.";
                    return false;
                }

                if (Choices is null)
                {
                    error = "declared choices collection is missing.";
                    return false;
                }

                if (Choices.Any(item => item is null))
                {
                    error = "declared choices collection contains a null item.";
                    return false;
                }

                if (!Choices.Any(item => string.Equals(
                        item.Value,
                        choice,
                        StringComparison.Ordinal)))
                {
                    error = $"'{choice}' is not one of the {Choices.Count} declared options.";
                    return false;
                }

                break;

            case CapabilityValueKind.Color when value.ColorValue is not (>= 0 and <= 0xFFFFFF):
                error = "colour value is missing or outside 24-bit RGB.";
                return false;

            case CapabilityValueKind.Text:
                return PlainText.TryValidate(
                    value.TextValue,
                    MaximumLength ?? 0,
                    $"setting '{SettingId}' text",
                    out error
                );

            case CapabilityValueKind.None:
            case CapabilityValueKind.Curve:
            default:
                break;
        }

        error = null;
        return true;
    }
}

/// <summary>
///     Everything a plugin declares for its own settings page: the sections, and the settings placed in
///     them. The plugin ships no UI — WSGM draws, validates, stores, and localizes all of it.
/// </summary>
internal sealed record PluginSettingsManifest
{
    /// <summary>The declared sections, in the order they were written.</summary>
    /// <remarks>A null collection or item is an invalid manifest, including after deserialization.</remarks>
    public IReadOnlyList<PluginSettingSection> Sections { get; init; } = [];

    /// <summary>The declared settings, in the order they were written.</summary>
    /// <remarks>A null collection or item is an invalid manifest, including after deserialization.</remarks>
    public IReadOnlyList<PluginSettingDescriptor> Settings { get; init; } = [];

    /// <summary>
    ///     Whether the whole manifest is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when every section and setting is safe.</returns>
    /// <remarks>
    ///     A setting naming an unknown section is deliberately <em>not</em> an error: it renders in a
    ///     WSGM-owned fallback section and the placement is logged. Dropping it silently is the one
    ///     outcome that leaves the user with a missing control and no way to find out why.
    /// </remarks>
    public bool TryValidate(out string? error)
    {
        if (Sections is null)
        {
            error = "manifest has no sections collection.";
            return false;
        }

        if (Settings is null)
        {
            error = "manifest has no settings collection.";
            return false;
        }

        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Sections.Count; index++)
        {
            var section = Sections[index];
            if (section is null)
            {
                error = $"manifest has a null section at index {index}.";
                return false;
            }

            if (!section.TryValidate(out error))
            {
                return false;
            }

            if (sectionIds.Add(section.SectionId))
            {
                continue;
            }

            error = $"manifest declares section '{section.SectionId}' more than once.";
            return false;
        }

        var settingIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Settings.Count; index++)
        {
            var setting = Settings[index];
            if (setting is null)
            {
                error = $"manifest has a null setting at index {index}.";
                return false;
            }

            if (!setting.TryValidate(out error))
            {
                return false;
            }

            if (settingIds.Add(setting.SettingId))
            {
                continue;
            }

            error = $"manifest declares setting '{setting.SettingId}' more than once.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>One validated effective value passed back to a plugin.</summary>
/// <param name="SettingId">Declared setting identifier.</param>
/// <param name="Value">Value validated against the current declaration.</param>
internal readonly record struct DeviceSettingValue(string SettingId, CapabilityValue Value);
