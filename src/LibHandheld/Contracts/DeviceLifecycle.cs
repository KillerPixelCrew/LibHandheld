namespace LibHandheld.Contracts;

/// <summary>A supported exact model and its stable family and glyph identities.</summary>
/// <param name="Id">Stable model definition identity.</param>
/// <param name="FamilyId">Stable family identity, also identifying existing recovery state.</param>
/// <param name="Name">Plain model name.</param>
/// <param name="GlyphProfileId">Authored controller glyph profile identity.</param>
public sealed record HandheldDefinition(string Id, string FamilyId, string Name, string GlyphProfileId);

/// <summary>Aggregate state of native device services.</summary>
public enum DeviceOperationalState
{
    /// <summary>No mutable service acquired.</summary>
    Passive,

    /// <summary>All required services started.</summary>
    Active,

    /// <summary>Some services remain unavailable.</summary>
    Degraded
}

/// <summary>Result of native service acquisition.</summary>
/// <param name="State">Aggregate native state.</param>
/// <param name="Reason">Optional diagnostic explanation.</param>
public sealed record DeviceStartResult(DeviceOperationalState State, CapabilityReason? Reason);

/// <summary>Aggregate restoration evidence.</summary>
public enum DeviceStopStatus
{
    /// <summary>All changed temporary state restored and verified.</summary>
    Clean,

    /// <summary>Cleanup completed without full independent confirmation.</summary>
    Unverified,

    /// <summary>A restoration failed.</summary>
    Failed
}

/// <summary>Result of terminal native cleanup.</summary>
/// <param name="Status">Restoration evidence.</param>
/// <param name="Reason">Optional diagnostic explanation.</param>
public sealed record DeviceStopResult(DeviceStopStatus Status, CapabilityReason? Reason);

/// <summary>Reason for ending a device cycle.</summary>
public enum DeviceStopReason
{
    /// <summary>The consumer is exiting.</summary>
    Exiting,

    /// <summary>Device integration disabled.</summary>
    IntegrationDisabled,

    /// <summary>The consumer is updating.</summary>
    Updating,

    /// <summary>The interactive session ends.</summary>
    SessionEnding,

    /// <summary>The consumer is uninstalling.</summary>
    Uninstalling,

    /// <summary>Startup owner cancelled.</summary>
    StartCanceled,

    /// <summary>Startup failed.</summary>
    StartFailed,

    /// <summary>A native background service failed.</summary>
    RuntimeFault
}

/// <summary>Diagnostic importance.</summary>
public enum DeviceLogLevel
{
    /// <summary>Normal transition.</summary>
    Info,

    /// <summary>Degraded operation.</summary>
    Warn,

    /// <summary>Operation or service failed.</summary>
    Error,

    /// <summary>Verbose developer detail.</summary>
    Debug
}

/// <summary>One native diagnostic transition. Never produced for each input sample.</summary>
/// <param name="Level">Importance.</param>
/// <param name="Scope">Native subsystem.</param>
/// <param name="Message">Plain diagnostic detail.</param>
/// <param name="ChangeKey">Optional key whose unchanged repeats may be suppressed.</param>
public sealed record DeviceLogEntry(DeviceLogLevel Level, string Scope, string Message, string? ChangeKey = null);

/// <summary>Typed publication consumer; callbacks acknowledge dispatch, not downstream delivery.</summary>
/// <remarks>
///     Collections are detached replacements. Controller samples are complete value types; do not log or allocate per
///     sample.
/// </remarks>
public interface IHandheldObserver
{
    /// <summary>Accepts a complete descriptor replacement.</summary>
    ValueTask DescriptorsChangedAsync(CapabilityDescriptorSet descriptors, CancellationToken cancellationToken);

    /// <summary>Accepts one observed control state.</summary>
    ValueTask StateChangedAsync(CapabilityState state, CancellationToken cancellationToken);

    /// <summary>Accepts a complete physical interface replacement and its haptic mechanics.</summary>
    ValueTask PhysicalDevicesChangedAsync(IReadOnlyList<PhysicalDeviceIdentity> devices, HapticCapabilities? output,
        CancellationToken cancellationToken);

    /// <summary>Accepts complete canonical physical input and motion.</summary>
    ValueTask ControllerSampleAsync(CanonicalControllerSample sample, CancellationToken cancellationToken);

    /// <summary>Accepts a complete assignable OEM control replacement.</summary>
    ValueTask OemControlsChangedAsync(IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken);

    /// <summary>Accepts a deduplicated OEM press or release.</summary>
    ValueTask OemEventAsync(OemControlEvent controlEvent, CancellationToken cancellationToken);

    /// <summary>Reports a background fault invalidating this device cycle.</summary>
    void Faulted(string scope, string message);
}
