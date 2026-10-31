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

    /// <summary>A state whose quality follows its value: observed with a value, unknown and untimed without one.</summary>
    internal static CapabilityState State(string id, bool available, CapabilityValue? value,
        DateTimeOffset? observedAt, CapabilityReason? reason = null, string? instance = null)
    {
        return new CapabilityState
        {
            CapabilityId = id, InstanceId = instance, Available = available, ObservedValue = value,
            ObservedAt = value is null ? null : observedAt,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed, Reason = reason
        };
    }

    /// <summary>The state of <paramref name="descriptor" /> from the value and time <paramref name="observed" /> holds for it.</summary>
    internal static CapabilityState Observed(CapabilityDescriptor descriptor, ObservedValues observed, bool available,
        CapabilityReason? reason = null)
    {
        return State(descriptor.CapabilityId, available,
            observed.Value(descriptor.CapabilityId, descriptor.InstanceId),
            observed.Time(descriptor.CapabilityId, descriptor.InstanceId), reason, descriptor.InstanceId);
    }
}
