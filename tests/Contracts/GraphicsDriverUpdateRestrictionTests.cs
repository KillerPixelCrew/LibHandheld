using LibHandheld.Contracts;
using Xunit;

namespace LibHandheld.Tests.Contracts;

public sealed class GraphicsDriverUpdateRestrictionTests
{
    [Fact]
    public void FactsDefaultToAbsentAndOnlyMatchTheirExactVendorField()
    {
        Assert.Null(new HandheldDefinition("generic", "generic", "Generic", "generic").GraphicsDriverUpdateRestriction);
        var fact = GraphicsDriverUpdateRestrictions.AmdHandheld;
        Assert.NotNull(fact.ReasonFor("VEN_1002&DEV_15BF"));
        Assert.Null(fact.ReasonFor("VEN_8086&DEV_1002"));
        Assert.Null(fact.ReasonFor("XVEN_1002&DEV_15BF"));
        Assert.Null(fact.ReasonFor("VEN_10021&DEV_15BF"));
    }

    [Fact]
    public void DeckModelsShareTheirApuFactWhileTheSteamMachineAndIntelModelsDoNotInheritIt()
    {
        var models = HandheldDevice.Definitions;
        var lcd = models.Single(model => model.Id == "steamdeck-lcd").GraphicsDriverUpdateRestriction;
        Assert.Same(lcd, models.Single(model => model.Id == "steamdeck-oled").GraphicsDriverUpdateRestriction);
        Assert.Null(models.Single(model => model.Id == "steam-machine").GraphicsDriverUpdateRestriction);
        Assert.All(models.Where(model => model.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)),
            model => Assert.Null(model.GraphicsDriverUpdateRestriction));
    }
}
