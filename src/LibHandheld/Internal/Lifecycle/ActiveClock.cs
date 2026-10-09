using System.Diagnostics;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Lifecycle;

/// <summary>
///     A process-wide clock that advances only while the process can run: Modern Standby, sleep and
///     hibernation add at most <see cref="MaximumStep" /> to it however long they last.
/// </summary>
/// <remarks>
///     A background thread samples <see cref="Stopwatch" /> at <see cref="Tick" /> intervals and clamps
///     each observed gap to <see cref="MaximumStep" />. This approximates runnable time without relying
///     on standby-specific Windows clocks; severe scheduling stalls are clamped too. Values are process
///     local and must not be persisted or compared across processes.
/// </remarks>
internal static class ActiveClock
{
    /// <summary>How often the clock is observed.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>The most one observation adds; longer gaps are treated as suspension or scheduling stalls.</summary>
    public static readonly TimeSpan MaximumStep = TimeSpan.FromSeconds(1);

    private static readonly Lock Gate = new();
    private static readonly List<(long Ticks, CancellationTokenSource Source)> Pending = [];
    private static readonly AutoResetEvent Wake = new(false);
    private static long _active;
    private static long _lastTimestamp = Stopwatch.GetTimestamp();
    private static int _started;

    /// <summary>The active time since the process first read this clock.</summary>
    public static TimeSpan Now
    {
        get
        {
            EnsureStarted();
            lock (Gate)
            {
                Advance();
                return TimeSpan.FromTicks(_active);
            }
        }
    }

    internal static void CancelAt(long ticks, CancellationTokenSource source)
    {
        EnsureStarted();
        lock (Gate)
        {
            Pending.Add((ticks, source));
        }

        Wake.Set();
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "WSGM active clock", Priority = ThreadPriority.AboveNormal }
            .Start();
    }

    private static void Run()
    {
        List<CancellationTokenSource> due = [];
        var wait = Tick;
        while (true)
        {
            // Never longer than a tick, so a freeze always shows as one long step; shorter when a
            // deadline falls due sooner, so short deadlines cancel on time.
            Wake.WaitOne(wait);
            lock (Gate)
            {
                Advance();
                var now = _active;
                var next = long.MaxValue;
                for (var index = Pending.Count - 1; index >= 0; index--)
                {
                    if (Pending[index].Ticks <= now)
                    {
                        due.Add(Pending[index].Source);
                        Pending.RemoveAt(index);
                    }
                    else
                    {
                        next = Math.Min(next, Pending[index].Ticks);
                    }
                }

                wait = next == long.MaxValue
                    ? Tick
                    : TimeSpan.FromTicks(Math.Clamp(next - now, TimeSpan.TicksPerMillisecond, Tick.Ticks));
            }

            foreach (var source in due)
            {
                _ = CancelAsync(source);
            }

            due.Clear();
        }
    }

    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The operation finished before its deadline.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("clock", "Deadline cancellation callback failed", ex);
        }
    }

    /// <summary>How much of one observed step the clock counts.</summary>
    /// <param name="observed">The monotonic time between two observations.</param>
    /// <returns>Zero for nonpositive input; otherwise the smaller of the observed step and <see cref="MaximumStep" />.</returns>
    public static TimeSpan CountedStep(TimeSpan observed)
    {
        return observed <= TimeSpan.Zero ? TimeSpan.Zero
            : observed > MaximumStep ? MaximumStep
            : observed;
    }

    private static void Advance()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var observed = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
        _lastTimestamp = timestamp;
        _active += CountedStep(observed).Ticks;
    }
}
