using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Input;

/// <summary>Turns sensor-space IMU readings into canonical motion samples.</summary>
/// <remarks>
///     One path for every handheld: HC's saturation clip, the device's axis map, then the measured
///     zero-rate offset subtracted. The offset calibrator works in the application basis; it needs only
///     the acceleration's magnitude and spread, which an axis map preserves. Keep one builder for the life
///     of the device, so a restarted sensor stream does not send two seconds of uncorrected drift before
///     the first rest window.
/// </remarks>
internal sealed class MotionSampleBuilder
{
    /// <summary>
    ///     HC's gyro threshold: an axis at or beyond 2000 degrees/second, the sensor's full scale, reads
    ///     as zero before anything else sees it (<c>IMUGyrometer.ReadingChanged</c>).
    /// </summary>
    public const float SaturationThreshold = 2000f;

    /// <summary>
    ///     A refined offset is logged only once it moved by more than one rest window can resolve, so a
    ///     settling estimate does not fill the log.
    /// </summary>
    private const float MinimumLoggedBiasChange = 0.05f;

    /// <summary>Roughly ten seconds of reports: still no offset by then means the device never held still.</summary>
    private const ulong UncalibratedReportSampleCount = 1000;

    private readonly Func<Vector3, Vector3> _accelerationToApplication;
    private readonly StationaryGyroBiasCalibrator _calibrator = new();
    private readonly Lock _gate = new();
    private readonly Func<Vector3, Vector3> _gyroToApplication;
    private ulong _count;
    private Vector3? _reportedBias;
    private bool _uncalibratedReported;

    /// <summary>Creates a builder for one device's axes.</summary>
    /// <param name="gyroToApplication">Maps sensor-space angular velocity to the application basis.</param>
    /// <param name="accelerationToApplication">Maps sensor-space acceleration to the application basis.</param>
    public MotionSampleBuilder(Func<Vector3, Vector3> gyroToApplication,
        Func<Vector3, Vector3> accelerationToApplication)
    {
        ArgumentNullException.ThrowIfNull(gyroToApplication);
        ArgumentNullException.ThrowIfNull(accelerationToApplication);
        _gyroToApplication = gyroToApplication;
        _accelerationToApplication = accelerationToApplication;
    }

    /// <summary>The measured zero-rate offset in application-basis degrees per second, or null before calibration.</summary>
    public Vector3? Bias => _calibrator.Bias;

    /// <summary>Zeroes an axis at or beyond the sensor's full scale, as HC does.</summary>
    /// <param name="value">Angular velocity in degrees per second.</param>
    /// <returns>The value with saturated axes zeroed.</returns>
    public static Vector3 ClipSaturated(Vector3 value)
    {
        return new Vector3(Clip(value.X), Clip(value.Y), Clip(value.Z));

        static float Clip(float axis)
        {
            return Math.Abs(axis) >= SaturationThreshold ? 0f : axis;
        }
    }

    /// <summary>Builds one sample. Safe to call from several sensor threads.</summary>
    /// <param name="reading">Fresh sensor-space gyro in degrees per second and optional acceleration in g.</param>
    /// <returns>
    ///     An application-basis sample preserving the sensor timestamp. Without acceleration, existing bias
    ///     is subtracted but no new calibration is learned; absent acceleration is flagged and zero-filled.
    /// </returns>
    public MotionSample Build(MotionSensorReading reading)
    {
        MotionSample sample;
        string? diagnostic;
        lock (_gate)
        {
            _count++;
            var gyro = _gyroToApplication(ClipSaturated(reading.AngularVelocity));
            Vector3? acceleration = reading.Acceleration is { } raw ? _accelerationToApplication(raw) : null;
            var corrected = acceleration is { } measured
                ? _calibrator.Correct(gyro, measured)
                : _calibrator.Bias is { } known
                    ? gyro - known
                    : gyro;
            diagnostic = ReportCalibration();
            sample = new MotionSample
            {
                GyroX = corrected.X,
                GyroY = corrected.Y,
                GyroZ = corrected.Z,
                HasGyro = true,
                AccelX = acceleration?.X ?? 0,
                AccelY = acceleration?.Y ?? 0,
                AccelZ = acceleration?.Z ?? 0,
                HasAccelerometer = acceleration.HasValue,
                SensorTimestamp = reading.Timestamp
            };
        }

        if (diagnostic is not null)
        {
            DeviceTrace.Info("motion", diagnostic);
        }

        return sample;
    }

    private string? ReportCalibration()
    {
        if (_calibrator.Bias is { } bias)
        {
            if (_reportedBias is not { } prior || (bias - prior).Length() > MinimumLoggedBiasChange)
            {
                _reportedBias = bias;
                return $"Gyroscope zero-rate offset measured at "
                       + $"({bias.X:F3}, {bias.Y:F3}, {bias.Z:F3}) degrees/second.";
            }
        }
        else if (!_uncalibratedReported && _count >= UncalibratedReportSampleCount)
        {
            _uncalibratedReported = true;
            return $"Gyroscope still uncorrected after {_count} reports: no "
                   + $"{StationaryGyroBiasCalibrator.WindowSampleCount}-report rest window "
                   + "has occurred yet, so its zero-rate offset remains unmeasured.";
        }

        return null;
    }
}
