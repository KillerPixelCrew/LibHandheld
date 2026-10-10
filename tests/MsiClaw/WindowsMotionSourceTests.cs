using System.Numerics;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw.Tests;

public sealed class WindowsMotionSourceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true)]
    [InlineData(49, true)]
    [InlineData(59, true)]
    [InlineData(499, true)]
    [InlineData(500, false)]
    [InlineData(1000, false)]
    [InlineData(-1, false)]
    public void UtcReportAdmissionPreservesOrdinaryJitterAndRejectsObsoleteOrFutureData(int age, bool accepted)
    {
        var now = new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(accepted, WindowsClawMotionSource.CanAcceptReading(now.AddMilliseconds(-age), now, null));
        Assert.False(WindowsClawMotionSource.CanAcceptReading(now, now, now));
        Assert.False(WindowsClawMotionSource.CanAcceptReading(now.AddMilliseconds(-5), now, now));
    }

    [Fact]
    public void PhysicalImuValuesUseTheSteamDeckApplicationAxisBasisOnce()
    {
        var sample = Build(ClawModels.Claw8A2Vm, new Vector3(1f, 2f, 3f), Timestamp,
            new Vector3(0.25f, 0.75f, -0.5f));

        Assert.True(sample.HasAccelerometer);
        Assert.Equal(0.25f, sample.AccelX);
        Assert.Equal(-0.5f, sample.AccelY);
        Assert.Equal(-0.75f, sample.AccelZ);
        Assert.Equal(1f, sample.GyroX);
        Assert.Equal(3f, sample.GyroY);
        Assert.Equal(-2f, sample.GyroZ);
        Assert.Equal(Timestamp, sample.SensorTimestamp);
    }

    [Fact]
    public void MissingAccelerometerDataIsNotApproximated()
    {
        var sample = Build(ClawModels.Claw8A2Vm, new Vector3(1f, 2f, 3f), Timestamp, null);

        Assert.False(sample.HasAccelerometer);
        Assert.Equal(0f, sample.AccelX);
        Assert.Equal(0f, sample.AccelY);
        Assert.Equal(0f, sample.AccelZ);
    }

    [Fact]
    public void SubDegreePhysicalGyroCrossesTheAxisTransformContinuously()
    {
        var sample = Build(ClawModels.Claw8A2Vm, new Vector3(0.07f, -0.14f, 0.21f), Timestamp, Vector3.UnitZ);

        Assert.Equal(0.07f, sample.GyroX);
        Assert.Equal(0.21f, sample.GyroY);
        Assert.Equal(0.14f, sample.GyroZ);
    }

    [Theory]
    [InlineData(1999f, 1999f)]
    [InlineData(2000f, 0f)]
    [InlineData(-2400f, 0f)]
    public void GyroAxesAtFullScaleAreZeroedAsInHc(float raw, float expected)
    {
        var clipped = MotionSampleBuilder.ClipSaturated(new Vector3(raw, 5f, raw));

        Assert.Equal((expected, 5f, expected), (clipped.X, clipped.Y, clipped.Z));
    }

    /// <summary>One sample through the same builder and axis map the Claw source uses.</summary>
    internal static MotionSample Build(ClawModel model, Vector3 gyro, DateTimeOffset stamp, Vector3? acceleration)
    {
        MotionSampleBuilder builder = new(
            raw => WindowsClawMotionSource.ToApplicationBasis(raw, model.GyroSigns),
            raw => WindowsClawMotionSource.ToApplicationBasis(raw, model.AccelerometerSigns));
        return builder.Build(new MotionSensorReading(gyro, acceleration, stamp));
    }
}
