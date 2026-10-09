using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Devices;

internal static class ProviderControls
{
    internal static CapabilityDescriptor Integer(string id, CapabilityRole role, DisplayKey label, int minimum,
        int maximum, CapabilityUnit unit, bool write, bool read = true)
    {
        return CapabilityDescriptors.Integer(id, role, new CapabilityDisplay { Key = label }, minimum, maximum, unit,
            read, write);
    }

    internal static CapabilityDescriptor Ownership(string id, CapabilityRole role, DisplayKey label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, Display = new CapabilityDisplay { Key = label },
            ValueKind = CapabilityValueKind.Choice, SupportsRead = true, Persistence = CapabilityPersistence.Volatile,
            Choices = CapabilityDescriptors.Choices(SourceOwnership.Choices)
        };
    }

    internal static CapabilityState State(string id, bool available, CapabilityValue? value,
        DateTimeOffset? observedAt, CapabilityReason? reason = null)
    {
        return new CapabilityState
        {
            CapabilityId = id, Available = available, ObservedValue = value,
            ObservedAt = value is null ? null : observedAt,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed, Reason = reason
        };
    }
}
