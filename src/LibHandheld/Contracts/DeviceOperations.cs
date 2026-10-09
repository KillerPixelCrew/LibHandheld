namespace LibHandheld.Contracts;

/// <summary>Explicit processor power targets. Omitted limits are left unchanged.</summary>
/// <param name="SustainedWatts">Sustained processor power in watts.</param>
/// <param name="SlowWatts">Optional slow-window power in watts.</param>
/// <param name="FastWatts">Optional fast-window power in watts.</param>
/// <param name="PeakWatts">Optional peak power in watts.</param>
/// <remarks>A declared native power pair requires both targets; use SetTdpAsync for one uniform target.</remarks>
public sealed record PowerLimits(
    int SustainedWatts,
    int? SlowWatts = null,
    int? FastWatts = null,
    int? PeakWatts = null);

/// <summary>One actual native outcome from a semantic operation.</summary>
/// <param name="Role">Meaning of the control, independent of its implementation.</param>
/// <param name="InstanceId">Logical fan, zone or other instance, when there is more than one.</param>
/// <param name="Result">Unmodified native command outcome.</param>
public sealed record DeviceControlResult(CapabilityRole Role, string? InstanceId, CapabilityCommandResult Result);

/// <summary>Result of a semantic request, including every completed native command in dispatch order.</summary>
/// <remarks>
///     Validation rejects the entire request before writing. Once dispatch begins, an unsuccessful or uncertain
///     result stops further commands. Earlier outcomes remain visible; no automatic retry or rollback occurs.
/// </remarks>
public sealed record DeviceOperationResult
{
    /// <summary>Aggregate outcome. Verified means every completed command was independently confirmed.</summary>
    public required CommandOutcome Outcome { get; init; }

    /// <summary>Why validation rejected the request or native execution stopped.</summary>
    public CapabilityReason? Reason { get; init; }

    /// <summary>Actual outcomes; empty when the request was refused before any write.</summary>
    public IReadOnlyList<DeviceControlResult> Results
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<DeviceControlResult>([.. value]);
        }
    } = [];

    /// <summary>Whether the complete request was written successfully, with or without independent readback.</summary>
    public bool Applied => Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
}

/// <summary>A currently declared semantic control and its last native observation.</summary>
/// <param name="Descriptor">Detached immutable shape, options and bounds.</param>
/// <param name="State">Current-generation observation, or null when the value has not been reported.</param>
public sealed record DeviceControl(CapabilityDescriptor Descriptor, CapabilityState? State)
{
    /// <summary>Meaning of the control.</summary>
    public CapabilityRole Role => Descriptor.Role;

    /// <summary>Logical instance suitable for fan and lighting selectors.</summary>
    public string? InstanceId => Descriptor.InstanceId;

    /// <summary>The native value; this never substitutes a desired or guessed value.</summary>
    public CapabilityValue? ObservedValue => State?.ObservedValue;
}

/// <summary>One detached view of current device controls, taken under a single publication lock.</summary>
public sealed record DeviceControlSnapshot
{
    /// <summary>Current acquisition cycle.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Current descriptor replacement; zero before descriptors have arrived.</summary>
    public required long DescriptorGeneration { get; init; }

    /// <summary>Declared controls and their matching current observations.</summary>
    public IReadOnlyList<DeviceControl> Controls
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<DeviceControl>([.. value]);
        }
    } = [];

    /// <summary>Last reliable sustained and optional boost observations, or null when sustained power is unknown.</summary>
    public PowerLimits? ObservedPowerLimits => ObservedInteger(CapabilityRole.PowerSustainedLimit) is { } sustained
        ? new PowerLimits(sustained, ObservedInteger(CapabilityRole.PowerSlowLimit),
            ObservedInteger(CapabilityRole.PowerFastLimit), ObservedInteger(CapabilityRole.PowerPeakLimit))
        : null;

    /// <summary>Last reliable battery charge ceiling, or null when unknown.</summary>
    public int? ObservedChargeLimitPercent => ObservedInteger(CapabilityRole.ChargeLimit);

    /// <summary>Last reliable Boolean battery-protection observation, or null for choice controls or unknown state.</summary>
    public bool? ObservedChargeProtectionEnabled =>
        ReliableState(CapabilityRole.ChargeProtectionMode)?.ObservedValue is
            { Kind: CapabilityValueKind.Boolean } value
            ? value.BooleanValue
            : null;

    /// <summary>Last reliable choice battery-protection mode, or null for Boolean controls or unknown state.</summary>
    public string? ObservedChargeProtectionMode =>
        ReliableState(CapabilityRole.ChargeProtectionMode)?.ObservedValue is
            { Kind: CapabilityValueKind.Choice } value
            ? value.ChoiceValue
            : null;

    /// <summary>Last reliable choice bypass-charging mode, or null for Boolean controls or unknown state.</summary>
    public string? ObservedChargeBypassMode =>
        ReliableState(CapabilityRole.ChargeBypass)?.ObservedValue is
            { Kind: CapabilityValueKind.Choice } value
            ? value.ChoiceValue
            : null;

    /// <summary>
    ///     Last reliable bypass-charging observation. Boolean controls report their exact value; known choice
    ///     modes disabled, until-sleep and always report their semantics. Other modes or unknown state return null.
    /// </summary>
    public bool? ObservedChargeBypassEnabled => ReliableState(CapabilityRole.ChargeBypass)?.ObservedValue switch
    {
        { Kind: CapabilityValueKind.Boolean } value => value.BooleanValue,
        { Kind: CapabilityValueKind.Choice, ChoiceValue: "disabled" } => false,
        { Kind: CapabilityValueKind.Choice, ChoiceValue: "until-sleep" or "always" } => true,
        _ => null
    };

    /// <summary>All declared fan controls and measured fan readings, including their logical instance identities.</summary>
    public IReadOnlyList<DeviceControl> Fans => Array.AsReadOnly(Controls.Where(control => control.Role is
        CapabilityRole.FanMode or CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm or CapabilityRole.FanCurve or
        CapabilityRole.FanMeasuredRpm).ToArray());

    /// <summary>All declared lighting controls and observed states, including logical zone identities.</summary>
    public IReadOnlyList<DeviceControl> Lighting => Array.AsReadOnly(Controls.Where(control => control.Role is
        CapabilityRole.LightingPower or CapabilityRole.LightingBrightness or CapabilityRole.LightingZoneColor or
        CapabilityRole.LightingEffect or CapabilityRole.LightingEffectSpeed).ToArray());

    /// <summary>Finds one semantic control. A logical instance must be supplied when a role is ambiguous.</summary>
    /// <param name="role">Meaning of the requested control.</param>
    /// <param name="instanceId">Optional logical instance.</param>
    /// <returns>The single matching control, or null when absent or ambiguous.</returns>
    public DeviceControl? Find(CapabilityRole role, string? instanceId = null)
    {
        var candidates = Controls.Where(control => control.Role == role &&
                                                   (instanceId is null || string.Equals(control.InstanceId, instanceId,
                                                       StringComparison.Ordinal))).Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private int? ObservedInteger(CapabilityRole role)
    {
        return ReliableState(role)?.ObservedValue?.IntegerValue;
    }

    private CapabilityState? ReliableState(CapabilityRole role)
    {
        return Find(role)?.State is
        {
            Available: true, Quality: HardwareStateQuality.Observed or
            HardwareStateQuality.Verified
        } state
            ? state
            : null;
    }
}
