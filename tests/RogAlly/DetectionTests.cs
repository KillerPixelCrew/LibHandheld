// SPDX-License-Identifier: MIT

using LibHandheld.Families.RogAlly.Tests.Fakes;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Plugin;

namespace LibHandheld.Families.RogAlly.Tests;

public sealed class DetectionTests
{
    [Theory]
    [InlineData("RC71L", "rc71l")]
    [InlineData("RC72LA", "rc72la")]
    [InlineData("RC72L", "rc72la")]
    [InlineData("RC73YA", "rc73ya")]
    [InlineData("RC73XA", "rc73xa")]
    [InlineData(" rc73xa ", "rc73xa")]
    public async Task EachAllyBoardMatchesItsOwnDefinition(string product, string definition)
    {
        await using var plugin = new AllyFakesPlugin().Plugin;
        var result = await plugin.DetectAsync(Context("ASUSTeK COMPUTER INC.", product), CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal(definition, result.DeviceDefinitionId);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "GZ302EA")] // ROG Flow Z13, another ASUS AMD handheld-class board
    [InlineData("ASUSTeK COMPUTER INC.", "G614JV")] // ROG Strix laptop
    [InlineData("ASUSTeK COMPUTER INC.", "RC71")] // prefix of a real board
    [InlineData("ASUSTeK COMPUTER INC.", "RC73XA2")] // extension of a real board
    [InlineData("ASUSTeK COMPUTER INC.", "")]
    [InlineData("Micro-Star International Co., Ltd.", "RC71L")] // right board name, wrong maker
    [InlineData("LENOVO", "RC72LA")]
    [InlineData("", "RC72LA")]
    public async Task EverythingElseIsDeclined(string manufacturer, string product)
    {
        await using var plugin = new AllyFakesPlugin().Plugin;
        var result = await plugin.DetectAsync(Context(manufacturer, product), CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Null(result.DeviceDefinitionId);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void ModelTableCoversEachBoardExactlyOnce()
    {
        var boards = AllyModels.All.SelectMany(model => model.BaseboardProducts).ToArray();

        Assert.Equal(boards.Length, boards.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["RC71L", "RC72LA", "RC72L", "RC73YA", "RC73XA"], boards);
        Assert.Equal(AllyModels.All.Count, AllyModels.All.Select(model => model.DefinitionId).Distinct().Count());
    }

    private static PluginDetectionContext Context(string manufacturer, string product)
    {
        return new PluginDetectionContext
        {
            Identity = new DeviceIdentitySnapshot
            {
                BaseboardManufacturer = manufacturer,
                BaseboardProduct = product
            }
        };
    }

    private sealed class AllyFakesPlugin
    {
        public AllyDeviceEngine Plugin { get; } = new AllyFakeHardware().CreatePlugin();
    }
}
