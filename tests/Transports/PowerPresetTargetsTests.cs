using LibHandheld.Families.Ayaneo;
using LibHandheld.Families.Generic;
using LibHandheld.Families.OneXPlayer;

namespace LibHandheld.Tests.Transports;

public sealed class PowerPresetTargetsTests
{
    [Fact]
    public void OneXDefaultKeepsHcsIndependentFastTarget()
    {
        var model = OneXPlayerModels.All.First(model => model.SourceFamilyId == "OneXPlayerMiniAMD");
        var preset = Assert.Single(model.PowerPresets);
        Assert.Equal(15, preset.SustainedWatts);
        Assert.Equal(15, preset.SlowWatts);
        Assert.Equal(20, preset.FastWatts);
    }

    [Fact]
    public void OneXPerformanceClampsAllThreeHcTargetsToModelRange()
    {
        var model = OneXPlayerModels.All.First(model => model.SourceFamilyId == "OneXPlayerX1AMD");
        var preset = model.PowerPresets.Single(preset => preset.Id == "performance");
        Assert.Equal(30, preset.SustainedWatts);
        Assert.Equal(30, preset.SlowWatts);
        Assert.Equal(30, preset.FastWatts);
    }

    [Theory]
    [InlineData("3-8840u", 30)]
    [InlineData("3-hx370", 45)]
    [InlineData("next-2", 45)]
    public void ModernAyaneoProfilesUseTheirActualOverriddenTargets(string modelId, int performance)
    {
        var model = AyaneoModels.All.Single(model => model.Profile.Definition.Id == "ayaneo." + modelId).Profile;
        var battery = model.PowerPresets.Single(preset => preset.Id == "battery");
        var balanced = model.PowerPresets.Single(preset => preset.Id == "balanced");
        var maximum = model.PowerPresets.Single(preset => preset.Id == "performance");
        Assert.Equal(15, battery.FastWatts);
        Assert.Equal(30, balanced.FastWatts);
        Assert.Equal(performance, maximum.SustainedWatts);
        Assert.Equal(performance, maximum.SlowWatts);
        Assert.Equal(performance, maximum.FastWatts);
    }

    [Fact]
    public void MinisforumProfilesCarryAllThreeEqualTargets()
    {
        var model = GenericModels.All.Single(model => model.Definition.Id == "minisforum-v3");
        Assert.All(model.PowerPresets, preset => Assert.Equal(preset.SlowWatts, preset.FastWatts));
    }
}
