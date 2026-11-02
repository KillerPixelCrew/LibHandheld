using LibHandheld.Internal.Input;

namespace LibHandheld.Tests.Services;

public sealed class MotionDemandSessionTests
{
    [Fact]
    public void StartWithoutDemandProvesTheSensorThenClosesIt()
    {
        var sensors = new FakeSensors();
        MotionDemandSession session = new(sensors.Open);
        session.SetWanted(false);

        Assert.True(session.Start());

        Assert.Equal(1, sensors.Opened);
        Assert.Equal(1, sensors.Closed);
        Assert.False(session.Streaming);
    }

    [Fact]
    public void DemandOpensAndClosesTheSessionOnlyWhileOwned()
    {
        var sensors = new FakeSensors();
        MotionDemandSession session = new(sensors.Open);
        session.SetWanted(false);
        session.SetWanted(true);
        Assert.Equal(0, sensors.Opened);

        session.Start();
        Assert.True(session.Streaming);
        session.SetWanted(false);
        Assert.False(session.Streaming);
        session.SetWanted(true);
        Assert.True(session.Streaming);
        session.Stop();

        Assert.False(session.Streaming);
        Assert.Equal(2, sensors.Opened);
        Assert.Equal(2, sensors.Closed);
    }

    [Fact]
    public void AMissingSensorReportsUnavailable()
    {
        MotionDemandSession session = new(() => null);

        Assert.False(session.Start());
        session.SetWanted(true);

        Assert.False(session.Streaming);
    }

    private sealed class FakeSensors
    {
        public int Opened { get; private set; }

        public int Closed { get; private set; }

        public IDisposable Open()
        {
            Opened++;
            return new Session(this);
        }

        private sealed class Session(FakeSensors owner) : IDisposable
        {
            public void Dispose()
            {
                owner.Closed++;
            }
        }
    }
}
