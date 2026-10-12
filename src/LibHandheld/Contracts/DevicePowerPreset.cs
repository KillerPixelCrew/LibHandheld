using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Contracts;

/// <summary>The Windows power mode requested by a device power preset, independent of power plans.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DevicePowerMode>))]
public enum DevicePowerMode
{
    /// <summary>Prefer battery life.</summary>
    BetterBattery,

    /// <summary>Use balanced performance.</summary>
    Balanced,

    /// <summary>Prefer performance.</summary>
    BestPerformance
}

/// <summary>A device-authored shortcut for its sustained/slow power limits and Windows power mode.</summary>
/// <param name="Id">Stable identifier of ASCII letters, digits, dots, underscores or hyphens.</param>
/// <param name="Name">Single-line display name.</param>
/// <param name="SustainedWatts">Target for the descriptor carrying this preset.</param>
/// <param name="SlowWatts">Target for the single-instance PowerSlowLimit capability.</param>
/// <param name="WindowsMode">Windows power mode applied by the host.</param>
/// <remarks>
///     Declare them on a single-instance sustained watt limit. Both targets must fit the
///     current writable descriptors. Presets are shortcuts, never policies to reapply after drift.
///     Hosts derive Custom from observed values; a failed multi-control application may be partial.
/// </remarks>
public sealed record DevicePowerPreset(
    string Id,
    string Name,
    int SustainedWatts,
    int SlowWatts,
    DevicePowerMode WindowsMode)
{
    /// <summary>Optional independent fast-PPT target; when omitted on a three-limit device, use SlowWatts.</summary>
    public int? FastWatts { get; init; }

    /// <summary>Optional ScenarioMode choice applied before watt limits on AC power.</summary>
    /// <remarks>
    ///     Declare both scenario targets or neither. Choices belong to the single readable,
    ///     writable ScenarioMode descriptor; the host must confirm the current power source.
    /// </remarks>
    public string? ScenarioOnAc { get; init; }

    /// <summary>Optional ScenarioMode choice applied before watt limits on battery power.</summary>
    public string? ScenarioOnDc { get; init; }

    /// <summary>Validates all presets against their current power descriptors.</summary>
    /// <param name="descriptors">The complete descriptor set.</param>
    /// <param name="error">The first invalid declaration, if any.</param>
    /// <returns>Whether the declarations are safe to offer.</returns>
    public static bool TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, out string? error)
    {
        error = "Power presets require one readable, writable sustained/slow watt pair with valid targets.";
        foreach (var descriptor in descriptors)
        {
            if (descriptor.PowerPresets is null)
            {
                return false;
            }

            if (descriptor.PowerPresets.Count == 0)
            {
                continue;
            }

            var sustained = descriptors.Where(d => d.Role == CapabilityRole.PowerSustainedLimit).ToArray();
            var slow = descriptors.Where(d => d.Role == CapabilityRole.PowerSlowLimit).ToArray();
            if (sustained.Length != 1 || slow.Length != 1 || sustained[0] != descriptor
                || !DevicePowerPair.IsLimit(descriptor) || !DevicePowerPair.IsLimit(slow[0]))
            {
                return false;
            }

            HashSet<string> ids = new(StringComparer.Ordinal);
            if (descriptor.PowerPresets.Any(preset =>
                    preset is null || !PlainText.IsIdentifier(preset.Id)
                                   || preset.Id == "custom" ||
                                   !ids.Add(preset.Id) || string.IsNullOrWhiteSpace(preset.Name)
                                   || !Enum.IsDefined(preset.WindowsMode) || preset.SustainedWatts > preset.SlowWatts
                                   || !DevicePowerPair.Fits(preset.SustainedWatts, descriptor)
                                   || !DevicePowerPair.Fits(preset.SlowWatts, slow[0])
                                   || !ValidFast(preset, descriptors)
                                   || !ValidScenario(preset, descriptors)))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidFast(DevicePowerPreset preset, IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        var fast = descriptors.Where(descriptor => descriptor.Role == CapabilityRole.PowerFastLimit).ToArray();
        return fast.Length == 0 ? preset.FastWatts is null
            : fast.Length == 1 && DevicePowerPair.IsLimit(fast[0])
                               && DevicePowerPair.Fits(preset.FastWatts ?? preset.SlowWatts, fast[0]);
    }

    private static bool ValidScenario(DevicePowerPreset preset, IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        if (preset.ScenarioOnAc is null && preset.ScenarioOnDc is null)
        {
            return true;
        }

        var scenarios = descriptors.Where(d => d.Role == CapabilityRole.ScenarioMode).ToArray();
        return scenarios is
               [
                   {
                       InstanceId: null,
                       SupportsWrite: true,
                       AvailableOnAc: true,
                       AvailableOnDc: true,
                       ValueKind: CapabilityValueKind.Choice,
                       // ReSharper disable once RedundantAlwaysMatchSubpattern
                       Choices: not null
                   } scenario
               ]
               && !string.IsNullOrEmpty(preset.ScenarioOnAc) && !string.IsNullOrEmpty(preset.ScenarioOnDc)
               && scenario.Choices.Any(choice => choice.Value == preset.ScenarioOnAc)
               && scenario.Choices.Any(choice => choice.Value == preset.ScenarioOnDc);
    }
}
