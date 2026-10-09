using LibHandheld.Contracts;

namespace LibHandheld.Internal.Capabilities;

/// <summary>Checks a typed native value against its descriptor.</summary>
/// <remarks>
///     Admission also checks the active owner, cancellation, deadline and power-source requirements.
/// </remarks>
internal static class CapabilityValueValidation
{
    /// <summary>Whether a value has its descriptor's kind and lies within the shape and bounds it declares.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="descriptor">The capability's descriptor.</param>
    /// <param name="error">Why the value does not match, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the value matches.</returns>
    /// <remarks>
    ///     An integer lies within the declared minimum and maximum and on the step counted from the minimum.
    ///     A choice is one of the declared options. A colour is 24-bit RGB. A curve has at least one point,
    ///     strictly ascending inputs and outputs within the declared bounds. Text is plain text within the
    ///     declared maximum length. This is not descriptor validation: omitted integer bounds are unbounded,
    ///     omitted or nonpositive steps impose no step, and unrelated payload fields are not checked.
    ///     <see cref="CapabilityValueKind.None" /> does not match a writable value.
    /// </remarks>
    public static bool ValueMatches(
        CapabilityValue value,
        CapabilityDescriptor descriptor,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (value.Kind != descriptor.ValueKind)
        {
            error = "Capability value kind differs from its descriptor.";
            return false;
        }

        var valid = value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is not null,
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                                           && (descriptor.Minimum is null || integer >= descriptor.Minimum)
                                           && (descriptor.Maximum is null || integer <= descriptor.Maximum)
                                           && (descriptor.Step is null or <= 0
                                               || (integer - (descriptor.Minimum ?? 0)) % descriptor.Step == 0),
            CapabilityValueKind.Choice => value.ChoiceValue is { Length: > 0 } choice
                                          && descriptor.Choices.Any(item => string.Equals(
                                              item.Value,
                                              choice,
                                              StringComparison.Ordinal)),
            CapabilityValueKind.Color => value.ColorValue is >= 0 and <= 0xFFFFFF,
            CapabilityValueKind.Curve => CurveIsValid(value.CurveValue, descriptor),
            CapabilityValueKind.Text => PlainText.TryValidate(
                value.TextValue,
                descriptor.MaximumLength ?? 0,
                "text",
                out _),
            _ => false
        };
        error = valid ? null : "Capability value violates its descriptor shape or bounds.";
        return valid;
    }

    /// <summary>
    ///     Point count, strictly ascending inputs, and outputs inside whatever bounds the descriptor declared.
    /// </summary>
    /// <remarks>
    ///     Only the bounds the device declared are enforced: a descriptor that leaves one unset is saying it
    ///     has no limit there, and inventing one would refuse a curve the device would have accepted.
    /// </remarks>
    private static bool CurveIsValid(IReadOnlyList<CurvePoint> points, CapabilityDescriptor descriptor)
    {
        if (points.Count is 0)
        {
            return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (index > 0 && point.Input <= points[index - 1].Input)
            {
                return false;
            }

            if ((descriptor.Minimum is { } minimum && point.Output < minimum)
                || (descriptor.Maximum is { } maximum && point.Output > maximum))
            {
                return false;
            }
        }

        return true;
    }
}
