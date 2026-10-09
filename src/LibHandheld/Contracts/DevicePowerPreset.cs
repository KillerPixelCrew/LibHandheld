using System.Text.Json.Serialization;

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
    /// <summary>Optional ScenarioMode choice applied before watt limits on AC power.</summary>
    /// <remarks>
    ///     Declare both scenario targets or neither. Choices belong to the single readable,
    ///     writable ScenarioMode descriptor; the host must confirm the current power source.
    /// </remarks>
    public string? ScenarioOnAc { get; init; }

    /// <summary>Optional ScenarioMode choice applied before watt limits on battery power.</summary>
    public string? ScenarioOnDc { get; init; }
}
