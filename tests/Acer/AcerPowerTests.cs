using System.Buffers.Binary;
using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Acer;

public sealed class AcerPowerTests
{
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
        Assert.Equal(10, service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").Minimum);
        Assert.Equal(40, service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").Maximum);
        Assert.Equal(65, service.Descriptors.Single(item => item.CapabilityId == "power.boost-limit").Maximum);
        var presets = service.Descriptors.Single(item => item.CapabilityId == "power.primary-limit").PowerPresets;
        Assert.Equal(new[] { "quiet", "balanced", "turbo", "full-power" }, presets.Select(preset => preset.Id));
        Assert.Equal(new[] { (20, 20), (25, 37), (30, 37) },
            presets.Take(3).Select(preset => (preset.SustainedWatts, preset.SlowWatts)));
        Assert.All(presets, preset =>
        {
            Assert.False(preset.FirmwareManaged);
            Assert.Equal("manual", preset.ScenarioOnAc);
        });
        var full = presets.Last();
        Assert.False(full.FirmwareManaged);
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
        report[0] = 0x9E;
        report[1] = 0;
        report[3] = 5;
        Assert.True(AcerInputProtocol.IsGuidePress(report));
    }

    [Fact]
    public async Task FailedRestoreReadbackRetainsTheOriginalWithoutRepeatingTheWrite()
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

        Assert.Equal(DeviceServiceState.ReleasedUnverified, release.State);
        Assert.True(context.Journal.HasUnrestoredMutation("acer.manual-profile"));
        Assert.Equal(new[] { "profile", "mode:3", "profile", "mode:0" }, ec.Writes);
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
        public bool FailProfileRead { get; init; }
        public List<string> Writes { get; } = [];

        public byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null)
        {
            token.ThrowIfCancellationRequested();
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
                reply[5] = payload[5];
                if (payload[5] < 3)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6), payload[5] switch
                    {
                        0 => 35, 1 => 40, _ => 30
                    });
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(10), 65);
                }
            }
            else if (payload[2] == 1 && payload[4] == 2)
            {
                if (FailModeRead)
                {
                    throw new IOException("Mode getter unavailable.");
                }

                reply[4] = checked((byte)Mode);
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
