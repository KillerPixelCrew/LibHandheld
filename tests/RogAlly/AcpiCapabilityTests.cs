// SPDX-License-Identifier: MIT

using LibHandheld.Families.RogAlly.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;

namespace LibHandheld.Families.RogAlly.Tests;

public sealed class AcpiCapabilityTests
{
    [Theory]
    [InlineData(10, 12, 15, 25)]
    [InlineData(20, 25, 30, 10)]
    [InlineData(20, 25, 30, 22)]
    [InlineData(25, 25, 25, 25)]
    public async Task EveryLimitCommandWritesSplSpptFpptEvenWhenUnchanged(int sustained, int slow, int fast, int target)
    {
        var acpi = new FakeAsusAcpi();
        acpi.SetScalar(AsusAcpiId.SustainedPower, sustained);
        acpi.SetScalar(AsusAcpiId.SlowPower, slow);
        acpi.SetScalar(AsusAcpiId.FastPower, fast);
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);
        var command = Command(CapabilityValue.Integer(target));
        _ = await power.ApplyLimitsAsync(command, target, target, CancellationToken.None);
        _ = await power.ApplyLimitsAsync(command, target, target, CancellationToken.None);
        Assert.Equal(new[]
        {
            AsusAcpiId.SustainedPower, AsusAcpiId.SlowPower, AsusAcpiId.FastPower,
            AsusAcpiId.SustainedPower, AsusAcpiId.SlowPower, AsusAcpiId.FastPower
        }, acpi.Writes.Select(write => write.Id));
        Assert.Equal(0, acpi.StatusReads);
    }

    [Fact]
    public async Task UnifiedPairMovesAllThreeWithoutReadback()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(22)), 22, 22,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Null(result.ReadbackValue);
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.SustainedPower));
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.SlowPower));
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.FastPower));
    }

    [Fact]
    public async Task TheBoostValueGoesToSpptAndFpptTogether()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        _ = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(18)), 18, 20, CancellationToken.None);
        Assert.Equal((18, 20, 20), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));

        _ = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(28)), 28, 28, CancellationToken.None);
        Assert.Equal((28, 28, 28), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));
    }

    [Fact]
    public async Task ABoostCommandWritesThePairItCarriesAndReportsTheBoost()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(10), CapabilityIds.PowerBoost),
            10, 10, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.ReadbackValue);
        Assert.Equal((10, 10, 10), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));
    }

    [Fact]
    public async Task OutOfModelRangeIsRejectedBeforeAnyWrite()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc71l")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(35)), 35, 35, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(acpi.Writes);
    }

    [Fact]
    public async Task UnreadableLimitsAreReportedUnverified()
    {
        var acpi = new FakeAsusAcpi { ScalarsReadable = false };
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(20)), 20, 20,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.ReadbackValue);
    }

    [Fact]
    public async Task AReadbackMismatchLeavesTheWriteInPlace()
    {
        // As HC: the write is trusted. A readback that disagrees only means it cannot be verified; it is
        // never a reason to roll back or write again.
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.SlowPower };
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(10)), 10, 10,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(10, acpi.Scalar(AsusAcpiId.SustainedPower));
    }

    [Fact]
    public async Task ScenarioWritesTheAsusModeValue()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyScenarioAsync(
            Command(CapabilityValue.Choice(Scenarios.Silent), CapabilityIds.Scenario),
            Scenarios.Silent, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(2, acpi.Scalar(AsusAcpiId.PerformanceMode));
        Assert.Equal((AsusAcpiId.PerformanceMode, 2u), acpi.Writes.Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RestorePutsTheModeBackBeforeTheLimits(int currentMode)
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);
        var original = power.Read();
        acpi.SetScalar(AsusAcpiId.PerformanceMode, currentMode);
        acpi.SetScalar(AsusAcpiId.SustainedPower, 25);
        acpi.SetScalar(AsusAcpiId.SlowPower, 25);
        acpi.SetScalar(AsusAcpiId.FastPower, 25);

        var reads = acpi.StatusReads;
        Assert.True(await power.RestoreAsync(original, CancellationToken.None));
        Assert.Equal(reads, acpi.StatusReads);
        Assert.Equal(new[]
        {
            AsusAcpiId.PerformanceMode, AsusAcpiId.SustainedPower,
            AsusAcpiId.SlowPower, AsusAcpiId.FastPower
        }, acpi.Writes.Select(write => write.Id));
        Assert.Equal(AsusAcpiId.PerformanceMode, acpi.Writes[0].Id);
        Assert.Equal(original, power.Read());
    }

    [Fact]
    public void ChargeLimitWritesAndRefusesTooLow()
    {
        var acpi = new FakeAsusAcpi();
        var charge = new AllyChargeLimitCapability(acpi);

        Assert.Equal(CommandOutcome.AppliedUnverified,
            charge.Apply(Command(CapabilityValue.Integer(80), CapabilityIds.ChargeLimit), 80).Outcome);
        Assert.Equal(80, charge.Read());
        Assert.Equal(CommandOutcome.Rejected,
            charge.Apply(Command(CapabilityValue.Integer(20), CapabilityIds.ChargeLimit), 20).Outcome);
    }

    [Fact]
    public void AChargeLimitTheFirmwareDoesNotReportBackStaysWrittenAndIsNotRolledBack()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.ChargeLimit };
        acpi.SetScalar(AsusAcpiId.ChargeLimit, 100);
        var charge = new AllyChargeLimitCapability(acpi);

        var result = charge.Apply(Command(CapabilityValue.Integer(80), CapabilityIds.ChargeLimit), 80);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal([(AsusAcpiId.ChargeLimit, 80u)], acpi.Writes);
    }

    [Fact]
    public void FanCurvesEncodeLikeHc()
    {
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 0), new(40, 10), new(50, 20), new(60, 35), new(70, 50), new(80, 70), new(90, 90), new(100, 100)
        ];

        Assert.True(AllyFanCapability.TryEncode(points, out var curve, out _));
        Assert.Equal([30, 40, 50, 60, 70, 80, 90, 100, 0, 10, 20, 35, 50, 70, 90, 99], curve);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void FanCurvesNeedEightPoints(int count)
    {
        var points = Enumerable.Range(0, count).Select(index => new CurvePoint(30 + index * 5, 20)).ToArray();

        Assert.False(AllyFanCapability.TryEncode(points, out _, out _));
    }

    [Fact]
    public void FanCurvesRejectFallingDuties()
    {
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 50), new(40, 40), new(50, 50), new(60, 50), new(70, 50), new(80, 50), new(90, 50), new(100, 50)
        ];

        Assert.False(AllyFanCapability.TryEncode(points, out _, out _));
    }

    [Fact]
    public async Task FanCurveIsWrittenToEachFanWithoutReadback()
    {
        var acpi = new FakeAsusAcpi();
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 5), new(40, 10), new(50, 20), new(60, 35), new(70, 55), new(80, 75), new(90, 75), new(100, 75)
        ];

        var result = await fans.ApplyCurveAsync(Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.False(fans.HasMidFan);
        Assert.Equal([AsusAcpiId.CpuFanCurve, AsusAcpiId.GpuFanCurve], acpi.BufferWrites.Select(write => write.Id));
    }

    [Fact]
    public async Task FanCurveThatDoesNotEchoIsUnverifiedNotRolledBack()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.GpuFanCurve };
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 5), new(40, 10), new(50, 20), new(60, 35), new(70, 55), new(80, 75), new(90, 75), new(100, 75)
        ];

        var result = await fans.ApplyCurveAsync(Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(2, acpi.BufferWrites.Count);
    }

    [Fact]
    public async Task MidFanWriteDoesNotReadBackAnyChannel()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.MidFanCurve };
        acpi.SetCurve(AsusAcpiId.MidFanCurve, [.. AllyFanCapability.DefaultCpuCurve]);
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        var points = AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve);

        var result = await fans.ApplyCurveAsync(
            Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points, CancellationToken.None);

        Assert.True(fans.HasMidFan);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(3, acpi.BufferWrites.Count);
    }

    [Fact]
    public async Task AutomaticRestoresTheCapturedCurves()
    {
        var acpi = new FakeAsusAcpi();
        byte[] captured = [40, 45, 55, 63, 68, 74, 74, 74, 4, 8, 26, 34, 52, 74, 74, 74];
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();

        var result = await fans.ApplyAutomaticAsync(
            Command(CapabilityValue.Choice(FanModes.Automatic), CapabilityIds.FanMode),
            new AllyFanSnapshot(captured, captured, null, 0), CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(captured, acpi.Curve(AsusAcpiId.CpuFanCurve));
    }

    [Fact]
    public async Task FanRestoreDispatchesCapturedChannelsWithoutAnyStatusOrCurveReads()
    {
        var acpi = new FakeAsusAcpi();
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        var snapshot = new AllyFanSnapshot(AllyFanCapability.DefaultCpuCurve, AllyFanCapability.DefaultGpuCurve,
            AllyFanCapability.DefaultCpuCurve, 0);
        Assert.True(await fans.RestoreAsync(snapshot, CancellationToken.None));
        Assert.Equal(0, acpi.StatusReads);
        Assert.Equal(0, acpi.BufferReads);
        Assert.Equal(new[] { AsusAcpiId.CpuFanCurve, AsusAcpiId.GpuFanCurve, AsusAcpiId.MidFanCurve },
            acpi.BufferWrites.Select(write => write.Id));
    }

    [Fact]
    public async Task ResendingTheSameScenarioStillWritesTheModeAndWaitsForIt()
    {
        var acpi = new FakeAsusAcpi();
        List<TimeSpan> delays = [];
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var command = Command(CapabilityValue.Choice(Scenarios.Performance), CapabilityIds.Scenario);
        _ = await power.ApplyScenarioAsync(command, Scenarios.Performance, CancellationToken.None);
        _ = await power.ApplyScenarioAsync(command, Scenarios.Performance, CancellationToken.None);
        Assert.Equal(2, acpi.Writes.Count);
        Assert.All(acpi.Writes, write => Assert.Equal(AsusAcpiId.PerformanceMode, write.Id));
        Assert.Equal(2, delays.Count);
        Assert.Equal(0, acpi.StatusReads);
    }

    internal static CapabilityCommand Command(CapabilityValue value, string capabilityId = CapabilityIds.PowerSustained)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            RequestedValue = value,
            // WSGM carries the other limit on every power-limit write; these commands move both to one target.
            PairedPowerLimitWatts = capabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                ? value.IntegerValue
                : null,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = 1,
            Deadline = Deadline.After(TimeSpan.FromSeconds(10))
        };
    }
}
