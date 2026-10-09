using LibHandheld.Families.MsiClaw.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using static LibHandheld.Families.MsiClaw.Tests.Builders.ClawCommands;

namespace LibHandheld.Families.MsiClaw.Tests;

[Collection("plugin-trace")]
public sealed class ClawCapabilitiesTests
{
    /// <summary>The RGB profile address on the reference unit's MCU.</summary>
    private const ushort ReferenceLightingProfileAddress = 0x024A;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPowerWriteDisarmsWatchdogUntilAnExplicitCommand(bool reassert)
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var command = Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20));
        _ = await power.ApplyLimitsAsync(command, 20, 20, CancellationToken.None);
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        wmi.FailNextSetter = true;
        if (reassert)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                power.ReassertAsync(new PowerPair(30, 37, 0xC1), CancellationToken.None).AsTask());
        }
        else
        {
            var failed = await power.ApplyLimitsAsync(command, 25, 25, CancellationToken.None);
            Assert.Equal(CommandOutcome.Indeterminate, failed.Outcome);
        }

        wmi.Writes.Clear();
        var read = await power.ReadAsync(CancellationToken.None);
        Assert.Equal(read, power.Observe(read));
        await power.ReassertAsync(read, CancellationToken.None);
        Assert.Empty(wmi.Writes);
        var applied = await power.ApplyLimitsAsync(command, 20, 20, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, applied.Outcome);
        Assert.Equal(20, power.Observe(read).SustainedWatts);
    }

    [Theory]
    [InlineData("scenario")]
    [InlineData("fan-mode")]
    [InlineData("fan-curve")]
    public async Task RequiredReadTimeoutRejectsWithoutAWrite(string operation)
    {
        FakeWmiTransport wmi = new() { GetterTimeout = true };
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        ClawFanCapability fan = new(wmi);
        var result = operation switch
        {
            "scenario" => await power.ApplyScenarioAsync(
                Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("sport")), "sport",
                CancellationToken.None),
            "fan-mode" => await fan.ApplyModeAsync(
                Command(CapabilityIds.FanMode, null, CapabilityValue.Choice("automatic")), "automatic",
                CancellationToken.None),
            _ => await fan.ApplyCurveAsync(
                Command(CapabilityIds.FanCurve, null, CapabilityValue.Curve([])),
                Enumerable.Range(0, 6).Select(index => new CurvePoint(index * 10, index * 10)).ToArray(),
                CancellationToken.None)
        };
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData("charge")]
    [InlineData("power")]
    [InlineData("scenario")]
    [InlineData("fan")]
    public async Task AcceptedWmiWriteWithReadbackTimeoutStaysApplied(string operation)
    {
        FakeWmiTransport wmi = new();
        wmi.AfterSetter = (_, _) => wmi.GetterTimeout = true;
        var result = operation switch
        {
            "charge" => await new ClawChargeLimitCapability(wmi).ApplyAsync(
                Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), 60, CancellationToken.None),
            "power" => await new ClawPowerCapability(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay).ApplyLimitsAsync(
                Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)), 20, 20,
                CancellationToken.None),
            "scenario" => await new ClawPowerCapability(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay)
                .ApplyScenarioAsync(
                    Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("sport")), "sport",
                    CancellationToken.None),
            _ => await new ClawFanCapability(wmi).ApplyModeAsync(
                Command(CapabilityIds.FanMode, null, CapabilityValue.Choice("full-speed")), "full-speed",
                CancellationToken.None)
        };
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.NotEmpty(wmi.Writes);
    }

    [Fact]
    public async Task SetterTimeoutIsTransportFaultedAfterTheWriteBegan()
    {
        FakeWmiTransport wmi = new() { SetterTimeout = true };
        var result = await new ClawChargeLimitCapability(wmi).ApplyAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), 60, CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, result.Reason?.Code);
    }

    [Fact]
    public async Task McuTimeoutLeavesInitialLightingUnknownAndAcceptedWriteUnverified()
    {
        FakeMcuTransport mcu = new() { ReadFailure = new OperationCanceledException("MCU timeout") };
        ClawLightingCapability lighting = new(mcu, ReferenceLightingProfileAddress);
        Assert.Null(await lighting.ReadAsync(CancellationToken.None));
        var result = await lighting.ApplyAsync(
            Command(CapabilityIds.LightingBrightness, null, CapabilityValue.Integer(75)),
            current => current with { Brightness = 75 }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(75, lighting.Current?.Brightness);
    }

    [Fact]
    public async Task InitialReadPreservesCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClawObservation.TryAsync(
            new ClawPowerCapability(new FakeWmiTransport(), ClawModels.Claw8A2Vm, TestTiming.NoDelay).ReadAsync,
            "power", cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(8, 9, 20)]
    [InlineData(30, 37, 12)]
    [InlineData(8, 8, 37)]
    public async Task UnifiedPairChangesBothLimits(int sustained, int boost, int target)
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, sustained);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, boost);
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var command = Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(target));
        var result = await power.ApplyLimitsAsync(command, target, target, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(target, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(target, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Fact]
    public async Task ABoostCommandWritesThePairItCarriesAndReportsTheBoost()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 37);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 37);
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var command = Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(31));

        var result = await power.ApplyLimitsAsync(command, 25, 31, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(25, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(31, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(38)]
    public async Task BoostOutsideTheAcceptedRangeIsStillRefused(int watts)
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var command = Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(watts));

        var result = await power.ApplyLimitsAsync(command, 20, watts, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task PairReadbackMismatchLeavesTheWriteAndPublishesTheRequestedPair()
    {
        FakeWmiTransport wmi = new();
        wmi.AfterSetter = (_, package) =>
        {
            if (package[0] == ClawHardwareFacts.PowerBoostAddress && package[1] == 12)
            {
                wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 13);
            }
        };
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var command = Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(12));

        var result = await power.ApplyLimitsAsync(command, 12, 12, CancellationToken.None);
        var read = await power.ReadAsync(CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(13, read.BoostWatts);
        Assert.Equal((12, 12), (power.Observe(read).SustainedWatts, power.Observe(read).BoostWatts));
        Assert.Equal(
            [ClawHardwareFacts.PowerSustainedAddress, ClawHardwareFacts.PowerBoostAddress],
            wmi.Writes.Select(write => write.Package[0]));
    }

    [Fact]
    public async Task PowerWritesPl1BeforePl2AsHcDoesEvenWhenRaising()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 12);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 20);
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);

        _ = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(30)), 30, 30, CancellationToken.None);

        Assert.Equal(
            [ClawHardwareFacts.PowerSustainedAddress, ClawHardwareFacts.PowerBoostAddress],
            wmi.Writes.Select(write => write.Package[0]));
    }

    [Fact]
    public async Task ReassertWritesTheRequestedPairWhenTheEcReportsAnother()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        _ = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)), 20, 20, CancellationToken.None);
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        wmi.Writes.Clear();

        await power.ReassertAsync(await power.ReadAsync(CancellationToken.None), CancellationToken.None);

        Assert.Equal(20, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(2, wmi.Writes.Count);
    }

    [Fact]
    public void Encode_Lighting_ReplicatesThreeLogicalZonesAcrossNineProtocolIndices()
    {
        var payload = ClawLightingCapability.Encode(new LightingState(
            60,
            0x112233,
            0x445566,
            0x778899));

        Assert.Equal(32, payload.Length);
        Assert.Equal([0x11, 0x22, 0x33], payload[5..8]);
        Assert.Equal([0x11, 0x22, 0x33], payload[14..17]);
        Assert.Equal([0x44, 0x55, 0x66], payload[17..20]);
        Assert.Equal([0x44, 0x55, 0x66], payload[26..29]);
        Assert.Equal([0x77, 0x88, 0x99], payload[29..32]);
    }

    [Fact]
    public async Task ApplyLighting_WritesWithoutAReadableProfile()
    {
        FakeMcuTransport mcu = new() { ReadFailure = new IOException("no answer") };
        ClawLightingCapability lighting = new(mcu, ReferenceLightingProfileAddress);
        Assert.Null(await lighting.ReadAsync(CancellationToken.None));

        var result = await lighting.ApplyAsync(
            Command(CapabilityIds.LightingBrightness, null, CapabilityValue.Integer(75)),
            current => current with { Brightness = 75 },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(75, Assert.Single(mcu.ProfileWrites)[4]);
        Assert.Equal(75, lighting.Current?.Brightness);
    }

    [Fact]
    public async Task ApplyLighting_PreservesUnknownProfileBytesAndVerifiesTheWholeReadback()
    {
        var original = ClawLightingCapability.Encode(
            new LightingState(50, 0x112233, 0x445566, 0x778899));
        original[0] = 0xA5;
        original[3] = 0x7E;
        FakeMcuTransport mcu = new() { Profile = original };
        ClawLightingCapability lighting = new(mcu, ReferenceLightingProfileAddress);
        _ = await lighting.ReadAsync(CancellationToken.None);
        var command = Command(
            CapabilityIds.LightingBrightness,
            null,
            CapabilityValue.Integer(75));

        var result = await lighting.ApplyAsync(
            command,
            current => current with { Brightness = 75 },
            CancellationToken.None);

        byte[] expected = [.. original];
        expected[4] = 75;
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(expected, Assert.Single(mcu.ProfileWrites));
        Assert.Equal(expected, mcu.Profile);
    }

    [Fact]
    public async Task ApplyLighting_ReadbackMismatchLeavesTheWriteUnverified()
    {
        var original = ClawLightingCapability.Encode(
            new LightingState(50, 0x112233, 0x445566, 0x778899));
        original[0] = 0xA5;
        original[3] = 0x7E;
        FakeMcuTransport mcu = new()
        {
            Profile = original,
            TransformNextWrite = payload =>
            {
                payload[0] ^= 0xFF;
                return payload;
            }
        };
        ClawLightingCapability lighting = new(mcu, ReferenceLightingProfileAddress);
        _ = await lighting.ReadAsync(CancellationToken.None);
        var command = Command(
            CapabilityIds.LightingBrightness,
            null,
            CapabilityValue.Integer(75));

        var result = await lighting.ApplyAsync(
            command,
            current => current with { Brightness = 75 },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        var write = Assert.Single(mcu.ProfileWrites);
        Assert.Equal(0xA5, write[0]);
        Assert.Equal(0x7E, write[3]);
        Assert.Equal(75, lighting.Current?.Brightness);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public async Task ApplyChargeLimit_WritesPercentageAndVerifiesReadback(int percent)
    {
        FakeWmiTransport wmi = new();
        ClawChargeLimitCapability chargeLimit = new(wmi);
        var command = Command(
            CapabilityIds.ChargeLimit,
            null,
            CapabilityValue.Integer(percent));

        var result = await chargeLimit.ApplyAsync(
            command,
            percent,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(0x80 | percent, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    // HC's SetBatteryMaster: the limit is only enforced with bit 7 set, so choosing one sets it.
    [Fact]
    public async Task ChargeLimit_SetsBatteryMasterWhenItWasClear()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ChargeLimitAddress, 100);
        ClawChargeLimitCapability chargeLimit = new(wmi);

        _ = await chargeLimit.ApplyAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), 60, CancellationToken.None);

        Assert.Equal(0x80 | 60, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    [Theory]
    [InlineData(70)]
    [InlineData(59)]
    public async Task ChargeLimit_OnlyTakesHcsTwentyPercentSteps(int percent)
    {
        FakeWmiTransport wmi = new();
        ClawChargeLimitCapability chargeLimit = new(wmi);

        var result = await chargeLimit.ApplyAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(percent)), percent,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    // Bit 7 of the register is a firmware flag, not part of the percentage. Handheld Companion
    // carries it through its writes, and after BIOS E1T52IMS.114 the plain range check on the
    // whole byte faulted the charge-limit service at every start.
    [Fact]
    public async Task ChargeLimit_MasksTheFlagBitOnReadAndCarriesItThroughWrites()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ChargeLimitAddress, 0x80 | 100);
        ClawChargeLimitCapability chargeLimit = new(wmi);

        var observed = await chargeLimit.ReadAsync(CancellationToken.None);
        var result = await chargeLimit.ApplyAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(80)),
            80,
            CancellationToken.None);

        Assert.Equal(100, observed.Percent);
        Assert.Equal(0x80 | 100, observed.RawValue);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(0x80 | 80, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    [Fact]
    public async Task ApplyCurveAsync_UsesMeasuredSixOffsetsAndPreservesUnknownBytes()
    {
        FakeWmiTransport wmi = new();
        ClawFanCapability fan = new(wmi);
        var command = Command(
            CapabilityIds.FanCurve,
            null,
            CapabilityValue.Curve(
            [
                new CurvePoint(0, 0),
                new CurvePoint(50, 40),
                new CurvePoint(60, 50),
                new CurvePoint(70, 60),
                new CurvePoint(80, 70),
                new CurvePoint(90, 80)
            ]));

        var result = await fan.ApplyCurveAsync(
            command,
            command.RequestedValue!.CurveValue,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        var dutyWrite = Assert.Single(
            wmi.Writes,
            write => write.Method == "Set_Fan" && write.Package[0] == 1).Package;
        var temperatureWrite = Assert.Single(
            wmi.Writes,
            write => write.Method == "Set_Temperature" && write.Package[0] == 1).Package;
        Assert.Equal([0, 40, 50, 60, 70, 80], dutyWrite[2..8]);
        Assert.Equal(0xA1, dutyWrite[1]);
        Assert.Equal(0xA8, dutyWrite[8]);
        Assert.Equal(0, temperatureWrite[1]);
        Assert.Equal([50, 60, 70, 80, 90], temperatureWrite[4..9]);
        Assert.Equal(0xB2, temperatureWrite[2]);
        Assert.Equal(0xB3, temperatureWrite[3]);
    }

    /// The two fans share a heatsink and the firmware ramps them together, so one authored curve
    /// has to reach both channels or the pair describes a machine that does not exist.
    [Fact]
    public async Task ApplyCurveAsync_WritesTheSameCurveToBothFanChannels()
    {
        FakeWmiTransport wmi = new();
        ClawFanCapability fan = new(wmi);
        var command = Command(
            CapabilityIds.FanCurve,
            null,
            CapabilityValue.Curve(
            [
                new CurvePoint(0, 0),
                new CurvePoint(50, 40),
                new CurvePoint(60, 50),
                new CurvePoint(70, 60),
                new CurvePoint(80, 70),
                new CurvePoint(90, 80)
            ]));

        var result = await fan.ApplyCurveAsync(
            command,
            command.RequestedValue!.CurveValue,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);

        // Only the six curve positions are compared. Every other byte in the package is firmware
        // data this write preserves per channel, and the two channels do not hold the same values
        // there. Copying one channel's spare bytes onto the other is exactly the bug the
        // preserve-unknown-bytes test above exists to prevent.
        var leftDuty = ChannelWrite(wmi, "Set_Fan", 1);
        var rightDuty = ChannelWrite(wmi, "Set_Fan", 2);
        Assert.Equal(leftDuty[2..8], rightDuty[2..8]);

        var leftTemperature = ChannelWrite(wmi, "Set_Temperature", 1);
        var rightTemperature = ChannelWrite(wmi, "Set_Temperature", 2);
        Assert.Equal(leftTemperature[1], rightTemperature[1]);
        Assert.Equal(leftTemperature[4..9], rightTemperature[4..9]);
    }

    [Fact]
    public async Task ScenarioReadbackMismatchLeavesTheWriteUnverifiedWithoutRetry()
    {
        FakeWmiTransport wmi = new();
        wmi.AfterSetter = (method, package) =>
        {
            if (method == "Set_Data" && package[0] == ClawHardwareFacts.ScenarioAddress)
            {
                wmi.SetData(ClawHardwareFacts.ScenarioAddress, 0xC2);
            }
        };
        ClawPowerCapability capability = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);

        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("sport")), "sport", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Single(wmi.Writes);
        Assert.Equal(0xC4, capability.Observe(await capability.ReadAsync(CancellationToken.None)).Scenario);
    }

    [Fact]
    public async Task UnsupportedScenarioIsRejectedWithoutWrites()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, 1);
        ClawPowerCapability capability = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("sport")), "sport", CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(0xC1, 0x81)]
    [InlineData(0xC4, 0x80)]
    [InlineData(0xC6, 0x82)]
    public async Task InactiveScenarioIsHcsDeactive(int initial, int expected)
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, initial);
        ClawPowerCapability capability = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("inactive")), "inactive", CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(expected, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
    }

    [Fact]
    public async Task UnknownScenarioOnSupportedFirmwareIsRejectedWithoutWrites()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability capability = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("turbo")), "turbo", CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData("Get_Data", ClawHardwareFacts.PowerSustainedAddress, 1)]
    [InlineData("Get_Data", ClawHardwareFacts.PowerBoostAddress, 1)]
    [InlineData("Get_AP", 0, 3)]
    public async Task PowerRejectsTruncatedResponses(string method, byte selector, int length)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse(method, selector, new byte[length]);
        ClawPowerCapability power = new(wmi, ClawModels.Claw8A2Vm, TestTiming.NoDelay);

        var failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() => power.ReadAsync(CancellationToken.None).AsTask());

        Assert.Contains("truncated", failure.Message);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData("Get_AP", 1)]
    [InlineData("Get_Data", ClawHardwareFacts.FanFullSpeedAddress)]
    public async Task FanSnapshotRejectsTruncatedFlags(string method, byte selector)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse(method, selector, new byte[1]);
        ClawFanCapability fan = new(wmi);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fan.ReadSnapshotAsync(CancellationToken.None).AsTask());
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData("Get_Fan", 4)]
    [InlineData("Get_Temperature", 1)]
    public async Task FanTelemetryRejectsTruncatedResponses(string method, int length)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse(method, 0, new byte[length]);
        ClawFanCapability fan = new(wmi);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fan.ReadTelemetryAsync(CancellationToken.None).AsTask());
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task UnknownFanModeIsRejectedBeforeAnyTransportAccess()
    {
        FakeWmiTransport wmi = new();
        ClawFanCapability fan = new(wmi);
        CapabilityCommand command = new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = CapabilityIds.FanMode,
            Deadline = Deadline.After(TimeSpan.FromSeconds(2))
        };

        var result = await fan.ApplyModeAsync(command, "unknown", CancellationToken.None);

        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason!.Code);
        Assert.Equal(0, wmi.Reads);
        Assert.Empty(wmi.Writes);
    }

    private static byte[] ChannelWrite(FakeWmiTransport wmi, string method, byte channel)
    {
        return Assert.Single(
            wmi.Writes,
            write => write.Method == method && write.Package[0] == channel).Package;
    }
}
