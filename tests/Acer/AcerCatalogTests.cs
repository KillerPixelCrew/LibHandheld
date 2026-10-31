using LibHandheld.Families.Acer;

namespace LibHandheld.Tests.Acer;

public sealed class AcerCatalogTests
{
    /// The Atlas fans stall below 10% and the SoC idles at 52..66 °C, so every shape rests at a spinning
    /// floor through 50 °C and reaches full speed at the processor's 100 °C limit.
    [Fact]
    public void OnlyTheAtlasCarriesMeasuredFanPresetsAndTheyIdleQuietlyAndRiseWithoutDips()
    {
        var presets = AcerModels.Atlas8.Definition.FanCurvePresets;
        Assert.NotNull(presets);
        foreach (var shape in new[] { presets.Quiet, presets.Default, presets.Aggressive })
        {
            Assert.Equal(11, shape.Count);
            Assert.InRange(shape[5], 10, 35);
            Assert.Equal(100, shape[10]);
            Assert.True(shape.Zip(shape.Skip(1)).All(pair => pair.Second >= pair.First));
            Assert.True(shape.All(duty => duty >= 10));
        }

        Assert.Equal(45, presets.Default[7]);
        Assert.Equal((3d, 5, 2, 10),
            (presets.HysteresisCelsius, presets.MaximumRisePerTick, presets.MaximumFallPerTick,
                presets.AverageSamples));
        Assert.Same(presets, HandheldDevice.Definitions.Single(item => item.Id == AcerModels.Atlas8.Definition.Id)
            .FanCurvePresets);
        Assert.All(HandheldDevice.Definitions.Where(item => item.Id != AcerModels.Atlas8.Definition.Id),
            item => Assert.Null(item.FanCurvePresets));
    }

    /// Predator Sense is exact Atlas knowledge; the other makers' apps are named on any machine.
    [Fact]
    public void ManagerAppsListEachFamilysOwnAppInFamilyOrderAndBindPredatorSenseToTheAtlas()
    {
        Assert.Equal(["msi-center-m", "armoury-crate", "legion-space", "zotac-launcher", "acer-predator-handheld"],
            HandheldDevice.ManagerApps.Select(app => app.Id));
        var predator = HandheldDevice.ManagerApps.Single(app => app.Id == "acer-predator-handheld");
        Assert.Equal(AcerModels.Atlas8.Definition.Id, predator.DefinitionId);
        Assert.Equal(10, predator.Services.Count);
        Assert.Equal(3, predator.Tasks.Count);
        Assert.Equal(2, predator.PackageFamilies.Count);
        Assert.Equal(@"HID\VID_0502&PID_F901&MI_02&COL01", predator.DedicatedKeyboardDeviceId);
        Assert.All(HandheldDevice.ManagerApps.Where(app => app != predator), app =>
        {
            Assert.Null(app.DefinitionId);
            Assert.Empty(app.PackageFamilies);
            Assert.Null(app.DedicatedKeyboardDeviceId);
        });
    }
}
