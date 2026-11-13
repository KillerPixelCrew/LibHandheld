using System.Buffers.Binary;
using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Acer;

public sealed class AcerPowerTests
{
    [Fact]
    public async Task UnknownInitialPowerModeIsSetToBalancedAndItsRealLimitsArePublished()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { Mode = 255, ModeReportsUnknown = true };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec, initializeUnknownValues: true);
        var cycle = Cycle(context);

        await service.AcquireAsync(cycle, CancellationToken.None);

        Assert.Equal(["mode:0"], ec.Writes);
        Assert.Equal("balanced", service.States.Single(state => state.CapabilityId == "power.scenario")
            .ObservedValue?.ChoiceValue);
        Assert.Equal(35, service.States.Single(state => state.CapabilityId == "power.primary-limit")
            .ObservedValue?.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Single(ec.Writes);
    }

    [Fact]
    public async Task AReadableInitialPowerModeIsKeptAndAnUncertainDefaultIsNeverRetried()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { Mode = 2 };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec, initializeUnknownValues: true);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Empty(ec.Writes);
        Assert.Equal("quiet", service.States.Single(state => state.CapabilityId == "power.scenario")
            .ObservedValue?.ChoiceValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);

        ec.Mode = 255;
        ec.FailModeWrite = true;
        service = new AcerPowerService(context, () => ec, initializeUnknownValues: true);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.ModeWriteAttempts);
        Assert.Null(service.States.Single(state => state.CapabilityId == "power.scenario").ObservedValue);
        await service.RefreshAsync(CancellationToken.None);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.ModeWriteAttempts);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Theory]
    [InlineData("balanced", 0, 35)]
    [InlineData("turbo", 1, 40)]
    [InlineData("quiet", 2, 30)]
    public async Task StockNamesSelectTheMeasuredFirmwarePowerOrder(string name, int mode, int watts)
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.scenario",
            RequestedValue = CapabilityValue.Choice(name), Deadline = Deadline.Never
        }, context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(mode, ec.Mode);
        Assert.Equal(name, service.States.Single(state => state.CapabilityId == "power.scenario").ObservedValue?.ChoiceValue);
        Assert.Equal(watts, service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue?.IntegerValue);
        Assert.Equal(65, service.States.Single(state => state.CapabilityId == "power.boost-limit").ObservedValue?.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task StockModeRefreshReadsOnlyTheActiveProfile()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { Mode = 1 };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);

        // Manual (profile 3) is read only when a PL1/PL2 edit needs the bytes it preserves.
        Assert.Equal([1, 1], ec.ProfileReads);
        Assert.Equal(40, service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue?.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task ModeSelectionImmediatelyPublishesItsLimitsAndKeepsSavedManualBytes()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc();
        var original = (byte[])ec.Profile.Clone();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        foreach (var (mode, watts) in new[] { ("quiet", 30), ("manual", 25) })
        {
            var result = await service.ExecuteAsync(new CapabilityCommand
            {
                CommandId = Guid.NewGuid(), CapabilityId = "power.scenario",
                RequestedValue = CapabilityValue.Choice(mode), Deadline = Deadline.Never
            }, context.Firmware, CancellationToken.None);
            Assert.Equal(CommandOutcome.Applied, result.Outcome);
            Assert.Equal(watts, service.States.Single(state => state.CapabilityId == "power.primary-limit")
                .ObservedValue?.IntegerValue);
            Assert.Equal(original, ec.Profile);
        }
        Assert.Equal(new[] { "mode:2", "mode:3" }, ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task MissingProfileGetterDoesNotPreventModeSelection()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { FailProfileRead = true };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.scenario",
            RequestedValue = CapabilityValue.Choice("quiet"), Deadline = Deadline.Never
        }, context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(new[] { "mode:2" }, ec.Writes);
        Assert.Null(service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 35)]
    [InlineData(1, 40)]
    [InlineData(2, 30)]
    public async Task StockStartupReadsActiveLimitsWithoutOverwritingRetainedManualProfile(int mode, int watts)
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { Mode = mode };
        var original = (byte[])ec.Profile.Clone();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(watts, service.States.Single(state => state.CapabilityId == "power.primary-limit")
            .ObservedValue?.IntegerValue);
        Assert.Equal(65, service.States.Single(state => state.CapabilityId == "power.boost-limit")
            .ObservedValue?.IntegerValue);
        Assert.Equal(original, ec.Profile);
        Assert.Empty(ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Empty(ec.Writes);
    }

    [Fact]
    public async Task ManualPowerPreservesOtherFieldsAndRestoresTheProfileBeforeTheMode()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc();
        var original = (byte[])ec.Profile.Clone();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        // Selector 1 advertises 10 W; the attended 8 W floor is lower.
        Assert.Equal(8, service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").Minimum);
        Assert.Equal(40, service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").Maximum);
        Assert.Equal(8, service.Descriptors.Single(item => item.CapabilityId == "power.boost-limit").Minimum);
        Assert.Equal(65, service.Descriptors.Single(item => item.CapabilityId == "power.boost-limit").Maximum);
        Assert.True(DevicePowerPreset.TryValidate(service.Descriptors, out _));
        var presets = service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").PowerPresets;
        Assert.Equal(new[] { "battery-saver", "efficiency", "light", "quiet", "balanced", "turbo", "full-power" },
            presets.Select(preset => preset.Id));
        Assert.Equal(new[] { (8, 8), (12, 15), (15, 20), (20, 20), (25, 37), (30, 37) },
            presets.SkipLast(1).Select(preset => (preset.SustainedWatts, preset.SlowWatts)));
        Assert.All(presets, preset => Assert.Equal("manual", preset.ScenarioOnAc));
        var full = presets.Last();
        Assert.Equal(40, full.SustainedWatts);
        Assert.Equal(65, full.SlowWatts);
        Assert.Equal("manual", full.ScenarioOnAc);
        Assert.Empty(ec.Writes);

        var result = await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(3, ec.Mode);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(ec.Profile.AsSpan(6)));
        Assert.Equal(40, BinaryPrimitives.ReadUInt16LittleEndian(ec.Profile.AsSpan(10)));
        Assert.Equal(original.AsSpan(12).ToArray(), ec.Profile.AsSpan(12).ToArray());
        Assert.Equal(original.AsSpan(8, 2).ToArray(), ec.Profile.AsSpan(8, 2).ToArray());
        Assert.True(context.Journal.HasUnrestoredMutation("acer.manual-profile"));

        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.Idle, release.State);
        Assert.Equal(original, ec.Profile);
        Assert.Equal(0, ec.Mode);
        Assert.Equal(new[] { "profile", "mode:3", "profile", "mode:0" }, ec.Writes);
        Assert.Empty(context.Journal.OutstandingEntries);
    }

    [Fact]
    public async Task UncertainProfileWriteIsNotRetriedAndDoesNotSelectManualMode()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { FailProfileReply = true };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(new[] { "profile" }, ec.Writes);
        Assert.Equal(0, ec.Mode);
        Assert.Null(service.States.Single(item => item.CapabilityId == "power.primary-limit").ObservedValue);
        Assert.True(context.Journal.HasUnrestoredMutation("acer.manual-profile"));
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Empty(context.Journal.OutstandingEntries);
    }

    [Fact]
    public async Task CancelledCommandDoesNotWrite()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(Command(), context.Firmware, new CancellationToken(canceled: true));
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task MissingModeReadbackDoesNotGateAnExplicitModeCommand()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { FailModeRead = true };
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var command = new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.scenario",
            RequestedValue = CapabilityValue.Choice("quiet"), Deadline = Deadline.Never
        };
        var result = await service.ExecuteAsync(command, context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(new[] { "mode:2" }, ec.Writes);
        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.ReleasedUnverified, release.State);
        Assert.Equal(new[] { "mode:2" }, ec.Writes);
    }

    [Fact]
    public void MeasuredHealthMaskSelectsTwoSystemFansAndTheirDistinctWriteFields()
    {
        var fans = AcerFanService.Discover(0x02CD);
        Assert.Equal(new[] { "sys", "sys2" }, fans.Select(item => item.Id));
        Assert.Equal(new[] { "Left", "Right" }, fans.Select(item => item.Name));
        Assert.Equal(0x000C0002UL, fans[0].PackBehavior(behavior: 3));
        Assert.Equal(0x00300004UL, fans[1].PackBehavior(behavior: 3));
        Assert.Equal(0x3202UL, fans[0].PackSpeed(speed: 50));
        Assert.Equal(0x3203UL, fans[1].PackSpeed(speed: 50));
        Assert.Empty(AcerFanService.Discover(healthMask: 0));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public void BothObservedVendorReportLengthsDecodeRearAndGuide(int length)
    {
        var report = new byte[length];
        report[0] = 0x9A;
        report[1] = 6;
        Assert.True(AcerInputProtocol.TryDecodeRear(report, out var buttons));
        Assert.Equal(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, buttons);
        // Rear-left (9A04) is the left paddle, rear-right (9A02) the right one.
        report[1] = 4;
        Assert.True(AcerInputProtocol.TryDecodeRear(report, out buttons));
        Assert.Equal(CanonicalButtons.RearPaddle1, buttons);
        report[1] = 2;
        Assert.True(AcerInputProtocol.TryDecodeRear(report, out buttons));
        Assert.Equal(CanonicalButtons.RearPaddle2, buttons);
        report[0] = 0x9E;
        report[1] = 0;
        report[3] = 5;
        Assert.True(AcerInputProtocol.IsGuidePress(report));
    }

    [Fact]
    public async Task ARestoreReadbackMismatchIsOnlyLoggedAndRetiresTheOriginalWithoutRepeatingTheWrite()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None)).Outcome);

        ec.IgnoreProfileWrite = true;
        var release = await service.ReleaseAsync(cycle, CancellationToken.None);

        Assert.Equal(DeviceServiceState.Idle, release.State);
        Assert.Empty(context.Journal.OutstandingEntries);
        Assert.Equal(new[] { "profile", "mode:3", "profile", "mode:0" }, ec.Writes);
    }

    [Fact]
    public async Task AStandbyKeepsTheIpfSessionAndAWakeWritesThroughItAgain()
    {
        var policy = IntelPowerPaths.PolicyControl;
        var present = IntelPowerPaths.IpfPresent;
        var open = IntelPowerPaths.OpenIpf;
        List<RecordingIpf> sessions = [];
        IntelPowerPaths.PolicyControl = new RunningPolicy();
        IntelPowerPaths.IpfPresent = () => true;
        IntelPowerPaths.OpenIpf = _ =>
        {
            RecordingIpf session = new();
            sessions.Add(session);
            return session;
        };
        try
        {
            using var directory = new TemporaryDirectory();
            var context = Context(directory.Root);
            var service = new AcerPowerService(context, () => new FixtureEc());
            var cycle = Cycle(context);
            await service.AcquireAsync(cycle, CancellationToken.None);
            Assert.Empty(sessions);
            Assert.Equal(CommandOutcome.Applied,
                (await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None)).Outcome);

            await service.SuspendAsync(cycle, CancellationToken.None);
            var kept = Assert.Single(sessions);
            Assert.False(kept.Disposed);

            await service.AcquireAsync(cycle, CancellationToken.None);
            Assert.Equal(CommandOutcome.Applied,
                (await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None)).Outcome);
            Assert.Same(kept, Assert.Single(sessions));
            Assert.Equal(new[] { (20, 40), (20, 40) }, kept.Writes);

            await service.ReleaseAsync(cycle, CancellationToken.None);
            Assert.True(kept.Disposed);
        }
        finally
        {
            IntelPowerPaths.PolicyControl = policy;
            IntelPowerPaths.IpfPresent = present;
            IntelPowerPaths.OpenIpf = open;
        }
    }

    private sealed class RunningPolicy : IIntelPolicyControl
    {
        public WindowsServiceState? Read(string name) =>
            name == "ipfsvc" ? new WindowsServiceState(2, true) : null;

        public bool IsRunning(string name) => name == "ipfsvc";

        public void DisableAndStop(string name)
        {
        }

        public void Stop(string name)
        {
        }

        public void Restore(string name, WindowsServiceState original)
        {
        }
    }

    private sealed class RecordingIpf : IIntelIpfSession
    {
        internal List<(int, int)> Writes { get; } = [];
        internal bool Disposed { get; private set; }
        internal bool FailNextWrite { get; set; }
        public bool Alive => !Disposed;

        public CpuPowerLimits ReadLimits() => new(20, 40, 40);

        public void WriteLimits(int sustainedWatts, int boostWatts)
        {
            Writes.Add((sustainedWatts, boostWatts));
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("IPF dropped the reply.");
            }
        }

        public void Dispose() => Disposed = true;
    }

    // Selectors 0..3 of the PA07-I51 V1.03 EC, read 2026-11-09.
    private static FixtureEc Atlas7Ec() => new() { SlotIds = [4, 0, 2, 5], SlotWatts = [35, 30, 25], SlotBoost = [65, 65, 45] };

    private static AcerPowerService Atlas7Service(ProfiledFeatureContext context, FixtureEc ec)
    {
        return new AcerPowerService(context, new AcerLease<IAcerEcTransport>(() => ec), table: AcerPowerTable.Atlas7);
    }

    [Theory]
    [InlineData("turbo", 0, 35)]
    [InlineData("balanced", 1, 30)]
    [InlineData("quiet", 2, 25)]
    public Task Atlas7StockNamesSelectItsOwnFirmwareOrder(string name, int mode, int watts) => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        // With no limit of WSGM's in force, the mode is selected as it stands, with its own limits.
        var result = await service.ExecuteAsync(FanMode(name), context.Firmware, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(mode, ec.Mode);
        Assert.Equal(name, ScenarioOf(service));
        Assert.Equal(watts, service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue?.IntegerValue);
        Assert.Empty(sessions);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7FanModeBeforeAnyLimitOfWsgmsWritesNoLimit() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        // A saved fan mode restored at start, before any limit is reasserted: the mode alone is written.
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(FanMode("quiet"), context.Firmware, CancellationToken.None)).Outcome);
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(FanMode("auto"), context.Firmware, CancellationToken.None)).Outcome);

        Assert.Equal(["mode:2"], ec.Writes);
        Assert.Equal("auto", ScenarioOf(service));
        Assert.Empty(sessions);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7FanModeWithMissingReadingsStillWritesOnlyWhatItReports() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = new FixtureEc { SlotIds = [4, 0, 2, 5], SlotWatts = [35, 30, 25], FailProfileRead = true };
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        var result = await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(["mode:0"], ec.Writes);
        Assert.Empty(sessions);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7FanModeAfterAnUncertainLimitSendsWsgmsLimitAgain() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        var uncertain = await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, uncertain.Outcome);

        var result = await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(0, ec.Mode);
        Assert.Equal((20, 40), sessions[^1].Writes[^1]);
        Assert.Equal(2, sessions.Count);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }, failFirstWrite: true);

    [Fact]
    public Task Atlas7IntegrationReEnableForgetsWsgmsLimit() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        await service.ReleaseAsync(cycle, CancellationToken.None);

        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);

        Assert.Equal(0, ec.Mode);
        Assert.Equal([(20, 40)], Assert.Single(sessions).Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7BoostAboveAModesOwnRunsOnAModeThatCarriesIt() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        // 20 W fits Quiet, but Quiet's own boost is 45 W, so 20/60 runs on Balanced as requested.
        var result = await service.ExecuteAsync(Limit(20, 60), context.Firmware, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(1, ec.Mode);
        Assert.Equal((20, 60), Assert.Single(sessions).Writes[^1]);
        // Preset matching compares the published pair, so a 20/60 preset still matches after the write.
        Assert.Equal(20, service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue?.IntegerValue);
        Assert.Equal(60, service.States.Single(state => state.CapabilityId == "power.boost-limit").ObservedValue?.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7FanModesAreRefusedOffIpfAndLimitsCarryAcrossPathChanges() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(0, ec.Mode);

        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(Path("firmware"), context.Firmware, CancellationToken.None)).Outcome);
        Assert.Equal(3, ec.Mode);
        var writes = ec.Writes.Count;
        var refused = await service.ExecuteAsync(FanMode("quiet"), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Equal(writes, ec.Writes.Count);

        // An AutoTDP step on the firmware path runs on Manual, as on the Atlas 8.
        await service.ExecuteAsync(Limit(15, 20), context.Firmware, CancellationToken.None);
        Assert.Equal(3, ec.Mode);

        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(Path("ipf"), context.Firmware, CancellationToken.None)).Outcome);
        Assert.Equal(0, ec.Mode);
        Assert.Equal("turbo", ScenarioOf(service));
        Assert.Equal((15, 20), sessions[^1].Writes[^1]);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.Mode);
    });

    [Fact]
    public Task Atlas7AFailedBalancedExitIsReleasedUnverified() => WithIpfAsync(async _ =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);

        ec.FailModeWrite = true;
        var release = await service.ReleaseAsync(cycle, CancellationToken.None);

        Assert.Equal(DeviceServiceState.ReleasedUnverified, release.State);
        Assert.Equal(2, ec.Mode);
        Assert.DoesNotContain("mode:1", ec.Writes);

        // Nothing retried it during that release; the next start recovers the pending target once.
        ec.FailModeWrite = false;
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.Mode);
        Assert.Single(ec.Writes, write => write == "mode:1");
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7ACrashBeforeExitIsRecoveredToBalancedAtTheNextStart() => WithIpfAsync(async _ =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var crashed = Atlas7Service(context, ec);
        await crashed.AcquireAsync(Cycle(context), CancellationToken.None);
        await crashed.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);
        await crashed.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(0, ec.Mode);

        // No release: a new process reads the journal the first one left.
        var restarted = Context(directory.Root);
        var service = Atlas7Service(restarted, ec);
        await service.AcquireAsync(Cycle(restarted), CancellationToken.None);

        Assert.Equal(1, ec.Mode);
        Assert.DoesNotContain(restarted.Journal.OutstandingEntries, entry => entry.ServiceId == "acer.operating-mode");
        await service.ReleaseAsync(Cycle(restarted), CancellationToken.None);
    });

    [Fact]
    public Task Atlas7ARefusedLimitIsNeverReplayedByAFanModePick() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);

        var refused = await service.ExecuteAsync(Limit(20, 60), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);

        Assert.Equal(0, ec.Mode);
        Assert.Empty(sessions);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }, failFirstOpen: true);

    [Fact]
    public Task Atlas7ACancelledLimitKeepsThePreviousPair() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await service.ExecuteAsync(Limit(25, 45), context.Firmware, cancelled.Token);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);

        Assert.Equal((20, 40), Assert.Single(sessions).Writes[^1]);
        Assert.DoesNotContain((25, 45), sessions[0].Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    private static CapabilityCommand Path(string path)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.backend",
            RequestedValue = CapabilityValue.Choice(path), Deadline = Deadline.Never
        };
    }

    [Fact]
    public async Task Atlas7ManualEditAddressesSlotThreeAndRestoresItsRetainedBytes()
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var original = (byte[])ec.Profile.Clone();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(["profile", "mode:3"], ec.Writes.Take(2));
        // The write carries the selector, not the identifier 5 that the slot reports on readback.
        Assert.Equal(3, ec.Profile[5]);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(ec.Profile.AsSpan(6)));
        Assert.Equal(40, BinaryPrimitives.ReadUInt16LittleEndian(ec.Profile.AsSpan(10)));
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(original, ec.Profile);
        // The Atlas 7 leaves Balanced, its journaled exit target, rather than the mode found at start.
        Assert.Equal(1, ec.Mode);
    }

    [Fact]
    public async Task Atlas7OffersFirmwareFanModesWithEachModesCeiling()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, Atlas7Ec());
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var mode = service.Descriptors.Single(item => item.CapabilityId == "power.scenario");
        Assert.Equal(DisplayKey.FirmwareFanMode, mode.Display.Key);
        Assert.Equal(new[] { "auto", "quiet", "balanced", "turbo", "manual" }, mode.Choices.Select(choice => choice.Value));
        Assert.Equal("Auto picks the quietest mode that carries the power limit WSGM sets, from its next one. "
                     + "Quiet up to 25/45 W, Balanced up to 30/65 W, Turbo up to 35/65 W. Above that the fans use Manual, the loudest.",
            mode.Display.Description);
        Assert.All(service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").PowerPresets,
            preset => Assert.Null(preset.ScenarioOnAc));
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Theory]
    [InlineData(20, 2)]
    [InlineData(25, 2)]
    [InlineData(30, 1)]
    [InlineData(35, 0)]
    [InlineData(40, 3)]
    public Task Atlas7AutoRunsTheQuietestModeThatCarriesTheLimit(int watts, int mode) => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal("auto", ScenarioOf(service));

        var result = await service.ExecuteAsync(Limit(watts, 45), context.Firmware, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(mode, ec.Mode);
        Assert.Equal("auto", ScenarioOf(service));
        Assert.Equal((watts, 45), Assert.Single(sessions).Writes[^1]);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7HandPickedFanModeLeavesTheLimitAndRisesOnlyWhileTheLimitNeedsIt() => WithIpfAsync(async sessions =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(2, ec.Mode);

        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);
        Assert.Equal(0, ec.Mode);
        Assert.Equal("turbo", ScenarioOf(service));
        Assert.Equal(20, service.States.Single(state => state.CapabilityId == "power.primary-limit").ObservedValue?.IntegerValue);
        Assert.Equal((20, 40), Assert.Single(sessions).Writes[^1]);

        await service.ExecuteAsync(FanMode("quiet"), context.Firmware, CancellationToken.None);
        await service.ExecuteAsync(Limit(30, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(1, ec.Mode);
        Assert.Equal("quiet", ScenarioOf(service));
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(2, ec.Mode);

        await service.ExecuteAsync(FanMode("auto"), context.Firmware, CancellationToken.None);
        Assert.Equal("auto", ScenarioOf(service));
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7ModeTheFirmwareMovedItselfIsCorrectedByTheNextLimit() => WithIpfAsync(async _ =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);

        ec.Mode = 0;
        await service.ExecuteAsync(Limit(21, 40), context.Firmware, CancellationToken.None);

        Assert.Equal(2, ec.Mode);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    });

    [Fact]
    public Task Atlas7StandbyKeepsTheModeAndEveryExitLeavesBalanced() => WithIpfAsync(async _ =>
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = Atlas7Service(context, ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(FanMode("turbo"), context.Firmware, CancellationToken.None);
        await service.ExecuteAsync(Limit(20, 40), context.Firmware, CancellationToken.None);
        Assert.Equal(0, ec.Mode);

        await service.SuspendAsync(cycle, CancellationToken.None);
        Assert.Equal(0, ec.Mode);

        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.Mode);
        Assert.Empty(context.Journal.OutstandingEntries);
    });

    private static async Task WithIpfAsync(Func<List<RecordingIpf>, Task> body, bool failFirstWrite = false,
        bool failFirstOpen = false)
    {
        var policy = IntelPowerPaths.PolicyControl;
        var present = IntelPowerPaths.IpfPresent;
        var open = IntelPowerPaths.OpenIpf;
        var sessions = new List<RecordingIpf>();
        var opens = 0;
        IntelPowerPaths.PolicyControl = new RunningPolicy();
        IntelPowerPaths.IpfPresent = () => true;
        IntelPowerPaths.OpenIpf = _ =>
        {
            if (failFirstOpen && opens++ == 0)
            {
                throw new IOException("The IPF server refused the session.");
            }

            var session = new RecordingIpf { FailNextWrite = failFirstWrite && sessions.Count == 0 };
            sessions.Add(session);
            return session;
        };
        try
        {
            await body(sessions);
        }
        finally
        {
            IntelPowerPaths.PolicyControl = policy;
            IntelPowerPaths.IpfPresent = present;
            IntelPowerPaths.OpenIpf = open;
        }
    }

    private static string? ScenarioOf(AcerPowerService service)
    {
        return service.States.Single(state => state.CapabilityId == "power.scenario").ObservedValue?.ChoiceValue;
    }

    private static CapabilityCommand Limit(int sustained, int boost)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.primary-limit",
            RequestedValue = CapabilityValue.Integer(sustained), PairedPowerLimitWatts = boost, Deadline = Deadline.Never
        };
    }

    private static CapabilityCommand FanMode(string name)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.scenario",
            RequestedValue = CapabilityValue.Choice(name), Deadline = Deadline.Never
        };
    }

    [Fact]
    public async Task TheAtlas8TableStillRefusesAnUnexpectedSlotIdentifier()
    {
        using var directory = new TemporaryDirectory();
        var ec = Atlas7Ec();
        var context = Context(directory.Root);
        var service = new AcerPowerService(context, () => ec);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(Command(), context.Firmware, CancellationToken.None);
        Assert.NotEqual(CommandOutcome.Applied, result.Outcome);
        Assert.Empty(ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    private static ProfiledFeatureContext Context(string directory)
    {
        return new ProfiledFeatureContext(directory, "atlas|V1.03", new TestPluginHostAdapter(1))
        {
            Journal = TestRecovery.Open(directory)
        };
    }

    private static DeviceCycleContext<ProfiledCycleIdentity> Cycle(ProfiledFeatureContext context)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity(context.Firmware));
    }

    private static CapabilityCommand Command()
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.primary-limit",
            RequestedValue = CapabilityValue.Integer(20), PairedPowerLimitWatts = 40, Deadline = Deadline.Never
        };
    }

    private sealed class FixtureEc : IAcerEcTransport
    {
        // The live PA08-I51 profile, including fields not interpreted by LibHandheld.
        public byte[] Profile { get; private set; } = Convert.FromHexString("00E00200FF0319001E002D00FFFFFFFFFFFFFFFFFFFFFFFFFF");
        public int Mode { get; set; }
        public bool FailProfileReply { get; set; }
        public bool IgnoreProfileWrite { get; set; }
        public bool FailModeRead { get; init; }
        public bool ModeReportsUnknown { get; init; }
        public bool FailProfileRead { get; init; }
        public byte[]? SlotIds { get; init; }
        public int[]? SlotWatts { get; init; }
        public int[]? SlotBoost { get; init; }
        public bool FailModeWrite { get; set; }
        public int ModeWriteAttempts { get; private set; }
        public List<string> Writes { get; } = [];
        public List<int> ProfileReads { get; } = [];

        public byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null)
        {
            token.ThrowIfCancellationRequested();
            if (payload.Length > 5 && payload[2] == 2 && payload[4] == 2)
            {
                ProfileReads.Add(payload[5]);
            }
            var reply = new byte[64];
            reply[1] = 0xE0;
            reply[2] = payload[2];
            if (payload[2] == 0)
            {
                reply[7] = 8;
            }
            else if (payload[2] == 2 && payload[4] == 3)
            {
                reply[25] = payload[25];
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(26), payload[25] switch
                {
                    1 => 10, 2 => 40, 4 => 65, _ => throw new IOException("Unexpected limit selector.")
                });
            }
            else if (payload[2] == 2 && payload[4] == 2)
            {
                if (FailProfileRead)
                {
                    throw new IOException("Profile getter unavailable.");
                }
                Profile.CopyTo(reply, index: 0);
                reply[5] = SlotIds?[payload[5]] ?? payload[5];
                if (payload[5] < 3)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6), checked((ushort)(SlotWatts?[payload[5]] ?? payload[5] switch
                    {
                        0 => 35, 1 => 40, _ => 30
                    })));
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(10),
                        checked((ushort)(SlotBoost?[payload[5]] ?? 65)));
                }
            }
            else if (payload[2] == 1 && payload[4] == 2)
            {
                if (FailModeRead)
                {
                    throw new IOException("Mode getter unavailable.");
                }

                reply[4] = ModeReportsUnknown ? (byte)255 : checked((byte)Mode);
            }
            else if (payload[2] == 2 && payload[4] == 1)
            {
                dispatched?.Invoke();
                Writes.Add("profile");
                if (!IgnoreProfileWrite)
                {
                    Profile = payload.ToArray();
                    Profile[0] = 0;
                    Profile[1] = 0xE0;
                    Profile[4] = 0xFF;
                }
                if (FailProfileReply)
                {
                    FailProfileReply = false;
                    throw new IOException("Lost reply after dispatch.");
                }
            }
            else if (payload[2] == 1 && payload[4] == 1)
            {
                dispatched?.Invoke();
                ModeWriteAttempts++;
                if (FailModeWrite)
                {
                    throw new IOException("Lost reply after the mode was sent.");
                }

                Mode = payload[5];
                Writes.Add($"mode:{Mode}");
            }
            else
            {
                throw new IOException("Unexpected fixture request.");
            }

            return reply;
        }

        public void Dispose()
        {
        }
    }
}
