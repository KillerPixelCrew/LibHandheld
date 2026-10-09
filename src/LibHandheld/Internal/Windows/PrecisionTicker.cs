using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>Waits on a high-resolution periodic Windows timer, with a timed-wait fallback.</summary>
/// <remarks>
///     Does not change the machine-wide timer resolution. Actual scheduling accuracy depends on Windows;
///     missed ticks are not queued. Owns the native timer, but not the supplied token's wait handle.
///     Stop the polling thread before disposing this instance.
/// </remarks>
internal sealed partial class PrecisionTicker : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly WaitHandle[] _handles;
    private readonly TimeSpan _interval;
    private readonly SafeWaitHandle? _timer;
    private readonly TimerWaitHandle? _timerHandle;

    /// <summary>Starts ticking.</summary>
    /// <param name="interval">Time between ticks, at least one millisecond.</param>
    /// <param name="cancellationToken">Signals waits to finish; its source must remain alive while the ticker is used.</param>
    /// <exception cref="ArgumentOutOfRangeException">The interval is shorter than one millisecond.</exception>
    public PrecisionTicker(TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMilliseconds(1));
        _interval = interval;
        var timer = CreateWaitableTimerEx(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        var dueTime = -interval.Ticks;
        if (!timer.IsInvalid
            && SetWaitableTimer(timer, in dueTime, (int)Math.Max(1, interval.TotalMilliseconds), 0, 0, false))
        {
            _timer = timer;
            _timerHandle = new TimerWaitHandle(timer);
            _handles = [_timerHandle, cancellationToken.WaitHandle];
        }
        else
        {
            timer.Dispose();
            _handles = [cancellationToken.WaitHandle];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timerHandle?.Dispose();
        _timer?.Dispose();
    }

    /// <summary>Waits for the next tick.</summary>
    /// <returns>
    ///     True on a timer signal or fallback interval; false when cancellation wins the wait.
    ///     A timer and cancellation signaled together can still return true.
    /// </returns>
    public bool Wait()
    {
        if (_timer is null)
        {
            return !_handles[0].WaitOne(_interval);
        }

        return WaitHandle.WaitAny(_handles) == 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags,
        uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period,
        nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    private sealed class TimerWaitHandle : WaitHandle
    {
        internal TimerWaitHandle(SafeWaitHandle handle)
        {
            // The ticker owns the handle and closes it; this wrapper only lets WaitAny see it.
            SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), false);
        }
    }
}
