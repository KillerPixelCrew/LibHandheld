using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     A request to change or invoke one capability.
/// </summary>
/// <remarks>
///     The active owner validates the value, live service availability and deadline before writing.
///     Rejection does not authorize the plugin to resubmit a command or choose a replacement value.
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

    /// <summary>The other member of a consumer-coordinated power limit pair.</summary>
    public int? PairedPowerLimitWatts { get; init; }


    /// <summary>When the command stops being worth applying, on the active clock.</summary>
    public required Deadline Deadline { get; init; }
}

/// <summary>The native outcome of one command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandOutcome>))]
public enum CommandOutcome
{
    /// <summary>
    ///     Validated and kept for a later apply; nothing has reached the hardware yet. WSGM returns it for a
    ///     native per-application value saved to the running game's profile, which the driver applies from the
    ///     next sync.
    /// </summary>
    Accepted,

    /// <summary>The native transport accepted the requested write.</summary>
    Applied,

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

    /// <summary>When the command finished, in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}
