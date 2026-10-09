using System.Text.Json.Serialization;

namespace LibHandheld.Internal.Capabilities;

/// <summary>Closed host-rendered emphasis for a capability, independent of its value and write semantics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityProminence>))]
internal enum CapabilityProminence
{
    /// <summary>Ordinary control with its normal editor.</summary>
    Normal,

    /// <summary>A principal control that benefits from the full available group width.</summary>
    Primary,

    /// <summary>A compact reading or secondary control; accessibility and input remain host-owned.</summary>
    Compact
}

/// <summary>Identifies one companion capability in the same explicitly assigned section and category.</summary>
/// <param name="CapabilityId">Companion's stable capability identifier.</param>
/// <param name="InstanceId">Companion's optional instance discriminator.</param>
internal sealed record CapabilityLayoutPair(string CapabilityId, string? InstanceId = null);

/// <summary>Validates markup-free descriptor presentation hints as part of a complete publication.</summary>
internal static class CapabilityLayout
{
    /// <summary>Checks closed prominence values and exact, non-self, same-group pairing references.</summary>
    /// <param name="descriptors">Complete descriptor publication.</param>
    /// <param name="error">Validation failure, or null on success.</param>
    /// <returns>Whether all hints are safe for the host to consume.</returns>
    public static bool TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, out string? error)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        foreach (var descriptor in descriptors)
        {
            if (!Enum.IsDefined(descriptor.Prominence))
            {
                error = $"Capability '{descriptor.CapabilityId}' has undefined prominence.";
                return false;
            }

            if (descriptor.LayoutPair is not { } pair)
            {
                continue;
            }

            var matches = descriptors.Where(candidate => candidate.CapabilityId == pair.CapabilityId
                                                         && candidate.InstanceId == pair.InstanceId).ToArray();
            if (string.IsNullOrWhiteSpace(descriptor.SectionId) || matches.Length != 1
                                                                || ReferenceEquals(matches[0], descriptor)
                                                                || (descriptor.CapabilityId == pair.CapabilityId &&
                                                                    descriptor.InstanceId == pair.InstanceId)
                                                                || matches[0].SectionId != descriptor.SectionId ||
                                                                matches[0].CategoryId != descriptor.CategoryId)
            {
                error =
                    $"Capability '{descriptor.CapabilityId}' must pair with one different capability in the same explicitly assigned group.";
                return false;
            }
        }

        error = null;
        return true;
    }
}
