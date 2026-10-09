namespace LibHandheld.Internal.Capabilities;

/// <summary>
///     The one rule for plugin-supplied plain text, shared by a capability's custom label and a
///     <see cref="CapabilityValueKind.Text" /> value.
/// </summary>
/// <remarks>
///     The rule keeps labels and values non-empty and single-line so logs and controls present the same
///     text. It sets no length of its own: a text value is held to the maximum its descriptor declares,
///     and labels and identifiers are as long as the plugin wrote them.
///     <para>
///         Text validated here is never a format string, never markup, and never a localization key. WSGM
///         cannot translate text it did not author, so it renders in whatever language the plugin wrote it.
///     </para>
/// </remarks>
internal static class PlainText
{
    /// <summary>
    ///     Whether a value is safe to render and log.
    /// </summary>
    /// <param name="value">The text to validate.</param>
    /// <param name="field">Field name used in the failure message.</param>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the value is safe.</returns>
    public static bool TryValidate(
        string? value,
        string field,
        out string? error
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{field} is required.";
            return false;
        }

        if (value.Any(IsUnsafe))
        {
            error = $"{field} contains a control or bidirectional-override character.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Whether a text value is safe to render and log and fits the maximum its declaration names.
    /// </summary>
    /// <param name="value">The text to validate.</param>
    /// <param name="maximumLength">The longest value the descriptor or setting declared, in characters.</param>
    /// <param name="field">Field name used in the failure message.</param>
    /// <param name="error">The reason it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> when the value is safe and within the declared maximum.</returns>
    public static bool TryValidate(
        string? value,
        int maximumLength,
        string field,
        out string? error
    )
    {
        if (!TryValidate(value, field, out error))
        {
            return false;
        }

        if (value!.Length <= maximumLength)
        {
            return true;
        }

        error = $"{field} exceeds its declared {maximumLength} characters.";
        return false;
    }

    /// <summary>
    ///     Whether a value is usable as a stable identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><see langword="true" /> when the value is a legal identifier.</returns>
    /// <remarks>
    ///     Identifiers are matched, logged, and used as keys, so they are restricted to a shape that
    ///     survives all three: letters, digits, dot, underscore, and hyphen. Uppercase is allowed
    ///     because the identifiers WSGM itself sends are PascalCase. The shape is the whole rule: no length
    ///     is imposed.
    /// </remarks>
    public static bool IsIdentifier(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.All(c => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '.' or '_' or '-');
    }

    /// <summary>
    ///     Whether a character would corrupt a log line or misrepresent the rest of the string.
    /// </summary>
    /// <param name="c">The character to judge.</param>
    /// <returns><see langword="true" /> when the character must be rejected.</returns>
    /// <remarks>
    ///     Control and bidirectional formatting characters make logs and controls present different
    ///     shapes, so the shared text contract excludes them.
    /// </remarks>
    public static bool IsUnsafe(char c)
    {
        if (char.IsControl(c))
        {
            return true;
        }

        return c switch
        {
            // LRM and RLM.
            '‎' or '‏' => true,
            // The LRE/RLE/PDF/LRO/RLO embedding and override set.
            >= '‪' and <= '‮' => true,
            // The isolates, which do the same job as the overrides above.
            >= '⁦' and <= '⁩' => true,
            _ => false
        };
    }
}
