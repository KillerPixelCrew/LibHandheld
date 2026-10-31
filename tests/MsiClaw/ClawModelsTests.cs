using System.Numerics;
using LibHandheld.Families.MsiClaw.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using static LibHandheld.Families.MsiClaw.Tests.Builders.ClawCommands;

namespace LibHandheld.Families.MsiClaw.Tests;

[Collection("plugin-trace")]
public sealed class ClawModelsTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("MS-1T41", "ms-1t41")]
    [InlineData("MS-1T42", "ms-1t42")]
    [InlineData("MS-1T52", "ms-1t52")]
    [InlineData("MS-1T8K", "ms-1t8k")]
    [InlineData("MS-1T91", "ms-1t91")]
    public void EveryHcBoardSelectsItsModelWhateverTheSku(string board, string definition)
    {
        var model = ClawModels.Find(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = board,
            SystemSku = "unknown"
        });

        Assert.Equal(definition, model?.DefinitionId);
    }

    [Theory]
    [InlineData("MICRO-STAR INTERNATIONAL CO., LTD.", "MS-1T53")]
    [InlineData("ASUSTeK COMPUTER INC.", "MS-1T52")]
    public void AnotherBoardOrManufacturerMatchesNothing(string manufacturer, string board)
    {
        Assert.Null(ClawModels.Find(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer,
            BaseboardProduct = board
        }));
    }

    [Fact]
    public void DefinitionIdsAndBoardsAreUniqueAndOnlyTheReferenceUnitIsVerified()
    {
        Assert.Equal(ClawModels.All.Count, ClawModels.All.Select(model => model.DefinitionId).Distinct().Count());
        Assert.Equal(ClawModels.All.Count, ClawModels.All.Select(model => model.BoardProduct).Distinct().Count());
        Assert.Equal([ClawModels.Claw8A2Vm], ClawModels.All.Where(model => model.HardwareVerified));
    }

    [Fact]
    public void EveryPresetFitsItsModelsPowerRange()
    {
        foreach (var model in ClawModels.All)
        {
            Assert.All(model.PowerPresets, preset =>
            {
                Assert.InRange(preset.SustainedWatts, model.MinimumWatts, model.MaximumWatts);
                Assert.InRange(preset.SlowWatts, preset.SustainedWatts, model.MaximumWatts);
            });
        }
    }

    [Theory]
    [InlineData("0163", 0x01FA)]
    [InlineData("0166", 0x024A)]
    [InlineData("0211", 0x01FA)]
    [InlineData("0229", 0x024A)]
    [InlineData("0230", 0x024A)]
    [InlineData("0414", 0x024A)]
    [InlineData(null, 0x024A)]
    [InlineData("zz", 0x024A)]
    public void LightingProfileAddressFollowsHcsNearestFirmwareRow(string? revision, int address)
    {
        Assert.Equal(address, ClawModels.LightingProfileAddress(revision));
    }

    [Fact]
    public void ClawA1MFlipsTheAccelerometerLikeHcsDeviceJson()
    {
        var sample = WindowsMotionSourceTests.Build(
            ClawModels.A1M,
            new Vector3(1f, 2f, 3f),
            Timestamp,
            new Vector3(0.25f, 0.75f, -0.5f));

        Assert.Equal((1f, 3f, -2f), (sample.GyroX, sample.GyroY, sample.GyroZ));
        Assert.Equal((-0.25f, 0.5f, 0.75f), (sample.AccelX, sample.AccelY, sample.AccelZ));
    }

    [Fact]
    public void ClawBz2EmKeepsTheSwappedAccelerometerUnsigned()
    {
        var sample = WindowsMotionSourceTests.Build(
            ClawModels.A8Bz2Em,
            new Vector3(1f, 2f, 3f),
            Timestamp,
            new Vector3(0.25f, 0.75f, -0.5f));

        Assert.Equal((1f, 3f, -2f), (sample.GyroX, sample.GyroY, sample.GyroZ));
        Assert.Equal((0.25f, -0.5f, 0.75f), (sample.AccelX, sample.AccelY, sample.AccelZ));
    }

    [Fact]
    public async Task Bz2EmWritesItsBoostLimitToTheFastRegisterToo()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 20);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 20);
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em, TestTiming.NoDelay);

        var result = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(28)), 20, 28, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(
            [
                ClawHardwareFacts.PowerSustainedAddress, ClawHardwareFacts.PowerBoostAddress,
                ClawHardwareFacts.PowerFastAddress
            ],
            wmi.Writes.Select(write => write.Package[0]));
        Assert.Equal(28, wmi.ReadData(ClawHardwareFacts.PowerFastAddress));
    }

    [Fact]
    public async Task IntelModelsNeverTouchTheFastRegister()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);

        _ = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(33)), 20, 33, CancellationToken.None);

        Assert.DoesNotContain(wmi.Writes, write => write.Package[0] == ClawHardwareFacts.PowerFastAddress);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(46)]
    public async Task A1MRejectsLimitsOutsideItsRange(int watts)
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.A1M, TestTiming.NoDelay);

        var result = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(watts)), 20, watts, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task Cg3EmSelectsTheUserScenarioAtSix()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8ExCg3Em, TestTiming.NoDelay);

        var result = await power.ApplyScenarioAsync(
            Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("user")), "user", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(0xC6, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
    }
}
