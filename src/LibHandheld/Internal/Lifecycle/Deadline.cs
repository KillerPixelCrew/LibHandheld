namespace LibHandheld.Internal.Lifecycle;

/// <summary>
///     A point in time on <see cref="ActiveClock" />: the moment by which an operation must have
///     finished, counting only time the process could run.
/// </summary>
/// <remarks>
///     Uses the process-local clamped monotonic clock rather than a continuously running UTC clock.
///     Long scheduling or suspension gaps consume at most <see cref="ActiveClock.MaximumStep" />.
///     A deadline signals cooperative cancellation; it cannot terminate a native call.
/// </remarks>
internal readonly struct Deadline : IEquatable<Deadline>, IComparable<Deadline>
{
    private readonly long _ticks;

    private Deadline(long ticks)
    {
        _ticks = ticks;
    }

    /// <summary>A deadline that has already passed.</summary>
    public static Deadline Expired => new(long.MinValue);

    /// <summary>A deadline that never passes.</summary>
    public static Deadline Never => new(long.MaxValue);

    /// <summary>The time left before the deadline, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            if (_ticks == long.MaxValue)
            {
                return TimeSpan.MaxValue;
            }

            // Compare before subtracting to avoid overflow for the Expired sentinel.
            var now = ActiveClock.Now.Ticks;
            return _ticks > now ? TimeSpan.FromTicks(_ticks - now) : TimeSpan.Zero;
        }
    }

    /// <summary>Whether the deadline has passed.</summary>
    public bool HasExpired => Remaining == TimeSpan.Zero;

    /// <summary>A deadline <paramref name="budget" /> of active time from now.</summary>
    /// <param name="budget">Active-time budget; zero or negative budgets expire immediately.</param>
    /// <returns>A deadline from the current active clock, saturated to <see cref="Never" /> on overflow.</returns>
    public static Deadline After(TimeSpan budget)
    {
        var now = ActiveClock.Now.Ticks;
        var add = Math.Max(0, budget.Ticks);
        return new Deadline(add >= long.MaxValue - now ? long.MaxValue : now + add);
    }

    /// <summary>The active-time deadline equivalent to a wall-clock one, measured from now.</summary>
    /// <param name="utc">Wall-clock target converted once to an active-time budget; later UTC changes do not alter it.</param>
    /// <returns>The deadline, already expired when <paramref name="utc" /> has passed.</returns>
    public static Deadline At(DateTimeOffset utc)
    {
        return After(utc - DateTimeOffset.UtcNow);
    }

    /// <summary>The earlier of two deadlines.</summary>
    /// <param name="other">The other deadline.</param>
    /// <returns>Whichever comes first.</returns>
    public Deadline Earliest(Deadline other)
    {
        return _ticks <= other._ticks ? this : other;
    }

    /// <summary>A source that cancels when the deadline passes or any linked token cancels.</summary>
    /// <param name="linked">Further tokens that cancel it.</param>
    /// <returns>The source, which the caller disposes. It is already cancelled when the deadline has passed.</returns>
    /// <remarks>
    ///     A timer on the wall clock would fire the moment a frozen process thaws, so the source is
    ///     cancelled by <see cref="ActiveClock" /> instead, within its tick.
    /// </remarks>
    public CancellationTokenSource CreateCancellationSource(params CancellationToken[] linked)
    {
        // CreateLinkedTokenSource requires at least one token.
        var source = linked.Length == 0
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(linked);
        if (HasExpired)
        {
            source.Cancel();
        }
        else if (_ticks != long.MaxValue)
        {
            ActiveClock.CancelAt(_ticks, source);
        }

        return source;
    }

    /// <inheritdoc />
    public bool Equals(Deadline other)
    {
        return _ticks == other._ticks;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is Deadline other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return _ticks.GetHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(Deadline other)
    {
        return _ticks.CompareTo(other._ticks);
    }

    /// <summary>Whether two deadlines are the same moment.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when both store the same active-clock instant.</returns>
    public static bool operator ==(Deadline left, Deadline right)
    {
        return left.Equals(right);
    }

    /// <summary>Whether two deadlines differ.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when the stored active-clock instants differ.</returns>
    public static bool operator !=(Deadline left, Deadline right)
    {
        return !left.Equals(right);
    }

    /// <summary>Whether the left deadline comes first.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when the left instant precedes the right instant.</returns>
    public static bool operator <(Deadline left, Deadline right)
    {
        return left._ticks < right._ticks;
    }

    /// <summary>Whether the left deadline comes later.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when the left instant follows the right instant.</returns>
    public static bool operator >(Deadline left, Deadline right)
    {
        return left._ticks > right._ticks;
    }

    /// <summary>Whether the left deadline comes first or at the same moment.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when the left instant precedes or equals the right instant.</returns>
    public static bool operator <=(Deadline left, Deadline right)
    {
        return left._ticks <= right._ticks;
    }

    /// <summary>Whether the left deadline comes later or at the same moment.</summary>
    /// <param name="left">First deadline on the process-local active clock.</param>
    /// <param name="right">Second deadline on the same clock.</param>
    /// <returns>True when the left instant follows or equals the right instant.</returns>
    public static bool operator >=(Deadline left, Deadline right)
    {
        return left._ticks >= right._ticks;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return HasExpired ? "expired" : $"{Remaining.TotalMilliseconds:F0} ms left";
    }
}
