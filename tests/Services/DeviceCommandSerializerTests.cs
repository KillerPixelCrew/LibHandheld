using System.Collections.Concurrent;
using System.Diagnostics;

namespace LibHandheld.Tests.Services;

public sealed class DeviceCommandSerializerTests
{
    [Fact]
    public async Task LiveTelemetryRunsInsideFreshnessWindowWithoutAcceleratingSettingsObservation()
    {
        var started = Stopwatch.GetTimestamp();
        var observations = new ConcurrentQueue<(bool Telemetry, TimeSpan Elapsed)>();
        var thirdTelemetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetryCount = 0;
        using var serializer = new DeviceCommandSerializer("fixture",
            () => new CapabilityDescriptorSet { Descriptors = [] },
            (_, _) =>
            {
                observations.Enqueue((false, Stopwatch.GetElapsedTime(started)));
                settings.TrySetResult();
                return ValueTask.CompletedTask;
            },
            _ => ValueTask.CompletedTask,
            _ =>
            {
                observations.Enqueue((true, Stopwatch.GetElapsedTime(started)));
                if (Interlocked.Increment(ref telemetryCount) == 3)
                {
                    thirdTelemetry.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });
        serializer.StartObservation();
        try
        {
            await thirdTelemetry.Task.WaitAsync(TimeSpan.FromSeconds(9));
            Assert.False(settings.Task.IsCompleted);
            await settings.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        finally
        {
            serializer.StopObservation();
            await serializer.RunAsync(() => ValueTask.CompletedTask, CancellationToken.None);
        }

        var telemetry = observations.Where(value => value.Telemetry).ToArray();
        Assert.InRange(telemetry[0].Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        for (var index = 1; index < telemetry.Length; index++)
        {
            Assert.InRange(telemetry[index].Elapsed - telemetry[index - 1].Elapsed,
                TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        var full = Assert.Single(observations, value => !value.Telemetry);
        Assert.True(full.Elapsed >= TimeSpan.FromSeconds(10));
    }
}
