namespace LibHandheld.Internal.Windows;

/// <summary>Decides when a polling controller reader has stopped making progress.</summary>
/// <remarks>
///     Checked on its own timer. A gap between checks longer than <see cref="FrozenGap" /> means the
///     whole process was frozen, as Modern Standby does, so the reader cannot be blamed for it and the
///     count starts again. A stall is reported once per reader; a new reader brings a new watch.
/// </remarks>
internal sealed class ControllerStallWatch
{
    /// <summary>How long the reader may make no progress, with the watch itself running, before it is stalled.</summary>
    internal static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(3);

    /// <summary>A longer gap between checks means the checks themselves were frozen.</summary>
    internal static readonly TimeSpan FrozenGap = TimeSpan.FromMilliseconds(1500);

    private long _progress = -1;
    private TimeSpan _stalled;
    private bool _reported;

    /// <summary>Records one check.</summary>
    /// <param name="progress">The reader's progress counter.</param>
    /// <param name="sinceLastCheck">Time since the previous check on a clock that runs during a freeze.</param>
    /// <returns>True exactly once, when the reader first counts as stalled.</returns>
    internal bool Check(long progress, TimeSpan sinceLastCheck)
    {
        if (progress != _progress || sinceLastCheck > FrozenGap)
        {
            _progress = progress;
            _stalled = TimeSpan.Zero;
            return false;
        }

        _stalled += sinceLastCheck;
        if (_reported || _stalled < StallLimit)
        {
            return false;
        }

        _reported = true;
        return true;
    }
}
