using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     The immutable description of one capability instance: what it is, what values it accepts, and
///     what applying it costs.
/// </summary>
/// <remarks>
///     A descriptor never changes. When firmware, the endpoint set, or dependency health changes what a
///     capability can do, the device engine publishes a complete replacement set under a new
///     CapabilityDescriptorSet.Generation and consumers discard everything they cached.
///     Mutating a descriptor in place would let a stale range validate a command the hardware will
///     reject.
///     <para>
///         A descriptor is a description, not a promise. the consumer validates a request against it for UI
///         consistency; the device engine revalidates authoritatively against current firmware and state on every
///         command, because a value that was legal when the descriptor was published may not be legal now.
///     </para>
/// </remarks>
public sealed record CapabilityDescriptor
{
    /// <summary>Plain descriptive name of this hardware control.</summary>
    public required string Label { get; init; }

    /// <summary>Stable capability identifier, for example <c>power.primary-limit</c>.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    ///     Instance discriminator when a device has several of the same capability, such as two fans.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>What this capability means to the consumer.</summary>
    public required CapabilityRole Role { get; init; }

    /// <summary>The shape of its value.</summary>
    public required CapabilityValueKind ValueKind { get; init; }


    /// <summary>Whether the current value can be read back from hardware.</summary>
    public bool SupportsRead { get; init; }

    /// <summary>Whether a new value can be written.</summary>
    public bool SupportsWrite { get; init; }

    /// <summary>Whether the capability can be invoked as a one-shot action.</summary>
    public bool SupportsAction { get; init; }

    /// <summary>Inclusive minimum for an integer capability.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive maximum for an integer capability.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values.</summary>
    public int? Step { get; init; }

    /// <summary>Unit of a numeric value.</summary>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>Optional single-instance boost-limit capability coordinated with this sustained limit.</summary>
    /// <remarks>
    ///     When present, every write to either limit carries the other one in
    ///     CapabilityCommand.PairedPowerLimitWatts. The host decides the pair: it keeps the
    ///     sustained limit at or below the boost limit, and a unified target moves both to the same wattage
    ///     within the boost descriptor's range. The device engine writes the two values as given and never derives
    ///     one from the other. The host snapshots both observed limits before taking ownership and restores
    ///     the sustained pair followed by the original boost value where those originals are known.
    ///     Both descriptors must be writable integer watt limits with valid bounds and step;
    ///     missing readback does not disable writing.
    /// </remarks>
    public string? PairedPowerLimitId { get; init; }

    /// <summary>Legal options for a choice capability.</summary>
    public IReadOnlyList<CapabilityChoice> Choices
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<CapabilityChoice>([.. value]);
        }
    } = [];

    /// <summary>
    ///     Optional device-authored shortcuts on the single-instance sustained power limit.
    ///     Empty by default. Assignment takes a read-only snapshot so later edits to the supplied
    ///     collection cannot change this descriptor. See DevicePowerPreset for bounds
    ///     and host behavior.
    /// </summary>
    public IReadOnlyList<DevicePowerPreset> PowerPresets
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<DevicePowerPreset>([.. value]);
        }
    } = [];

    /// <summary>
    ///     Longest accepted value for a CapabilityValueKind.Text capability.
    /// </summary>
    /// <remarks>
    ///     Required for text, ignored otherwise. There is no default: a text capability that declared no
    ///     bound would be the one value shape with no limit at all, which is exactly what
    ///     PlainText exists to prevent.
    /// </remarks>
    public int? MaximumLength { get; init; }

    /// <summary>Whether the capability is available while running on AC power.</summary>
    public bool AvailableOnAc { get; init; } = true;

    /// <summary>
    ///     Whether the capability is available while running on battery.
    /// </summary>
    /// <remarks>
    ///     AC/DC is a descriptor field, not a descriptor generation: the power source changes constantly
    ///     and republishing every descriptor on each transition would invalidate caches for no reason.
    ///     The live condition is reported through capability state instead.
    /// </remarks>
    public bool AvailableOnDc { get; init; } = true;

    /// <summary>How long a written value survives.</summary>
    public required CapabilityPersistence Persistence { get; init; }

    /// <summary>How the consumer carries a remembered value from one game to the next.</summary>
    public CapabilityProfileScope ProfileScope { get; init; } = CapabilityProfileScope.Switched;

    /// <summary>When a written value takes effect.</summary>
    public CapabilityApplyTiming ApplyTiming { get; init; } = CapabilityApplyTiming.Immediate;
}

/// <summary>One legal option of a choice capability.</summary>
/// <param name="Value">Stable machine value, used in commands and persisted state.</param>
/// <param name="Label">Plain descriptive choice name.</param>
public sealed record CapabilityChoice(string Value, string Label);

/// <summary>How long a written capability value survives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityPersistence>))]
public enum CapabilityPersistence
{
    /// <summary>Not established. Treated as device-persistent by every safety rule.</summary>
    Unknown,

    /// <summary>Lost when the device loses power.</summary>
    Volatile,

    /// <summary>Stored on the device and survives reboot.</summary>
    DevicePersistent
}

/// <summary>
///     A complete, versioned set of descriptors for one device generation.
/// </summary>
/// <remarks>
///     Descriptors are always published as a whole set. A capability missing from a new set has gone
///     away and its control disappears, rather than lingering as permanently unavailable.
/// </remarks>
public sealed record CapabilityDescriptorSet
{
    /// <summary>
    ///     Monotonic generation. Increments whenever any descriptor changes.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>The device generation these descriptors describe.</summary>
    public required long CycleGeneration { get; init; }


    /// <summary>Every capability the device currently offers.</summary>
    public IReadOnlyList<CapabilityDescriptor> Descriptors
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<CapabilityDescriptor>([.. value]);
        }
    } = [];
}
