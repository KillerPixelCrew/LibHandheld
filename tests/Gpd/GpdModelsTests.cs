using System.Numerics;
using LibHandheld.Families.Gpd;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Gpd;

public sealed class GpdModelsTests
{
    [Fact]
    public void EveryConcreteHcSelectorIsUniqueAndDetectable()
    {
        Assert.Equal(20, GpdModels.All.Count);
        Assert.Equal(20, GpdModels.All.Select(model => model.Definition.Id).Distinct().Count());
        foreach (var model in GpdModels.All)
        foreach (var product in model.Products)
        {
            var identity = new DeviceIdentitySnapshot
            {
                SystemManufacturer = "GPD", SystemProduct = product, ProcessorName = model.Processors.FirstOrDefault()
            };
            Assert.Same(model, GpdModels.Match(identity));
        }
    }

    [Theory]
    [InlineData("G1618-04", "AMD Ryzen 7 8840U w/ Radeon 780M Graphics", "gpd-win4-8840u")]
    [InlineData("G1619-05", "AMD Ryzen 5 7640U w/ Radeon 760M Graphics", "gpd-win-max2-7640u")]
    public void SharedBoardIdsSelectTheExactCpuProtocol(string product, string cpu, string expected)
    {
        Assert.Equal(expected, GpdModels.Match(new DeviceIdentitySnapshot
            { SystemManufacturer = "GPD", SystemProduct = product, ProcessorName = cpu })?.Definition.Id);
        Assert.Null(GpdModels.Match(new DeviceIdentitySnapshot
            { SystemManufacturer = "GPD", SystemProduct = product, ProcessorName = "Unknown CPU" }));
    }

    [Fact]
    public void Mini8840AndMaxIntelKeepTheirOwnPublishedSensorMatrices()
    {
        var raw = new Vector3(1, 2, 3);
        var mini = GpdModels.All.Single(model => model.Definition.Id == "gpd-win-mini-8840u");
        Assert.Equal(new Vector3(1, -3, 2), mini.Motion!.Gyro.Apply(raw));
        var intel = GpdModels.All.Single(model => model.Definition.Id == "gpd-win-max2-intel");
        Assert.Equal(new Vector3(1, 3, 2), intel.Motion!.Gyro.Apply(raw));
    }

    [Fact]
    public void HcBoardSelectorWinsOverTheSystemMarketingName()
    {
        var model = GpdModels.Match(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "Marketing manufacturer", SystemProduct = "GPD WIN 4",
            BaseboardManufacturer = "GPD", BaseboardProduct = "G1618-04",
            ProcessorName = "AMD Ryzen 7 8840U w/ Radeon 780M Graphics"
        });
        Assert.Equal("gpd-win4-8840u", model?.Definition.Id);
    }

    [Fact]
    public void Win5EdgesAndConfigPacketsUseTheirActualByteLayouts()
    {
        var report = new byte[64];
        report[0] = 1;
        report[9] = 0x20;
        report[10] = 0x40;
        Assert.Equal(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, GpdWin5OemSource.Decode(report));
        var legacy = GpdControlsProtocol.V1Packet(0x11, 3);
        Assert.Equal(new byte[] { 1, 0xA5, 0x11, 0x5A, 0xEE, 0, 3, 0 }, legacy[..8]);
        var modern = GpdControlsProtocol.V2Packet(0x43, 3, 0x1234, [10, 20, 30]);
        Assert.Equal((byte)60, modern[6]);
        Assert.Equal((byte)0x34, modern[4]);
        Assert.Equal((byte)0x12, modern[5]);
    }

    [Fact]
    public void ACompanionMustShareTheBuiltInHubAndTouchIsActualContactState()
    {
        Assert.True(StandardControllerSource.SameBuiltInHub("PCIROOT(0)#USBROOT(0)#USB(4)#USB(1)",
            "PCIROOT(0)#USBROOT(0)#USB(4)#USB(2)"));
        Assert.False(StandardControllerSource.SameBuiltInHub("PCIROOT(0)#USBROOT(0)#USB(1)",
            "PCIROOT(0)#USBROOT(0)#USB(2)"));
        Assert.False(StandardControllerSource.SameBuiltInHub("PCIROOT(0)#USBROOT(0)#USB(4)#USB(1)",
            "PCIROOT(0)#USBROOT(0)#USB(5)#USB(2)"));
        var sample = StandardControllerCodec.Decode(default, CanonicalButtons.None, null, DateTimeOffset.UtcNow,
            new StandardTouchSample(true, true, 0.25f, -0.5f));
        Assert.Equal(CanonicalButtons.LeftPadTouch | CanonicalButtons.LeftPadClick, sample.Buttons);
        Assert.Equal(0.25f, sample.LeftPadX);
        Assert.Equal(-0.5f, sample.LeftPadY);
        Assert.Equal(CanonicalButtons.DPadUp | CanonicalButtons.DPadLeft, StandardControllerHidSource.Hat(7, 0, 7));
        Assert.Equal(CanonicalButtons.None, StandardControllerHidSource.Hat(8, 0, 7));
    }
}
