using LibHandheld.Contracts;
using LibHandheld.Internal.Lifecycle;

namespace LibHandheld.Internal.Capabilities;

/// <summary>The checks every native command passes before its family touches hardware.</summary>
/// <remarks>
///     This is the one write-budget check a command gets before dispatch: a deadline that leaves less than
///     <see cref="DeviceWriteBudget.Minimum" /> is refused here as a retryable <see cref="CapabilityReasonCode.Quiescing" />.
/// </remarks>
internal static class CommandAdmission
{
    internal static CapabilityReason? Validate(CapabilityCommand command, CapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityDescriptor> descriptors, bool? onAcPower = null)
    {
        if (!DeviceWriteBudget.IsAvailable(command.Deadline))
        {
            return new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The command deadline leaves too little time for a native write.", true);
        }

        if (onAcPower is { } ac && (ac ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc))
        {
            return new CapabilityReason(CapabilityReasonCode.UnavailableOnPowerSource,
                "The control is unavailable on the current power source.");
        }

        if (command.RequestedValue is not { } value)
        {
            return descriptor.SupportsAction
                ? null
                : new CapabilityReason(CapabilityReasonCode.Unsupported, "The control is not an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "The control is read-only.");
        }

        if (!CapabilityValueValidation.ValueMatches(value, descriptor, out var error))
        {
            return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, error);
        }

        return DevicePowerPair.Peer(descriptors, command.CapabilityId) is not null
               && !DevicePowerPair.TryResolve(command, descriptors, out _, out _, out var pairError)
            ? new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, pairError)
            : null;
    }
}
