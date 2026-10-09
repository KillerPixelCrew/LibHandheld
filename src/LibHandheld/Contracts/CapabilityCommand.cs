using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     A request to change or invoke one capability.
/// </summary>
/// <remarks>
///     Both generations must still match the device engine's current cycle and descriptor set at dispatch.
///     The device engine also validates the value, live service availability and deadline before writing.
///     Rejection does not authorize the device engine to resubmit a command or choose a replacement value.
/// </remarks>
public sealed record CapabilityCommand
{
    /// <summary>Unique identifier for this command, used to correlate the outcome.</summary>
    public required Guid CommandId { get; init; }

    /// <summary>Capability being commanded.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Instance discriminator, matching the descriptor.</summary>
    public string? InstanceId { get; init; }

    /// <summary>The requested value, or null for an action.</summary>
    public CapabilityValue? RequestedValue { get; init; }

    /// <summary>The other limit of a declared power pair, as the host coordinated it with this one.</summary>
    /// <remarks>
    ///     Set on every write to either limit of a pair that a sustained descriptor declares through
    ///     CapabilityDescriptor.PairedPowerLimitId, and null on every other command: a
    ///     sustained command carries the boost wattage, a boost command the sustained wattage. The host owns
    ///     the relationship between the two limits. The device engine checks the pair with
    ///     DevicePowerPair.TryResolve and writes both values as given, in its own firmware
    ///     order. Verified success confirms both limits; CapabilityCommandResult.ReadbackValue
    ///     reports the commanded one.
    /// </remarks>
    public int? PairedPowerLimitWatts { get; init; }

    /// <summary>Descriptor generation this command was authored against.</summary>
    public required long ExpectedDescriptorGeneration { get; init; }

    /// <summary>Device generation this command was authored against.</summary>
    public required long ExpectedCycleGeneration { get; init; }

    /// <summary>When the command stops being worth applying, on the active clock.</summary>
    public required Deadline Deadline { get; init; }
}

/// <summary>
///     How a command finished.
/// </summary>
/// <remarks>
///     TimedOut and Indeterminate do not prove that the write failed to
///     reach hardware. Neither outcome authorizes an automatic retry. AppliedUnverified
///     is a successful transport write, distinct from an uncertain write.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CommandOutcome>))]
public enum CommandOutcome
{
    /// <summary>
    ///     Validated and kept for a later apply; nothing has reached the hardware yet. the consumer returns it for a
    ///     native per-application value saved to the running game's profile, which the driver applies from the
    ///     next sync.
    /// </summary>
    Accepted,

    /// <summary>Written successfully; independent readback was absent, failed, or did not confirm the value.</summary>
    AppliedUnverified,

    /// <summary>Written and confirmed by an independent read.</summary>
    AppliedVerified,

    /// <summary>Refused before anything was written.</summary>
    Rejected,

    /// <summary>The deadline passed. Whether it was applied is unknown.</summary>
    TimedOut,

    /// <summary>Interrupted mid-operation. Whether it was applied is unknown.</summary>
    Indeterminate
}

/// <summary>
///     The result of a capability command.
/// </summary>
public sealed record CapabilityCommandResult
{
    /// <summary>The command this result answers.</summary>
    public required Guid CommandId { get; init; }

    /// <summary>How it finished.</summary>
    public required CommandOutcome Outcome { get; init; }

    /// <summary>Why, when the outcome was not a clean apply.</summary>
    public CapabilityReason? Reason { get; init; }

    /// <summary>
    ///     An independently read value confirming the applied command.
    /// </summary>
    /// <remarks>
    ///     Present for CommandOutcome.AppliedVerified only. For
    ///     CommandOutcome.AppliedUnverified, the device engine publishes the written value as
    ///     observed capability state and leaves this property absent.
    /// </remarks>
    public CapabilityValue? ReadbackValue { get; init; }

    /// <summary>Whether the device engine restored the previous value after a failure.</summary>
    public RollbackResult Rollback { get; init; } = RollbackResult.NotRequired;

    /// <summary>When the command finished, in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>What happened to the previous value after a failed command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RollbackResult>))]
public enum RollbackResult
{
    /// <summary>No command-time rollback was performed; this does not imply that nothing was written.</summary>
    NotRequired,

    /// <summary>The previous value was restored and confirmed by readback.</summary>
    RestoredVerified,

    /// <summary>A restore was written but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>The attempted restore failed; the device engine must report any unresolved recovery obligation.</summary>
    RestoreFailed
}
