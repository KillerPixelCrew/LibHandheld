using System.Numerics;
using LibHandheld.Families.Generic;

namespace LibHandheld.Tests.Generic;

public sealed class GenericModelsTests
{
    [Theory]
    [InlineData("Loki MiniPro", "AMD Ryzen 3 7320U", "ayn-loki-mini-pro")]
    [InlineData("Loki Zero", "AMD Athlon Silver 3050U", "ayn-loki-zero")]
    [InlineData("Loki Max", "AMD Ryzen 5 6600U with Radeon Graphics", "ayn-loki-max-6600u")]
    [InlineData("Loki Max", "AMD Ryzen 7 6800U with Radeon Graphics", "ayn-loki-max-6800u")]
    public void ExactLokiBoardAndCpuSelectorsKeepTheirOwnFanBounds(string product, string cpu, string expected)
    {
        var profile = GenericModels.Match(new DeviceIdentitySnapshot
            { BaseboardManufacturer = "AYN", BaseboardProduct = product, ProcessorName = cpu });
        Assert.NotNull(profile);
        Assert.Equal(expected, profile.Definition.Id);
        Assert.Single(profile.ServiceFactories);
        Assert.Equal(new Vector3(2, -3, -1), profile.Motion!.Gyro.Apply(new Vector3(1, 2, 3)));
    }

    [Fact]
    public void UnknownCpuMachineRemainsPassive()
    {
        var profile = GenericModels.Match(new DeviceIdentitySnapshot
            { SystemManufacturer = "Unknown", SystemProduct = "Unknown", ProcessorName = "AMD Ryzen 7 7840U" });
        Assert.Null(profile);
        Assert.Null(GenericModels.Match(new DeviceIdentitySnapshot { ProcessorName = "Unknown processor" }));
    }

    [Fact]
    public void HhdProductIdentityIsNotMistakenForAnHcBoardName()
    {
        var profile = GenericModels.Match(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "TecToy", SystemProduct = "Zeenix Pro", BaseboardProduct = "Unrelated",
            ProcessorName = "AMD Ryzen 7 6800U"
        });
        Assert.Equal("tectoy-zeenix-pro", profile?.Definition.Id);
        Assert.True(profile!.Definition.HasController);
        Assert.Null(GenericModels.Match(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "Other", SystemProduct = "Desktop", BaseboardProduct = "Unrelated",
            ProcessorName = "AMD Ryzen 7 6800U"
        }));
    }

    [Fact]
    public void TecnoUsesMsbNumberedHhdBitsAndLokiDutyUsesIts128StepScale()
    {
        byte[] report = [0x74, 0, 0, 0, 0, 3, 0, 32];
        Assert.Equal((byte)7, TecnoButtonSource.Decode(report));
        report[0] = 0x75;
        Assert.Equal((byte)0, TecnoButtonSource.Decode(report));
        Assert.Equal((byte)128, AynNativeService.EncodeDuty(100));
        Assert.Equal((byte)64, AynNativeService.EncodeDuty(50));
    }
}
