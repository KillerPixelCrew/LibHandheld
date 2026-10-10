using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Contracts;

/// <summary>
///     How much a reported hardware value can be trusted.
/// </summary>
/// <remarks>
///     Availability is independent of value quality. An accepted control write supplies
///     <see cref="Observed" /> state for the owning cycle without a readback or verification pass.
///     Sensor measurements retain their actual sampling time and may become stale independently.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<HardwareStateQuality>))]
public enum HardwareStateQuality
{
    /// <summary>No value has been sampled or successfully written; an available control remains usable.</summary>
    Unknown,

    /// <summary>Read from hardware or published after an accepted write, without independent confirmation.</summary>
    Observed,

    /// <summary>Read back and confirmed to match what was applied.</summary>
    Verified,

    /// <summary>Was valid, but the observation has expired or its generation is gone.</summary>
    Stale,

    /// <summary>The capability failed and its value cannot be trusted at all.</summary>
    Faulted
}

/// <summary>
///     The live state of one capability as reported by its native owner.
/// </summary>
/// <remarks>
///     Versioned separately from the descriptor because it changes constantly while the descriptor does
///     not. It carries only what the plugin observed; WSGM's desired value and UI progress live in
///     WSGM's capability projection and are never mixed in here â€” a plugin does not own what the
///     user asked for.
/// </remarks>
public sealed record CapabilityState
{
    /// <summary>Capability this state belongs to.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Instance discriminator, matching the descriptor.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Whether the capability can currently be used.</summary>
    public required bool Available { get; init; }

    /// <summary>Why it is unavailable or degraded. Null when it is healthy.</summary>
    public CapabilityReason? Reason { get; init; }

    /// <summary>The last read or successfully written value, or null when unknown or action-only.</summary>
    /// <remarks>Must match the descriptor's value shape; <see cref="Quality" /> records its evidence.</remarks>
    public CapabilityValue? ObservedValue { get; init; }

    /// <summary>How much <see cref="ObservedValue" /> can be trusted.</summary>
    public required HardwareStateQuality Quality { get; init; }

    /// <summary>When the read or accepted write was observed, in UTC; null when no observation exists.</summary>
    public DateTimeOffset? ObservedAt { get; init; }
}

/// <summary>
///     A capability value in whichever shape its descriptor declares.
/// </summary>
/// <remarks>
///     Exactly the field selected by <see cref="Kind" /> carries a value; unused scalar fields stay null
///     and unused curves stay empty. Construction and factory methods do not validate descriptor bounds.
///     Validate with <see cref="CapabilityValueValidation" /> before accepting a value. Collection
///     properties retain the supplied list, which must not be mutated after publication.
/// </remarks>
public sealed record CapabilityValue
{
    /// <summary>Which field carries the value.</summary>
    public required CapabilityValueKind Kind { get; init; }

    /// <summary>Value when <see cref="Kind" /> is <see cref="CapabilityValueKind.Boolean" />.</summary>
    public bool? BooleanValue { get; init; }

    /// <summary>Value when <see cref="Kind" /> is <see cref="CapabilityValueKind.Integer" />.</summary>
    public int? IntegerValue { get; init; }

    /// <summary>Selected option when <see cref="Kind" /> is <see cref="CapabilityValueKind.Choice" />.</summary>
    public string? ChoiceValue { get; init; }

    /// <summary>Packed 24-bit RGB when <see cref="Kind" /> is <see cref="CapabilityValueKind.Color" />.</summary>
    public int? ColorValue { get; init; }

    /// <summary>Curve points when <see cref="Kind" /> is <see cref="CapabilityValueKind.Curve" />.</summary>
    public IReadOnlyList<CurvePoint> CurveValue { get; init; } = [];

    /// <summary>
    ///     Text when <see cref="Kind" /> is <see cref="CapabilityValueKind.Text" />.
    /// </summary>
    /// <remarks>
    ///     Bounded by the descriptor's <see cref="CapabilityDescriptor.MaximumLength" /> and checked
    ///     against <see cref="PlainText" /> before it reaches any surface.
    /// </remarks>
    public string? TextValue { get; init; }

    /// <summary>Creates a <see cref="CapabilityValueKind.None" /> value, which carries no field.</summary>
    /// <returns>The value.</returns>
    public static CapabilityValue None()
    {
        return new CapabilityValue { Kind = CapabilityValueKind.None };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Boolean" /> value.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value with <see cref="BooleanValue" /> set.</returns>
    public static CapabilityValue Boolean(bool value)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Boolean,
            BooleanValue = value
        };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Integer" /> value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The value with <see cref="IntegerValue" /> set.</returns>
    public static CapabilityValue Integer(int value)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = value
        };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Choice" /> value.</summary>
    /// <param name="value">The selected option.</param>
    /// <returns>The value with <see cref="ChoiceValue" /> set.</returns>
    public static CapabilityValue Choice(string value)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Choice,
            ChoiceValue = value
        };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Color" /> value.</summary>
    /// <param name="value">Packed 24-bit RGB.</param>
    /// <returns>The value with <see cref="ColorValue" /> set.</returns>
    public static CapabilityValue Color(int value)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Color,
            ColorValue = value
        };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Curve" /> value.</summary>
    /// <param name="points">The curve points. The list is stored as given, not copied.</param>
    /// <returns>The value with <see cref="CurveValue" /> set.</returns>
    public static CapabilityValue Curve(IReadOnlyList<CurvePoint> points)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Curve,
            CurveValue = points
        };
    }

    /// <summary>Creates a <see cref="CapabilityValueKind.Text" /> value.</summary>
    /// <param name="value">The text. Validation against the descriptor stays with the caller.</param>
    /// <returns>The value with <see cref="TextValue" /> set.</returns>
    public static CapabilityValue Text(string value)
    {
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Text,
            TextValue = value
        };
    }
}

/// <summary>One point of a capability curve, such as a fan table entry.</summary>
/// <param name="Input">The independent value, for example a temperature in Celsius.</param>
/// <param name="Output">The dependent value, for example a duty cycle in percent.</param>
public readonly record struct CurvePoint(int Input, int Output);
