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
