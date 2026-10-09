using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Devices.Sensors;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

internal interface IAllyMotionSource : IAsyncDisposable
{
    /// <param name="publish">Takes each sample on the sensor's own thread; it must return quickly.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <summary>Starts the motion stream, preferring the Windows sensor pair before the legacy sensor fallback.</summary>
    /// <returns>True when already streaming or a sensor session starts; false when neither supported sensor path is available.</returns>
    ValueTask<bool> StartAsync(Action<MotionSample> publish, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>The Ally IMU through Windows sensors.</summary>
/// <remarks>
///     HC reads the WinRT <c>Gyrometer</c> and <c>Accelerometer</c> first and falls back to the legacy
///     Sensor API only when WinRT exposes neither (<c>IDevice.PullSensors</c>, <c>IDevice.cs:1152-1172</c>;
///     <c>WindowsSensorManager.cs:96-192</c>). The Device Lab run on RC73XA found BMI320 accelerometer and
///     gyrometer sensors on the standard legacy motion fields, which the SDK's legacy stream reads.
/// </remarks>
/// <param name="model">Model axis transforms applied before the retained gyro-offset correction.</param>
internal sealed class WindowsAllyMotionSource(AllyModel model) : IAllyMotionSource
{
    /// <summary>Outlives each session, so a restarted stream keeps the measured zero-rate offset.</summary>
    private readonly MotionSampleBuilder _builder = new(model.Gyro.Apply, model.Accelerometer.Apply);

    private readonly Lock _gate = new();

    private IDisposable? _session;

    public ValueTask<bool> StartAsync(Action<MotionSample> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_session is not null)
            {
                return ValueTask.FromResult(true);
            }

            var builder = _builder;
            _session = WinRtMotionSession.TryStart(builder, publish)
                       ?? (IDisposable?)(LegacyMotionSensors.TryOpen([LegacyMotionSensorSource.Standard]) is { } sensors
                           ? LegacyMotionStream.Start(sensors, reading => publish(builder.Build(reading)))
                           : null);
            return ValueTask.FromResult(_session is not null);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IDisposable? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }
}

/// <summary>WinRT gyrometer and accelerometer at their fastest report interval.</summary>
internal sealed class WinRtMotionSession : IDisposable
{
    private readonly Accelerometer _accelerometer;
    private readonly uint _accelerometerInterval;
    private readonly MotionSampleBuilder _builder;
    private readonly Lock _gate = new();
    private readonly Gyrometer _gyrometer;
    private readonly uint _gyrometerInterval;
    private readonly Action<MotionSample> _publish;
    private Vector3 _acceleration;
    private bool _disposed;

    private WinRtMotionSession(
        Gyrometer gyrometer,
        Accelerometer accelerometer,
        MotionSampleBuilder builder,
        Action<MotionSample> publish)
    {
        _gyrometer = gyrometer;
        _accelerometer = accelerometer;
        _builder = builder;
        _publish = publish;
        _gyrometerInterval = gyrometer.ReportInterval;
        _accelerometerInterval = accelerometer.ReportInterval;
        gyrometer.ReportInterval = Math.Max(gyrometer.MinimumReportInterval, 1u);
        accelerometer.ReportInterval = Math.Max(accelerometer.MinimumReportInterval, 1u);
        if (accelerometer.GetCurrentReading() is { } first)
        {
            _acceleration = new Vector3((float)first.AccelerationX, (float)first.AccelerationY,
                (float)first.AccelerationZ);
        }

        accelerometer.ReadingChanged += OnAcceleration;
        gyrometer.ReadingChanged += OnGyro;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _gyrometer.ReadingChanged -= OnGyro;
            _accelerometer.ReadingChanged -= OnAcceleration;
            // The report interval is shared by every client of the sensor, so the value found at
            // acquisition goes back rather than a fixed default.
            _gyrometer.ReportInterval = _gyrometerInterval;
            _accelerometer.ReportInterval = _accelerometerInterval;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            DeviceTrace.Failure("motion", "WinRT sensor release", ex);
        }
    }

    public static WinRtMotionSession? TryStart(MotionSampleBuilder builder, Action<MotionSample> publish)
    {
        try
        {
            var gyrometer = Gyrometer.GetDefault();
            var accelerometer = Accelerometer.GetDefault();
            if (gyrometer is null || accelerometer is null)
            {
                DeviceTrace.Info("motion",
                    $"WinRT sensors: gyrometer={gyrometer is not null}, accelerometer={accelerometer is not null}.");
                return null;
            }

            DeviceTrace.Info("motion",
                $"WinRT IMU active: gyro {gyrometer.MinimumReportInterval} ms, accel "
                + $"{accelerometer.MinimumReportInterval} ms minimum interval.");
            return new WinRtMotionSession(gyrometer, accelerometer, builder, publish);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException
                                       or UnauthorizedAccessException)
        {
            DeviceTrace.Failure("motion", "WinRT sensor discovery failed", ex);
            return null;
        }
    }

    private void OnAcceleration(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            _acceleration = new Vector3((float)reading.AccelerationX, (float)reading.AccelerationY,
                (float)reading.AccelerationZ);
        }
    }

    private void OnGyro(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        MotionSample sample;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            sample = _builder.Build(new MotionSensorReading(
                new Vector3((float)reading.AngularVelocityX, (float)reading.AngularVelocityY,
                    (float)reading.AngularVelocityZ),
                _acceleration,
                reading.Timestamp.ToUniversalTime()));
        }

        // Straight to the service, which only stores the latest reading: no queue and no thread hop.
        _publish(sample);
    }
}
