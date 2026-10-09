using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>
///     A logical OEM control published by the device engine.
/// </summary>
/// <remarks>
///     A separate channel from the gamepad, deliberately. Face buttons, sticks, triggers, and the D-pad
///     are not expressible here, so a device engine can publish the physical vendor controls without supplying
///     host assignment policy or turning the canonical gamepad channel into a remapper.
/// </remarks>
public sealed record OemControlDescriptor
{
    /// <summary>Stable identifier within the device definition, for example <c>oem1</c>.</summary>
    public required string ControlId { get; init; }

    /// <summary>How the consumer labels it.</summary>
    public required string Label { get; init; }

    /// <summary>Where the control physically is, which decides what may be bound to it.</summary>
    public required OemControlPlacement Placement { get; init; }

    /// <summary>Whether the source distinguishes a short press from a long one.</summary>
    public bool SupportsLongPress { get; init; }

    /// <summary>
    ///     Whether this control disappears when the consumer controller management is turned off.
    /// </summary>
    /// <remarks>
    ///     Declared by the device engine rather than inferred: only it knows whether a control rides on the
    ///     physical-controller resource. On the reference handheld the rear paddles do — they are visible
    ///     only in the acquisition mode the device engine selects — while the front buttons arrive over a
    ///     separate vendor event channel and survive.
    /// </remarks>
    public bool RequiresControllerAcquisition { get; init; }

    /// <summary>
    ///     Whether this is the button the manufacturer prints for its own companion application, such as
    ///     Armoury Crate or Legion Space.
    /// </summary>
    /// <remarks>
    ///     Physical metadata, like Placement: it says what the button is for on the device,
    ///     not what the consumer does with it. The host decides what an unassigned control of this kind does. A
    ///     device engine that already routes the button into the controller sample as Guide or Quick Access
    ///     leaves this false.
    /// </remarks>
    public bool CompanionApplication { get; init; }
}

/// <summary>Where an OEM control sits on the device.</summary>
/// <remarks>
///     Placement is physical metadata. The host owns every action vocabulary and decides whether a
///     mapping is compatible with that placement; device engines never publish host application policy.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemControlPlacement>))]
public enum OemControlPlacement
{
    /// <summary>A front-facing vendor button, such as a home or quick-settings key.</summary>
    Front,

    /// <summary>A rear paddle or grip control.</summary>
    Rear
}

/// <summary>Which press duration an assignment applies to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OemPressKind>))]
public enum OemPressKind
{
    /// <summary>A short press.</summary>
    Short,

    /// <summary>A long press, where the source distinguishes one.</summary>
    Long
}

/// <summary>The physical edge represented by an OEM event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OemControlEdge>))]
public enum OemControlEdge
{
    /// <summary>The control became pressed.</summary>
    Pressed,

    /// <summary>The control was released and any held-state guard may reset.</summary>
    Released
}

/// <summary>One published OEM control event.</summary>
/// <param name="ControlId">The control that was pressed.</param>
/// <param name="Press">Which press duration was observed.</param>
/// <param name="Timestamp">When it was observed, in UTC.</param>
/// <param name="DeduplicationId">
///     Identifier that is equal across every source reporting the same physical press.
/// </param>
/// <param name="Edge">Whether this event represents the press or release edge.</param>
/// <remarks>
///     The deduplication ID exists because one press can legitimately arrive twice: a vendor event
///     channel and a raw-input path may both see it. Without a shared identifier the consumer would toggle the
///     QAM open and closed on a single press.
/// </remarks>
public sealed record OemControlEvent(
    string ControlId,
    OemPressKind Press,
    DateTimeOffset Timestamp,
    string DeduplicationId,
    OemControlEdge Edge = OemControlEdge.Pressed);
