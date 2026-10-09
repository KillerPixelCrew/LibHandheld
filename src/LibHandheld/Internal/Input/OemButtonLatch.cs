using System.Diagnostics;
using LibHandheld.Contracts;

namespace LibHandheld.Internal.Input;

/// <summary>Turns a release-less OEM button event into a press the virtual pad can deliver.</summary>
/// <remarks>
///     Handheld front buttons often arrive as one vendor or WMI event per press, with no release, while
///     the pad reader produces full samples at about 125 Hz. HC presses such a button and releases it
///     after its <c>KeyPressDelay</c> (<c>IDevice.KeyPressAndRelease</c>); this holds the Guide or Quick
///     Access bit for the same time, long enough to survive a dropped sample and short enough to stay a tap.
/// </remarks>
internal sealed class OemButtonLatch
{
    /// <summary>How long a latched button stays down: HC's <c>KeyPressDelay</c>.</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(200);

    private long _guideUntilTicks;
    private long _pulseUntil;
    private long _quickAccessUntilTicks;

    /// <summary>Monotonic deadline for a native control pulse, including controls without a gamepad bit.</summary>
    internal long PulseDeadline => Volatile.Read(ref _pulseUntil);

    internal void Pulse(TimeSpan duration)
    {
        Volatile.Write(ref _pulseUntil,
            Stopwatch.GetTimestamp() + checked((long)(duration.TotalSeconds * Stopwatch.Frequency)));
    }

    internal bool PulseHeld(long now)
    {
        return now < PulseDeadline;
    }

    /// <summary>Latches Guide and/or Quick Access down for <see cref="HoldDuration" />.</summary>
    /// <param name="button">The canonical buttons the press maps to; others are ignored.</param>
    /// <param name="now">Current time.</param>
    public void Press(CanonicalButtons button, DateTimeOffset now)
    {
        var until = (now + HoldDuration).UtcTicks;
        if ((button & CanonicalButtons.Guide) != 0)
        {
            Volatile.Write(ref _guideUntilTicks, until);
        }

        if ((button & CanonicalButtons.QuickAccess) != 0)
        {
            Volatile.Write(ref _quickAccessUntilTicks, until);
        }
    }

    /// <summary>The latched buttons that are down in a sample taken now.</summary>
    /// <param name="now">The sample's timestamp.</param>
    /// <returns>The buttons still held, or none once the hold elapsed.</returns>
    /// <remarks>Lock-free: it runs for every controller sample.</remarks>
    public CanonicalButtons Current(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var held = ticks < Volatile.Read(ref _guideUntilTicks) ? CanonicalButtons.Guide : CanonicalButtons.None;
        return ticks < Volatile.Read(ref _quickAccessUntilTicks) ? held | CanonicalButtons.QuickAccess : held;
    }

    /// <summary>Releases every latched button at once.</summary>
    public void Clear()
    {
        Volatile.Write(ref _guideUntilTicks, 0);
        Volatile.Write(ref _quickAccessUntilTicks, 0);
        Volatile.Write(ref _pulseUntil, 0);
    }
}
