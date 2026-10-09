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

    internal static IReadOnlyList<CapabilityChoice> Choices(IEnumerable<string> values)
    {
        return values.Select(value => new CapabilityChoice(value, Label(value))).ToArray();
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
