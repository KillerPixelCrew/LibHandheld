using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Devices.Sensors;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Windows;

/// <summary>WinRT gyrometer and accelerometer at their fastest report interval.</summary>
/// <remarks>
///     Discovery failures are traced and return no session, so a missing or refused sensor leaves motion
///     unavailable rather than faulting its owner. Samples carry UTC sensor timestamps and are handed straight
///     to the owner's callback on the sensor thread, with no queue and no logging.
/// </remarks>
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

    /// <summary>Opens the default sensor pair, or returns null when either is absent or discovery fails.</summary>
    /// <param name="builder">Applies the owner's axis transforms and retained gyro-offset correction.</param>
    /// <param name="publish">Takes each sample on the sensor's own thread; it must return quickly.</param>
    /// <returns>The running session, or null when WinRT exposes no usable pair.</returns>
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

        // Straight to the owner, which only stores the latest reading: no queue and no thread hop.
        _publish(sample);
    }
}
