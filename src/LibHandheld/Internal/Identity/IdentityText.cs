namespace LibHandheld.Internal.Identity;

/// <summary>
///     Normalization and comparison for identity strings.
/// </summary>
/// <remarks>
///     Firmware strings are hand-entered by vendors and vary in ways that carry no meaning: trailing
///     spaces, doubled spaces, and inconsistent casing all appear across revisions of the same board.
///     Normalizing once, here, keeps every comparison ordinal and keeps a definition from failing to
///     match its own hardware because a BIOS update added a space.
/// </remarks>
internal static class IdentityText
{
    /// <summary>
    ///     Returns the comparison form of an identity value: trimmed, with internal whitespace runs
    ///     collapsed to a single space. Returns <see langword="null" /> for null, empty, or
    ///     whitespace-only input, so "absent" and "blank" are the same thing to a matcher.
    /// </summary>
    /// <param name="value">Raw value as read from the machine or a manifest.</param>
    /// <returns>The normalized value, or <see langword="null" /> when there is nothing to compare.</returns>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var source = value.AsSpan().Trim();
        var buffer = source.Length <= 256 ? stackalloc char[source.Length] : new char[source.Length];

        var length = 0;
        var previousWasSpace = false;
        foreach (var c in source)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (previousWasSpace)
                {
                    continue;
                }

                buffer[length++] = ' ';
            }
            else
            {
                buffer[length++] = c;
            }

            previousWasSpace = isSpace;
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    ///     Compares two identity values after normalization, ignoring case.
    /// </summary>
    /// <param name="observed">Value read from the machine.</param>
    /// <param name="expected">Value declared by a manifest.</param>
    /// <returns><see langword="true" /> when both normalize to the same non-null value.</returns>
    public static bool Matches(string? observed, string? expected)
    {
        var left = Normalize(observed);
        var right = Normalize(expected);

        // Two absent values are not a match. A definition that declares an EC firmware gate must not
        // be satisfied by a machine that reports no EC firmware at all.
        return left is not null
               && right is not null
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
