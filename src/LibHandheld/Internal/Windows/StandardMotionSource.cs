using System.Numerics;
using Windows.Devices.Sensors;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;

namespace LibHandheld.Internal.Windows;

/// <summary>Windows sensor acquisition with per-model coordinate transforms and retained calibration.</summary>
internal sealed class StandardMotionSource(StandardMotionProfile profile) : IDisposable
{
    private readonly MotionSampleBuilder _builder = new(profile.Gyro.Apply, profile.Acceleration.Apply);
    private readonly Lock _gate = new();
    private Accelerometer? _accel;
    private uint _accelInterval;
    private Vector3 _acceleration;
    private Gyrometer? _gyro;
    private uint _gyroInterval;
    private MotionSample? _latest;
    private IDisposable? _legacy;

    internal MotionSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest is { SensorTimestamp: { } timestamp } sample
                       && DateTimeOffset.UtcNow - timestamp <= TimeSpan.FromMilliseconds(250)
                    ? sample
                    : null;
            }
        }
    }

    public void Dispose()
    {
        var gyro = _gyro;
        var accel = _accel;
        _gyro = null;
        _accel = null;
        if (gyro is not null)
        {
            gyro.ReadingChanged -= GyroChanged;
            gyro.ReportInterval = _gyroInterval;
        }

        if (accel is not null)
        {
            accel.ReadingChanged -= AccelerationChanged;
            accel.ReportInterval = _accelInterval;
        }

        _legacy?.Dispose();
        _legacy = null;
        lock (_gate)
        {
            _latest = null;
        }
    }

    internal bool Start()
    {
        if (_gyro is not null || _legacy is not null)
        {
            return true;
        }

        var gyro = Gyrometer.GetDefault();
        var acceleration = Accelerometer.GetDefault();
        if (gyro is not null && acceleration is not null)
        {
            _gyro = gyro;
            _accel = acceleration;
            _gyroInterval = gyro.ReportInterval;
            _accelInterval = acceleration.ReportInterval;
            gyro.ReportInterval = Math.Max(1u, gyro.MinimumReportInterval);
            acceleration.ReportInterval = Math.Max(1u, acceleration.MinimumReportInterval);
            if (acceleration.GetCurrentReading() is { } first)
            {
                _acceleration = new Vector3((float)first.AccelerationX, (float)first.AccelerationY,
                    (float)first.AccelerationZ);
            }

            acceleration.ReadingChanged += AccelerationChanged;
            gyro.ReadingChanged += GyroChanged;
            return true;
        }

        if (LegacyMotionSensors.TryOpen([LegacyMotionSensorSource.Standard]) is not { } sensors)
        {
            return false;
        }

        _legacy = LegacyMotionStream.Start(sensors, reading =>
        {
            lock (_gate)
            {
                _latest = _builder.Build(reading);
            }
        });
        return true;
    }

    private void AccelerationChanged(Accelerometer sensor, AccelerometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            _acceleration = new Vector3((float)reading.AccelerationX, (float)reading.AccelerationY,
                (float)reading.AccelerationZ);
        }
    }

    private void GyroChanged(Gyrometer sensor, GyrometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            if (_gyro is null)
            {
                return;
            }

            _latest = _builder.Build(new MotionSensorReading(
                new Vector3((float)reading.AngularVelocityX, (float)reading.AngularVelocityY,
                    (float)reading.AngularVelocityZ),
                _acceleration, reading.Timestamp));
        }
    }
}
