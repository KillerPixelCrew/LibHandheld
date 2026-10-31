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
            _ => char.ToUpperInvariant(value[0]) + value[1..].Replace('-', ' ')
        };
    }

    internal static CapabilityDescriptor Color(string id, string? instance, CapabilityDisplay display, bool read,
        CapabilityPersistence persistence)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.LightingZoneColor, Display = display,
            ValueKind = CapabilityValueKind.Color, SupportsRead = read, SupportsWrite = true, Persistence = persistence
        };
    }

    /// <summary>
    ///     A writable fan curve whose duties run from 0 to 100 percent; its inputs are the editor's starting
    ///     temperatures, a template and never an observed value.
    /// </summary>
    internal static CapabilityDescriptor Curve(string id, CapabilityDisplay display, IReadOnlyList<int> inputs,
        bool read, CapabilityPersistence persistence = CapabilityPersistence.Volatile, string? instance = null)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve, CurveInputs = inputs, Display = display, Minimum = 0,
            Maximum = 100, Unit = CapabilityUnit.Percent, SupportsRead = read, SupportsWrite = true,
            Persistence = persistence
        };
    }

    /// <summary>A capability that is invoked rather than read or written, such as a rumble sink.</summary>
    internal static CapabilityDescriptor Action(string id, CapabilityRole role, CapabilityDisplay display,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.None, Display = display,
            SupportsAction = true, Persistence = persistence
        };
    }

    /// <summary>An OEM control labelled with its printed name.</summary>
    internal static OemControlDescriptor OemControl(string id, string label, OemControlPlacement placement,
        bool requiresController = true, CanonicalButtons button = CanonicalButtons.None)
    {
        return new OemControlDescriptor
        {
            ControlId = id, Display = Label(label), Placement = placement,
            RequiresControllerAcquisition = requiresController, ControllerButton = button
        };
    }

    /// <summary>Writes a 0xRRGGBB colour as its red, green and blue bytes.</summary>
    internal static void WriteRgb(Span<byte> target, int rgb)
    {
        target[0] = (byte)(rgb >> 16);
        target[1] = (byte)(rgb >> 8);
        target[2] = (byte)rgb;
    }
}
