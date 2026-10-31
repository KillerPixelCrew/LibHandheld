using LibHandheld.Contracts;

namespace LibHandheld.Internal.Capabilities;

/// <summary>Common semantic descriptor construction; models still supply their real names, ranges and support.</summary>
internal static class CapabilityDescriptors
{
    internal static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = text };
    }

    internal static CapabilityDescriptor Integer(string id, CapabilityRole role, CapabilityDisplay display,
        int minimum, int maximum, CapabilityUnit unit, bool read, bool write,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile, int step = 1, string? instance = null)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = role, Display = display,
            ValueKind = CapabilityValueKind.Integer,
            Minimum = minimum, Maximum = maximum, Step = step, Unit = unit, SupportsRead = read,
            SupportsWrite = write, Persistence = persistence
        };
    }

    internal static CapabilityDescriptor Choice(string id, CapabilityRole role, CapabilityDisplay display,
        IReadOnlyList<CapabilityChoice> choices, bool read, bool write,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, Display = display, ValueKind = CapabilityValueKind.Choice,
            Choices = choices, SupportsRead = read, SupportsWrite = write, Persistence = persistence
        };
    }

    /// <summary>Choices labelled by <see cref="ChoiceLabel" />; each value stays the id the protocol and profiles use.</summary>
    internal static IReadOnlyList<CapabilityChoice> Choices(IEnumerable<string> values)
    {
        return Array.AsReadOnly(values.Select(value => new CapabilityChoice(value, Label(ChoiceLabel(value))))
            .ToArray());
    }

    /// <summary>The display name of a choice id, so a surface never shows the raw id.</summary>
    /// <param name="value">A lowercase, hyphenated choice id such as <c>solid</c> or <c>full-speed</c>.</param>
    /// <returns>The id as words with a leading capital ("Solid", "Full speed"), or a named exception.</returns>
    internal static string ChoiceLabel(string value)
    {
        return value switch
        {
            "" => value,
            SourceOwnership.Managed => "Plugin",
            "aok-classic" => "AOKZOE classic",
            "pawnio" => "PawnIO",
            "kx" => "KX",
            _ => char.ToUpperInvariant(value[0]) + value[1..].Replace('-', ' ')
        };
    }

    internal static CapabilityDescriptor Color(string id, string instance, CapabilityDisplay display, bool read,
        CapabilityPersistence persistence)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.LightingZoneColor, Display = display,
            ValueKind = CapabilityValueKind.Color, SupportsRead = read, SupportsWrite = true, Persistence = persistence
        };
    }
}
