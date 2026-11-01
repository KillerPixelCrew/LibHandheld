using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Tests.Devices;

public sealed class PowerButtonEdgesTests
{
    [Fact]
    public void APressAndReleaseShareOneIdAndRepeatsPublishNothing()
    {
        var events = new List<(OemControlEdge Edge, string Id)>();
        var edges = new PowerButtonEdges((edge, id) => events.Add((edge, id)));

        edges.Report(true);
        edges.Report(true);
        edges.Report(false);
        edges.Report(true);
        edges.Report(false);

        Assert.Equal(4, events.Count);
        Assert.Equal((OemControlEdge.Pressed, "power:1"), events[0]);
        Assert.Equal((OemControlEdge.Released, "power:1"), events[1]);
        Assert.Equal((OemControlEdge.Pressed, "power:2"), events[2]);
        Assert.Equal((OemControlEdge.Released, "power:2"), events[3]);
    }

    [Fact]
    public void AReleaseWithoutAPressStillReadsAsOnePress()
    {
        var events = new List<(OemControlEdge Edge, string Id)>();
        var edges = new PowerButtonEdges((edge, id) => events.Add((edge, id)));

        edges.Report(false);

        Assert.Equal([(OemControlEdge.Pressed, "power:1"), (OemControlEdge.Released, "power:1")], events);
    }

    [Fact]
    public void StoppingReleasesAHeldPressOnlyOnce()
    {
        var events = new List<(OemControlEdge Edge, string Id)>();
        var edges = new PowerButtonEdges((edge, id) => events.Add((edge, id)));

        edges.Report(true);
        edges.Stop();
        edges.Stop();

        Assert.Equal([(OemControlEdge.Pressed, "power:1"), (OemControlEdge.Released, "power:1")], events);
    }
}
