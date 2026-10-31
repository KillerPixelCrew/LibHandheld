using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;

namespace LibHandheld.Internal.Windows;

/// <summary>Windows sensor acquisition with per-model coordinate transforms and retained calibration.</summary>
/// <remarks>The WinRT pair is preferred; the legacy Sensor API is read only when WinRT exposes neither.</remarks>
internal sealed class StandardMotionSource(StandardMotionProfile profile) : IDisposable
{
    private readonly MotionSampleBuilder _builder = new(profile.Gyro.Apply, profile.Acceleration.Apply);
    private readonly Lock _gate = new();
    private MotionSample? _latest;
    private IDisposable? _session;

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
        _session?.Dispose();
        _session = null;
        lock (_gate)
        {
            _latest = null;
        }
    }

    internal bool Start()
    {
        if (_session is not null)
        {
            return true;
        }

        _session = WinRtMotionSession.TryStart(_builder, Store)
                   ?? (IDisposable?)(LegacyMotionSensors.TryOpen([LegacyMotionSensorSource.Standard]) is { } sensors
                       ? LegacyMotionStream.Start(sensors, reading => Store(_builder.Build(reading)))
                       : null);
        return _session is not null;
    }

    private void Store(MotionSample sample)
    {
        lock (_gate)
        {
            _latest = sample;
        }
    }
}
