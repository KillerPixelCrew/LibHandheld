using System.Numerics;
using LibHandheld.Families.Generic;
using LibHandheld.Families.Gpd;
using LibHandheld.Families.OneXPlayer;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Tests.Devices;

public sealed class MotionAxisMapTests
{
    [Fact]
    public void ApexRetainsHcLastAssignmentAndItsUnassignedZeroAxis()
    {
        var model = OneXPlayerModels.All.Single(item => item.Definition.Id == "oxp-apex");
        var raw = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(2, -3, 0), model.Motion!.Gyro.Apply(raw));
        Assert.Equal(new Vector3(-2, 3, 0), model.Motion.Acceleration.Apply(raw));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void HcInputDestinationsKeepSignsOnTheOutputAxes(int x, int y, int z)
    {
        var map = MotionAxisMap.FromInputToOutput(x, y, z, signX: -1, signY: 1, signZ: -1);
        var signedOutputs = new[] { -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ };
        Assert.Equal(signedOutputs[x], map.Apply(Vector3.UnitX));
        Assert.Equal(signedOutputs[y], map.Apply(Vector3.UnitY));
        Assert.Equal(signedOutputs[z], map.Apply(Vector3.UnitZ));
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(-1, 1, 2)]
    [InlineData(0, 1, 3)]
    public void NonPermutationHcMatricesCannotBeConvertedIntoInventedAxes(int x, int y, int z)
    {
        Assert.Throws<ArgumentException>(() => MotionAxisMap.FromInputToOutput(x, y, z,
            signX: 1, signY: 1, signZ: 1));
    }

    [Theory]
    [InlineData("gpd-win4-7640u", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win4-7840u", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win4-8640u", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win4-8840u", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win4-hx370", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win5", 3, -1, -2, -1, -3, 2)]
    [InlineData("gpd-win-max2-6800u", 3, -1, -2, -1, 3, 2)]
    [InlineData("gpd-win-max2-7640u", 3, -1, -2, -1, 3, 2)]
    [InlineData("gpd-win-max2-7840u", 3, -1, -2, -1, 3, 2)]
    [InlineData("gpd-win-max2-8640u", 3, -1, -2, -1, 3, 2)]
    [InlineData("gpd-win-max2-8840u", 3, -1, -2, -1, 3, 2)]
    [InlineData("gpd-win-mini-7640u", 2, -3, -1, 2, 3, 1)]
    [InlineData("gpd-win-mini-7840u", 2, -3, -1, 2, 3, 1)]
    [InlineData("gpd-win-mini-hx370", -3, -1, 2, -1, 3, 2)]
    [InlineData("oxp-mini-intel", 3, -1, -2, 1, -3, -2)]
    [InlineData("oxp-x2", 3, 1, -2, 3, 1, -2)]
    [InlineData("oxp-x2-mini-pro", 2, -3, -1, -1, -3, 2)]
    [InlineData("ayn-loki-mini-pro", 3, -1, -2, -1, 3, -2)]
    [InlineData("ayn-loki-zero", 3, -1, -2, -1, 3, -2)]
    [InlineData("ayn-loki-max-6600u", 3, -1, -2, -1, 3, -2)]
    [InlineData("ayn-loki-max-6800u", 3, -1, -2, -1, 3, -2)]
    public void EveryAffectedModelMatchesHcInputToOutputReadings(string id,
        int gx, int gy, int gz, int ax, int ay, int az)
    {
        var model = GpdModels.All.Concat(OneXPlayerModels.All).Concat(GenericModels.All)
            .Single(item => item.Definition.Id == id);
        var raw = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(gx, gy, gz), model.Motion!.Gyro.Apply(raw));
        Assert.Equal(new Vector3(ax, ay, az), model.Motion.Acceleration.Apply(raw));
    }

    [Theory]
    [InlineData("anbernic-win600", -2, -3, 1)]
    [InlineData("tectoy-zeenix-pro", 1, -3, 2)]
    public void HhdOutputSourceMatricesKeepTheirPublishedDirection(string id, int x, int y, int z)
    {
        var motion = GenericModels.All.Single(model => model.Definition.Id == id).Motion!;
        var expected = new Vector3(x, y, z);
        var raw = new Vector3(1, 2, 3);
        Assert.Equal(expected, motion.Gyro.Apply(raw));
        Assert.Equal(expected, motion.Acceleration.Apply(raw));
    }
}
