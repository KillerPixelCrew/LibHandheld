using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

/// <summary>The Claw IMU through the SDK's legacy Sensor API stream.</summary>
internal sealed class WindowsClawMotionSource : IClawMotionSource
{
    private readonly Lock _gate = new();

    /// <summary>
    ///     Outlives the streams on purpose: a calibrator that restarted with the stream after a wake would
    ///     send two seconds of uncorrected drift before the first rest window.
    /// </summary>
    private MotionSampleBuilder? _builder;

    private LegacyMotionStream? _stream;

    /// <inheritdoc />
    public ValueTask<bool> StartAsync(
        ClawModel model,
        Action<MotionSample> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stream is not null)
            {
                return ValueTask.FromResult(true);
            }

            var builder = _builder ??= new MotionSampleBuilder(
                raw => ToApplicationBasis(raw, model.GyroSigns),
                raw => ToApplicationBasis(raw, model.AccelerometerSigns));
            if (LegacyMotionSensors.TryOpen(Sources(model)) is not { } sensors)
            {
                return ValueTask.FromResult(false);
            }

            var readingGate = new Lock();
            DateTimeOffset? lastTimestamp = null;
            _stream = LegacyMotionStream.Start(sensors, reading =>
            {
                lock (readingGate)
                {
                    // ISensorDataReport timestamps and HID frame timestamps are UTC. A callback is not
                    // evidence of a current measurement: Intel may deliver queued or repeated reports.
                    if (!CanAcceptReading(reading.Timestamp, DateTimeOffset.UtcNow, lastTimestamp))
                    {
                        return;
                    }

                    lastTimestamp = reading.Timestamp;
                    publish(builder.Build(reading));
                }
            });
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        LegacyMotionStream? stream;
        lock (_gate)
        {
            stream = _stream;
            _stream = null;
        }

        if (stream is not null)
        {
            // Disposal waits for a callback in flight and the poll thread; neither may hold the caller.
            await Task.Run(stream.Dispose, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal static bool CanAcceptReading(DateTimeOffset stamp, DateTimeOffset now, DateTimeOffset? previous)
    {
        return stamp <= now && (previous is null || stamp > previous.Value)
                            && now - stamp < MotionService.StaleReportDelay;
    }

    /// <summary>HC's source order: the standard sensors, then the physical pair where the model declares it.</summary>
    /// <remarks>MS-1T52 takes the physical pair first on its own evidence.</remarks>
    /// <param name="model">Catalog model declaring physical-field support and source preference.</param>
    /// <returns>A fresh priority-ordered source list; discovery selects gyro and accelerometer independently.</returns>
    internal static LegacyMotionSensorSource[] Sources(ClawModel model)
    {
        return model.PreferPhysicalSensors
            ? [LegacyMotionSensorSource.Physical, LegacyMotionSensorSource.Standard]
            : model.PhysicalSensorFields
                ? [LegacyMotionSensorSource.Standard, LegacyMotionSensorSource.Physical]
                : [LegacyMotionSensorSource.Standard];
    }

    /// <summary>The Claw's one axis conversion.</summary>
    /// <remarks>
    ///     On the reference unit both physical LSM6DSO collections share the same die axes. Steam Deck
    ///     packets carry those raw axes, while Steam and SDL expose them to applications as X, Z, -Y.
    ///     Every Claw in HC swaps to (X, Z, Y) and then applies the model's signs, which for the A2VM
    ///     gyro and accelerometer are (1, 1, -1). The Neptune encoder applies the inverse when it writes
    ///     the raw packet slots.
    /// </remarks>
    /// <param name="raw">Raw sensor axes in the caller's sensor units.</param>
    /// <param name="signs">Model-specific axis multipliers applied after swapping Y and Z.</param>
    /// <returns>The application-basis vector in the same units; apply this transform exactly once.</returns>
    internal static Vector3 ToApplicationBasis(Vector3 raw, Vector3 signs)
    {
        return new Vector3(raw.X, raw.Z, raw.Y) * signs;
    }
}
