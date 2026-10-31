namespace LibHandheld.Contracts;

/// <summary>
///     Measured fan duties for the consumer's Quiet, Default and Aggressive fan curve presets, and how the
///     model's temperature is steadied before a software curve reads it.
/// </summary>
/// <remarks>
///     Only a model whose fans or temperature behave differently from the consumer's general shapes
///     carries these; every other definition leaves <see cref="HandheldDefinition.FanCurvePresets" /> null.
///     Each list holds duty percentages sampled at 0, 10, 20 … °C, so its first entry is the duty at 0 °C.
///     These are starting shapes for an editor and a curve loop, never a hardware observation or a write.
///     Assignment takes a read-only snapshot so later edits to the supplied collection cannot change the value.
/// </remarks>
public sealed record FanCurvePresetDuties
{
    /// <summary>The Quiet preset's duty samples.</summary>
    public required IReadOnlyList<int> Quiet
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<int>([.. value]);
        }
    }

    /// <summary>The Default preset's duty samples.</summary>
    public required IReadOnlyList<int> Default
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<int>([.. value]);
        }
    }

    /// <summary>The Aggressive preset's duty samples.</summary>
    public required IReadOnlyList<int> Aggressive
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly<int>([.. value]);
        }
    }

    /// <summary>
    ///     How far the temperature must move, in °C, from the reading the current target came from before
    ///     the target is recomputed.
    /// </summary>
    public required double HysteresisCelsius { get; init; }

    /// <summary>The most the duty may rise in one one-second tick, in percentage points.</summary>
    public required int MaximumRisePerTick { get; init; }

    /// <summary>The most the duty may fall in one one-second tick, in percentage points.</summary>
    public required int MaximumFallPerTick { get; init; }

    /// <summary>How many of the latest fresh temperature readings are averaged before the curve is read.</summary>
    public required int AverageSamples { get; init; }
}
