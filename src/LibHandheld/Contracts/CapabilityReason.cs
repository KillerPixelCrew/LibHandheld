using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     Why a capability is unavailable, degraded, or refused a command.
/// </summary>
/// <remarks>
///     A closed taxonomy rather than a message string, because the UI has to decide what to *do*: offer
///     a retry, point at a missing prerequisite, name the conflicting owner, or say nothing can be done.
///     A free-text reason would force that decision back onto string matching.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityReasonCode>))]
public enum CapabilityReasonCode
{
    /// <summary>The device does not implement this capability.</summary>
    Unsupported,

    /// <summary>A required provider, driver, library, or helper is absent.</summary>
    PrerequisiteMissing,

    /// <summary>Another owner currently holds the resource.</summary>
    ResourceConflict,

    /// <summary>The plugin released the resource, for example when controller management was turned off.</summary>
    ResourceReleased,

    /// <summary>Not available on the current power source.</summary>
    UnavailableOnPowerSource,

    /// <summary>The transport failed and the capability stays faulted until recovery.</summary>
    TransportFaulted,

    /// <summary>The observation expired under the freshness policy.</summary>
    ObservationExpired,

    /// <summary>The plugin runtime is unavailable, so nothing can be observed or commanded.</summary>
    HostUnavailable,

    /// <summary>The firmware is outside the range this implementation was verified against.</summary>
    FirmwareNotVerified,

    /// <summary>The requested value is outside what the hardware currently accepts.</summary>
    ValueOutOfRange,

    /// <summary>The device is quiescing for suspend or shutdown and takes no new work.</summary>
    Quiescing
}

/// <summary>
///     A structured reason, carrying separate user-facing and diagnostic detail.
/// </summary>
/// <remarks>
///     The split is deliberate. <see cref="Detail" /> may name a provider, a conflicting process, or a
///     firmware version — useful in a log, wrong in an overlay tile. WSGM renders the code through its
///     own localized strings and shows the detail only in diagnostics.
/// </remarks>
/// <param name="Code">The stable reason.</param>
/// <param name="Detail">Diagnostic detail, never rendered as the primary user-facing message.</param>
/// <param name="Retryable">Whether retrying the same request could plausibly succeed later.</param>
public sealed record CapabilityReason(
    CapabilityReasonCode Code,
    string? Detail = null,
    bool Retryable = false);
