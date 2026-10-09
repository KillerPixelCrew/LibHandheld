using LibHandheld.Internal.Plugin;

namespace LibHandheld.Internal.Windows;

/// <summary>Streams an open <see cref="LegacyMotionSensors" /> set: driver events, or a poll where events fail.</summary>
/// <remarks>
///     Owns the supplied sensor set. Readings are delivered directly on a Sensor API callback or polling
///     thread, without a queue. Callbacks must return quickly. Polling uses a precision ticker and drops
///     duplicate reports. Disposal suppresses further delivery and retains native owners until the
///     reader and unsubscription finish, even when the bounded disposal wait times out.
/// </remarks>
internal sealed class LegacyMotionStream : IDisposable
{
    /// <summary>
    ///     The poll period, faster than the sensors' 10 ms minimum report interval so scheduler jitter
    ///     cannot routinely skip a report; duplicates are recognised by the report key.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    private readonly CancellationTokenSource? _cancellation;
    private readonly Lock _disposeGate = new();
    private readonly Thread? _poller;
    private readonly LegacyMotionSensors _sensors;
    private Task? _cleanup;
    private bool _deliveryFaultTraced;
    private bool _disposed;

    private LegacyMotionStream(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading)
    {
        _sensors = sensors;

        void Deliver(MotionSensorReading reading)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            try
            {
                onReading(reading);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Trace the first failure only; delivery continues with the next reading.
                if (!_deliveryFaultTraced)
                {
                    _deliveryFaultTraced = true;
                    DeviceTrace.Failure("motion", "IMU reading callback failed", ex);
                }
            }
        }

        if (sensors.TrySubscribe(Deliver, out var error))
        {
            return;
        }

        DeviceTrace.Info("motion",
            $"IMU events unavailable ({error}); polling every {PollInterval.TotalMilliseconds:F0} ms instead.");
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _poller = new Thread(() => Poll(sensors, Deliver, token))
        {
            IsBackground = true,
            Name = "WSGM IMU poll"
        };
        _poller.Start();
    }

    /// <summary>Suppresses new delivery and waits up to two seconds for owned sensor cleanup.</summary>
    /// <remarks>
    ///     A callback already entered may still finish. Repeated calls wait for the same cleanup task.
    ///     Do not call synchronously from a reading callback that cleanup must join.
    /// </remarks>
    /// <exception cref="TimeoutException">Cleanup continues in the background with its native owners retained.</exception>
    /// <exception cref="AggregateException">The background cleanup task failed.</exception>
    public void Dispose()
    {
        Task cleanup;
        lock (_disposeGate)
        {
            Volatile.Write(ref _disposed, true);
            if (_cleanup is null)
            {
                _cancellation?.Cancel();
                _cleanup = Task.Run(() =>
                {
                    // Keep every native owner alive until the actual reader and unsubscription finish.
                    _poller?.Join();
                    try
                    {
                        _sensors.Dispose();
                    }
                    finally
                    {
                        _cancellation?.Dispose();
                    }
                });
                _ = _cleanup.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            cleanup = _cleanup;
        }

        if (!cleanup.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("Motion cleanup is still running; its native owners were retained.");
        }
    }

    /// <summary>Starts delivering readings, taking ownership of the sensors.</summary>
    /// <param name="sensors">An open sensor set; the stream disposes it.</param>
    /// <param name="onReading">
    ///     Receives each fresh reading on the producer thread. Must not block or dispose the stream;
    ///     ordinary callback exceptions are traced once and do not stop later delivery.
    /// </param>
    /// <returns>The running stream.</returns>
    public static LegacyMotionStream Start(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        ArgumentNullException.ThrowIfNull(onReading);
        return new LegacyMotionStream(sensors, onReading);
    }

    private static void Poll(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading,
        CancellationToken cancellationToken)
    {
        try
        {
            PollCore(sensors, onReading, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("motion", "IMU poll stopped", ex);
        }
    }

    private static void PollCore(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading,
        CancellationToken cancellationToken)
    {
        var failing = false;
        using PrecisionTicker ticker = new(PollInterval, cancellationToken);
        while (ticker.Wait())
        {
            var result = sensors.TryRead(out var reading, out var error);
            if (result == MotionSensorReadResult.Failed)
            {
                if (!failing)
                {
                    failing = true;
                    DeviceTrace.Warn("motion", $"IMU read failed: {error}");
                }

                continue;
            }

            if (failing)
            {
                failing = false;
                DeviceTrace.Info("motion", "IMU readings resumed.");
            }

            if (result == MotionSensorReadResult.Fresh && !cancellationToken.IsCancellationRequested)
            {
                onReading(reading);
            }
        }
    }
}
