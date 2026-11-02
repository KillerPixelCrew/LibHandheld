using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;

namespace LibHandheld.Internal.Windows;

/// <summary>Windows sensor acquisition with per-model coordinate transforms and retained calibration.</summary>
/// <remarks>
///     The WinRT pair is preferred; the legacy Sensor API is read only when WinRT exposes neither. The
///     sensors stream only while the host wants motion (<see cref="SetWanted" />).
/// </remarks>
internal sealed class StandardMotionSource : IDisposable
{
    private readonly MotionSampleBuilder _builder;
    private readonly Lock _gate = new();
    private readonly MotionDemandSession _session;
    private MotionSample? _latest;

    internal StandardMotionSource(StandardMotionProfile profile)
    {
        _builder = new MotionSampleBuilder(profile.Gyro.Apply, profile.Acceleration.Apply);
        _session = new MotionDemandSession(Open);
    }

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
        _session.Stop();
        Clear();
    }

    internal bool Start()
    {
        return _session.Start();
    }

    /// <summary>Opens or closes the sensors to match whether anything reads motion.</summary>
    /// <param name="wanted">Whether a consumer reads motion.</param>
    internal void SetWanted(bool wanted)
    {
        _session.SetWanted(wanted);
        if (!wanted)
        {
            Clear();
        }
    }

    private IDisposable? Open()
    {
        return WinRtMotionSession.TryStart(_builder, Store)
               ?? (IDisposable?)(LegacyMotionSensors.TryOpen([LegacyMotionSensorSource.Standard]) is { } sensors
                   ? LegacyMotionStream.Start(sensors, reading => Store(_builder.Build(reading)))
                   : null);
    }

    private void Clear()
    {
        lock (_gate)
        {
            _latest = null;
        }
    }

    private void Store(MotionSample sample)
    {
        lock (_gate)
        {
            _latest = sample;
        }
    }
}
