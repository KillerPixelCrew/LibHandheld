// SPDX-License-Identifier: MIT

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
    public void EachAllyBoardMatchesItsOwnDefinition(string product, string definition)
    {
        var result = AllyModels.Match(Context("ASUSTeK COMPUTER INC.", product));

        Assert.NotNull(result);
        Assert.Equal(definition, result.DefinitionId);
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
    public void EverythingElseIsDeclined(string manufacturer, string product)
    {
        var result = AllyModels.Match(Context(manufacturer, product));

        Assert.Null(result);
    }

    [Fact]
    public void ModelTableCoversEachBoardExactlyOnce()
    {
        var boards = AllyModels.All.SelectMany(model => model.BaseboardProducts).ToArray();

        Assert.Equal(boards.Length, boards.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["RC71L", "RC72LA", "RC72L", "RC73YA", "RC73XA"], boards);
        Assert.Equal(AllyModels.All.Count, AllyModels.All.Select(model => model.DefinitionId).Distinct().Count());
    }

    private static DeviceIdentitySnapshot Context(string manufacturer, string product)
    {
        return new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer,
            BaseboardProduct = product
        };
    }
}
