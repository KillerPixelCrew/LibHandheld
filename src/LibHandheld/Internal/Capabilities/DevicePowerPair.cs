namespace LibHandheld.Internal.Capabilities;

/// <summary>Validation for optional coordinated sustained/boost power limits and the commands that write them.</summary>
internal static class DevicePowerPair
{
    /// <summary>Finds the other limit of the declared pair a capability belongs to.</summary>
    /// <param name="descriptors">The current capability descriptors.</param>
    /// <param name="capabilityId">The sustained or boost limit.</param>
    /// <returns>The pair's other descriptor, or null when the capability is in no declared pair.</returns>
    public static CapabilityDescriptor? Peer(IReadOnlyList<CapabilityDescriptor> descriptors, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(capabilityId);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.InstanceId is not null || descriptor.PairedPowerLimitId is not { } boostId)
            {
                continue;
            }

            if (descriptor.CapabilityId == capabilityId)
            {
                return descriptors.FirstOrDefault(candidate =>
                    candidate.CapabilityId == boostId && candidate.InstanceId is null);
            }

            if (boostId == capabilityId)
            {
                return descriptor;
            }
        }

        return null;
    }

    /// <summary>Reads the sustained and boost wattage a command on either limit of a declared pair asks for.</summary>
    /// <param name="command">A command on the sustained or the boost limit.</param>
    /// <param name="descriptors">The descriptor set the command was admitted against.</param>
    /// <param name="sustained">Sustained power in watts; use only when the method returns true.</param>
    /// <param name="boost">Boost power in watts; use only when the method returns true.</param>
    /// <param name="error">Why the command cannot be written, or null on success.</param>
    /// <returns>
    ///     Whether the command names a limit in a declared pair, carries both wattages, the paired one fits
    ///     its own descriptor, and the sustained limit does not exceed the boost limit. The commanded value
    ///     is checked against its own descriptor by the plugin's ordinary admission.
    /// </returns>
    public static bool TryResolve(
        CapabilityCommand command,
        IReadOnlyList<CapabilityDescriptor> descriptors,
        out int sustained,
        out int boost,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(descriptors);
        sustained = 0;
        boost = 0;
        if (command.InstanceId is not null || Peer(descriptors, command.CapabilityId) is not { } peer)
        {
            error = "The capability is in no declared power pair.";
            return false;
        }

        if (command.RequestedValue?.IntegerValue is not { } requested)
        {
            error = "A power limit command carries no wattage.";
            return false;
        }

        if (command.PairedPowerLimitWatts is not { } paired)
        {
            error = $"A power limit command must carry the wattage of its paired limit '{peer.CapabilityId}'.";
            return false;
        }

        if (!Fits(paired, peer))
        {
            error = $"The paired limit's {paired} W is outside {peer.Minimum}-{peer.Maximum} W in steps of "
                    + $"{peer.Step}.";
            return false;
        }

        (sustained, boost) = peer.Role == CapabilityRole.PowerSlowLimit ? (requested, paired) : (paired, requested);
        if (sustained > boost)
        {
            error = $"The sustained limit ({sustained} W) cannot exceed the boost limit ({boost} W).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Validates every declared pair against the complete descriptor set.</summary>
    /// <param name="descriptors">The current capability descriptors.</param>
    /// <param name="error">The invalid declaration, or null on success.</param>
    /// <returns>True when every declared pair has unique, distinct writable integer watt controls with valid bounds.</returns>
    /// <remarks>
    ///     An empty set or a set without declared pairs is valid. Read support is not required.
    ///     This validates pair structure only, not each descriptor's complete shape or current availability.
    /// </remarks>
    public static bool TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, out string? error)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        error =
            "A power pair requires unique readable and writable sustained/boost watt controls with valid bounds and step.";
        foreach (var primary in descriptors)
        {
            if (primary.PairedPowerLimitId is null)
            {
                continue;
            }

            if (primary.Role != CapabilityRole.PowerSustainedLimit || !IsLimit(primary)
                                                                   || string.IsNullOrWhiteSpace(
                                                                       primary.PairedPowerLimitId))
            {
                return false;
            }

            var peers = descriptors.Where(d => d.CapabilityId == primary.PairedPowerLimitId).ToArray();
            if (peers.Length != 1 || !IsLimit(peers[0]) || peers[0].Role != CapabilityRole.PowerSlowLimit
                || peers[0].PairedPowerLimitId is not null || peers[0].CapabilityId == primary.CapabilityId
                || descriptors.Count(d => d.PairedPowerLimitId == primary.PairedPowerLimitId) != 1)
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Whether a descriptor is a single-instance writable integer watt limit with a range and step.</summary>
    /// <param name="descriptor">Capability declaration to check without reading hardware.</param>
    /// <returns>True for an uninstanced writable integer-watt control with positive minimum/step and ordered bounds.</returns>
    internal static bool IsLimit(CapabilityDescriptor descriptor)
    {
        return descriptor is
               {
                   InstanceId: null,
                   SupportsWrite: true,
                   ValueKind: CapabilityValueKind.Integer,
                   Unit: CapabilityUnit.Watt,
                   Minimum: > 0,
                   Step: > 0
               }
               && descriptor.Maximum >= descriptor.Minimum;
    }

    /// <summary>Whether a wattage is inside a limit's range and on its step.</summary>
    /// <param name="watts">Requested whole-watt value.</param>
    /// <param name="descriptor">Bounded limit descriptor; a supplied step must be positive.</param>
    /// <returns>Whether the value lies within both bounds and aligns to the step from the minimum.</returns>
    internal static bool Fits(int watts, CapabilityDescriptor descriptor)
    {
        return watts >= descriptor.Minimum && watts <= descriptor.Maximum
                                           && (descriptor.Step is not { } step
                                               || (watts - descriptor.Minimum!.Value) % step == 0);
    }
}
