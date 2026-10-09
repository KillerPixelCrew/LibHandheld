using System.Text.Json.Serialization;

namespace LibHandheld.Internal.Capabilities;

/// <summary>
///     The immutable description of one capability instance: what it is, what values it accepts, and
///     what applying it costs.
/// </summary>
/// <remarks>
///     A descriptor never changes. When firmware, the endpoint set, or dependency health changes what a
///     capability can do, the plugin publishes a complete replacement set under a new
///     <see cref="CapabilityDescriptorSet.Generation" /> and consumers discard everything they cached.
///     Mutating a descriptor in place would let a stale range validate a command the hardware will
///     reject.
///     <para>
///         A descriptor is a description, not a promise. WSGM validates a request against it for UI
///         consistency; the plugin revalidates authoritatively against current firmware and state on every
///         command, because a value that was legal when the descriptor was published may not be legal now.
///     </para>
/// </remarks>
internal sealed record CapabilityDescriptor
{
    /// <summary>Stable capability identifier, for example <c>power.primary-limit</c>.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    ///     Instance discriminator when a device has several of the same capability, such as two fans.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>What this capability means to WSGM.</summary>
    public required CapabilityRole Role { get; init; }

    /// <summary>The shape of its value.</summary>
    public required CapabilityValueKind ValueKind { get; init; }

    /// <summary>How WSGM labels it.</summary>
    public required CapabilityDisplay Display { get; init; }

    /// <summary>
    ///     Which declared section this belongs to: an overlay section declared in the same
    ///     <see cref="CapabilityDescriptorSet.Sections" />, or — for a <c>Generic*</c> role — a
    ///     settings-manifest section.
    /// </summary>
    /// <remarks>
    ///     A section declared in the descriptor set is the plugin authoring its own Device overlay
    ///     surface, and any role may be placed there: the layout ships atomically with the
    ///     capabilities it lays out, and every title and icon in it comes from a WSGM-owned
    ///     vocabulary, so the cross-device consistency <see cref="DisplayKey" /> protects survives the
    ///     placement. A semantic role naming a section the set does not declare is still refused —
    ///     outside a declared layout, a power limit belongs under Power on every device.
    ///     <para>
    ///         A generic role keeps its old latitude: an unknown section falls back to a WSGM-owned group
    ///         rather than dropping the control, because WSGM has nothing better to do with a control it
    ///         has no semantics for than put it where the plugin says.
    ///     </para>
    /// </remarks>
    public string? SectionId { get; init; }

    /// <summary>
    ///     Which category of the declared section this belongs to, or null for the section's
    ///     uncategorised lead group.
    /// </summary>
    /// <remarks>
    ///     Legal only when <see cref="SectionId" /> names a section declared in the same set and that
    ///     section declares the category: a category is a heading on a section's page, so referencing
    ///     one without the page it belongs to would name nothing.
    /// </remarks>
    public string? CategoryId { get; init; }

    /// <summary>Placement within its section and category. Ties break on declaration order.</summary>
    public int SortOrder { get; init; }

    /// <summary>Host-owned emphasis; this never supplies dimensions, styling or markup.</summary>
    public CapabilityProminence Prominence { get; init; } = CapabilityProminence.Normal;

    /// <summary>Optional semantic companion to keep adjacent within this section and category.</summary>
    /// <remarks>Requires an explicit shared SectionId. This metadata never authorizes a coordinated hardware write.</remarks>
    public CapabilityLayoutPair? LayoutPair { get; init; }

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
    ///     <see cref="CapabilityCommand.PairedPowerLimitWatts" />. The host decides the pair: it keeps the
    ///     sustained limit at or below the boost limit, and a unified target moves both to the same wattage
    ///     within the boost descriptor's range. The plugin writes the two values as given and never derives
    ///     one from the other. The host snapshots both observed limits before taking ownership and restores
    ///     the sustained pair followed by the original boost value where those originals are known.
    ///     Both descriptors must be writable integer watt limits with valid bounds and step;
    ///     missing readback does not disable writing.
    /// </remarks>
    public string? PairedPowerLimitId { get; init; }

    /// <summary>Legal options for a choice capability.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>
    ///     Optional device-authored shortcuts on the single-instance sustained power limit.
    ///     Empty by default. Assignment takes a read-only snapshot so later edits to the supplied
    ///     collection cannot change this descriptor. See <see cref="DevicePowerPreset" /> for bounds
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
    ///     Longest accepted value for a <see cref="CapabilityValueKind.Text" /> capability.
    /// </summary>
    /// <remarks>
    ///     Required for text, ignored otherwise. There is no default: a text capability that declared no
    ///     bound would be the one value shape with no limit at all, which is exactly what
    ///     <see cref="PlainText" /> exists to prevent.
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

    /// <summary>How WSGM carries a remembered value from one game to the next.</summary>
    public CapabilityProfileScope ProfileScope { get; init; } = CapabilityProfileScope.Switched;

    /// <summary>When a written value takes effect.</summary>
    public CapabilityApplyTiming ApplyTiming { get; init; } = CapabilityApplyTiming.Immediate;
}

/// <summary>One legal option of a choice capability.</summary>
/// <param name="Value">Stable machine value, used in commands and persisted state.</param>
/// <param name="Display">
///     How WSGM labels the option. It must be non-null and satisfy
///     <see cref="CapabilityDisplay.TryValidate" /> wherever the choice is accepted.
/// </param>
internal sealed record CapabilityChoice(string Value, CapabilityDisplay Display);

/// <summary>How long a written capability value survives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityPersistence>))]
internal enum CapabilityPersistence
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
internal sealed record CapabilityDescriptorSet
{
    /// <summary>
    ///     Monotonic generation. Increments whenever any descriptor changes.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>The device generation these descriptors describe.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>
    ///     The overlay sections this set's descriptors may reference, in declaration order.
    /// </summary>
    /// <remarks>
    ///     Published inside the set so layout and content replace atomically: a capability can never
    ///     reference a section from another generation. An empty list declares no layout, and every
    ///     capability keeps the semantic home WSGM gives its role.
    /// </remarks>
    public IReadOnlyList<CapabilitySection> Sections { get; init; } = [];

    /// <summary>Every capability the device currently offers.</summary>
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; init; } = [];
}
