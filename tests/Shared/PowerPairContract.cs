using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Testing;

/// <summary>The shape a published sustained/boost power pair must have, which the host checks on its side.</summary>
internal static class PowerPairContract
{
    /// <summary>Whether every declared pair names unique, distinct writable integer watt controls with valid bounds.</summary>
    /// <remarks>An empty set or a set without declared pairs is valid; read support is not required.</remarks>
    internal static bool IsValid(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        foreach (var primary in descriptors)
        {
            if (primary.PairedPowerLimitId is null)
            {
                continue;
            }

            if (primary.Role != CapabilityRole.PowerSustainedLimit || !DevicePowerPair.IsLimit(primary)
                                                                   || string.IsNullOrWhiteSpace(
                                                                       primary.PairedPowerLimitId))
            {
                return false;
            }

            var peers = descriptors.Where(d => d.CapabilityId == primary.PairedPowerLimitId).ToArray();
            if (peers.Length != 1 || !DevicePowerPair.IsLimit(peers[0])
                                  || peers[0].Role != CapabilityRole.PowerSlowLimit
                                  || peers[0].PairedPowerLimitId is not null
                                  || peers[0].CapabilityId == primary.CapabilityId
                                  || descriptors.Count(d => d.PairedPowerLimitId == primary.PairedPowerLimitId) != 1)
            {
                return false;
            }
        }

        return true;
    }
}
