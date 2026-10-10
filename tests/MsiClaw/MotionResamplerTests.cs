using System.Numerics;
using LibHandheld.Internal.Input;

namespace LibHandheld.Families.MsiClaw.Tests;

public sealed class MotionResamplerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderedReadingsPreserveIntegratedAngleAndIgnoreReversedOrDuplicateHistory()
    {
        var resampler = new GyroFrameResampler(TimeSpan.FromMilliseconds(50));
        Assert.True(resampler.OnReading(new Vector3(10, 0, 0), Start));
        resampler.FrameAverage(Start);
        Assert.True(resampler.OnReading(new Vector3(20, 0, 0), Start.AddMilliseconds(10)));
        Assert.False(resampler.OnReading(new Vector3(100, 0, 0), Start.AddMilliseconds(5)));
        Assert.False(resampler.OnReading(new Vector3(100, 0, 0), Start.AddMilliseconds(10)));
        Assert.Equal(15f, resampler.FrameAverage(Start.AddMilliseconds(20)).X, 4);
    }

    [Fact]
    public void FreshLateDeliveryUpdatesForwardVelocityWithoutRewindingAccountedHistory()
    {
        var resampler = new GyroFrameResampler(TimeSpan.FromMilliseconds(50));
        resampler.OnReading(new Vector3(10, 0, 0), Start);
        resampler.FrameAverage(Start);
        Assert.Equal(10f, resampler.FrameAverage(Start.AddMilliseconds(20)).X, 4);
        Assert.True(resampler.OnReading(new Vector3(20, 0, 0), Start.AddMilliseconds(15)));
        Assert.Equal(20f, resampler.FrameAverage(Start.AddMilliseconds(30)).X, 4);
    }

    [Fact]
    public void ExpiredHistoryCannotReviveVelocityAndAnExpiredFirstFrameIsZero()
    {
        var resampler = new GyroFrameResampler(TimeSpan.FromMilliseconds(50));
        resampler.OnReading(new Vector3(10, 0, 0), Start);
        Assert.Equal(Vector3.Zero, resampler.FrameAverage(Start.AddMilliseconds(100)));
        Assert.False(resampler.OnReading(new Vector3(100, 0, 0), Start.AddMilliseconds(20)));
        Assert.Equal(Vector3.Zero, resampler.FrameAverage(Start.AddMilliseconds(120)));
    }
}
