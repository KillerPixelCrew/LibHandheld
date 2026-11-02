using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
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

    /// <summary>Opens or closes the sensors to match whether anything reads motion.</summary>
    /// <param name="wanted">Whether a consumer reads motion.</param>
    void SetWanted(bool wanted)
    {
    }
}

/// <summary>The Ally IMU through Windows sensors.</summary>
/// <remarks>
///     HC reads the WinRT <c>Gyrometer</c> and <c>Accelerometer</c> first and falls back to the legacy
///     Sensor API only when WinRT exposes neither (<c>IDevice.PullSensors</c>, <c>IDevice.cs:1152-1172</c>;
///     <c>WindowsSensorManager.cs:96-192</c>). The Device Lab run on RC73XA found BMI320 accelerometer and
///     gyrometer sensors on the standard legacy motion fields, which the SDK's legacy stream reads. The
///     sensors stream only while the host wants motion.
/// </remarks>
internal sealed class WindowsAllyMotionSource : IAllyMotionSource
{
    /// <summary>Outlives each session, so a restarted stream keeps the measured zero-rate offset.</summary>
    private readonly MotionSampleBuilder _builder;

    private readonly MotionDemandSession _session;
    private volatile Action<MotionSample>? _publish;

    /// <param name="model">Model axis transforms applied before the retained gyro-offset correction.</param>
    internal WindowsAllyMotionSource(AllyModel model)
    {
        _builder = new MotionSampleBuilder(model.Gyro.Apply, model.Accelerometer.Apply);
        _session = new MotionDemandSession(Open);
    }

    public ValueTask<bool> StartAsync(Action<MotionSample> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        _publish = publish;
        return ValueTask.FromResult(_session.Start());
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _session.Stop();
        return ValueTask.CompletedTask;
    }

    public void SetWanted(bool wanted)
    {
        _session.SetWanted(wanted);
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private IDisposable? Open()
    {
        if (_publish is not { } publish)
        {
            return null;
        }

        var builder = _builder;
        return WinRtMotionSession.TryStart(builder, publish)
               ?? (IDisposable?)(LegacyMotionSensors.TryOpen([LegacyMotionSensorSource.Standard]) is { } sensors
                   ? LegacyMotionStream.Start(sensors, reading => publish(builder.Build(reading)))
                   : null);
    }
}
