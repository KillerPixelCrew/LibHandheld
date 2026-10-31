using LibHandheld.Families.MsiClaw.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Testing;
using static LibHandheld.Families.MsiClaw.Tests.Builders.ClawCommands;

namespace LibHandheld.Families.MsiClaw.Tests;

/// <summary>Per-model behaviour through the shared engine and injected family services.</summary>
[Collection("plugin-trace")]
public sealed class ClawModelLifecycleTests
{
    [Fact]
    public async Task StartAsync_PublishesTheModelsPowerRangesAndPresets()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.Claw8ExCg3Em };
        await using var plugin = Engine(Services(identity));
        TestPluginHostAdapter host = new(FixtureId);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8ExCg3Em), CancellationToken.None);

        var descriptors = host.DescriptorSets.Last().Descriptors;
        var sustained = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerSustained);
        var boost = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerBoost);
        Assert.Equal((20, 37), (sustained.Minimum, sustained.Maximum));
        Assert.Equal((20, 37), (boost.Minimum, boost.Maximum));
        Assert.Equal(ClawModels.Claw8ExCg3Em.PowerPresets, sustained.PowerPresets);
    }

    [Fact]
    public async Task StartAsync_UsesTheSuppliedModelWithoutCollectingAnotherIdentity()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.A1M };
        await using var plugin = Engine(Services(identity));

        var host = new TestPluginHostAdapter(FixtureId);
        var result = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);
        Assert.Equal(EngineOperationalState.Active, result.State);
        Assert.Equal(0, identity.ReadCount);
        Assert.Equal(ClawModels.Claw8A2Vm.MinimumWatts,
            host.DescriptorSets.Last().Descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerSustained)
                .Minimum);
    }

    [Theory]
    [InlineData("0211", 0x01FA)]
    [InlineData("0230", 0x024A)]
    public async Task FirstLightingWriteCapturesEnvelopeAtTheAddressForTheMcuRevision(string revision, int address)
    {
        using TemporaryDirectory state = new();
        FakeMcuTransport mcu = new();
        await using var plugin = Engine(Services(new FakeIdentityReader { McuRevision = revision }, mcu: mcu));

        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(FixtureId), state.Root, ClawModels.Claw8A2Vm) with
            {
                Identity = FakeIdentityReader.CreateState().Snapshot with { McuFirmwareVersion = revision }
            },
            CancellationToken.None);

        Assert.Empty(mcu.ReadAddresses);
        _ = await plugin.ExecuteCommandAsync(Command(CapabilityIds.LightingBrightness, null,
            CapabilityValue.Integer(80)), CancellationToken.None);
        Assert.NotEmpty(mcu.ReadAddresses);
        Assert.All(mcu.ReadAddresses, read => Assert.Equal(address, read));
    }

    [Fact]
    public async Task ExecuteCommand_ReadsThePowerSourceButNotTheCycleIdentity()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new();
        await using var plugin = Engine(Services(identity));
        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(FixtureId), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);

        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), CancellationToken.None);

        Assert.Equal(0, identity.ReadCount);
        Assert.Equal(2, identity.AcPowerReads);
    }

    [Fact]
    public async Task A1MRumble_IsOnOffAt193AndDeclaresItsPaceToTheHost()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new();
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.A1M);

        Assert.Equal(10, host.PublishedOutput?.MaxFramesPerSecond);
        Assert.Equal(ClawModels.BinaryRumbleInterval, host.PublishedOutput?.MinimumPulse);

        // The host paces frames to the declared ten a second; the plugin writes each state it gets once.
        await controller.ApplyHapticsAsync(Frame(0.5f, 0.25f), CancellationToken.None);
        await controller.ApplyHapticsAsync(Frame(0.9f, 0.1f), CancellationToken.None);
        Assert.Equal([(193, 193)], source.RumbleWrites.Select(write => ((int)write.Weak, (int)write.Strong)));

        await controller.ApplyHapticsAsync(Frame(0, 0), CancellationToken.None);
        await controller.ApplyHapticsAsync(Frame(0.3f, 0), CancellationToken.None);
        Assert.Equal([(193, 193), (0, 0), (0, 193)],
            source.RumbleWrites.Select(write => ((int)write.Weak, (int)write.Strong)));
    }

    [Fact]
    public async Task A1MRumble_ReleaseStopsTheMotors()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.A1M);
        await controller.ApplyHapticsAsync(Frame(1, 1), CancellationToken.None);

        await controller.ReleaseControllerAsync(Deadline.After(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.Equal((0, 0), (source.RumbleWrites[^1].Weak, source.RumbleWrites[^1].Strong));
    }

    [Fact]
    public async Task ControllerAcquire_WritesHcsPaddleMappingAndSyncsItToRom()
    {
        using TemporaryDirectory state = new();
        FakeMcuTransport mcu = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);

        _ = await AcquireControllerAsync(new FakeControllerSource(), new TestPluginHostAdapter(FixtureId),
            journal, ClawModels.Claw8A2Vm, mcu);

        // FakeIdentityReader reports MCU 0230: HC's nearest row is 0x0219, M1 0x00BA and M2 0x0163.
        Assert.Equal([(0x00BA, 1, 0), (0x0163, 1, 0)],
            mcu.Writes.Select(write => ((int)write.Address, (int)write.Payload[0], (int)write.Payload[1])));
        Assert.Equal(1, mcu.RomSyncs);
    }

    [Fact]
    public async Task ControllerReacquire_LeavesThePaddleMappingInRom()
    {
        using TemporaryDirectory state = new();
        FakeMcuTransport mcu = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(new FakeControllerSource(),
            new TestPluginHostAdapter(FixtureId), journal, ClawModels.Claw8A2Vm, mcu);
        await controller.ReleaseControllerAsync(Deadline.After(TimeSpan.FromSeconds(10)), CancellationToken.None);

        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);

        Assert.Equal(2, mcu.Writes.Count);
        Assert.Equal(1, mcu.RomSyncs);
        Assert.Equal([ClawControllerMode.DirectInput, ClawControllerMode.XInput, ClawControllerMode.DirectInput],
            mcu.ModeSwitches);
    }

    [Fact]
    public async Task ControllerAcquire_HidesTheXInputPadTheSwitchReplaced()
    {
        using TemporaryDirectory state = new();
        PhysicalDeviceIdentity directInput = new()
        {
            InstancePath = @"HID\VID_0DB0&PID_1902&MI_00\8&FAKE&0&0000",
            LocationPath = "PCIROOT(0)#USBROOT(0)#USB(2)",
            RequiresHiding = true
        };
        FakeMcuTransport mcu = new() { SwitchedNodes = [directInput] };
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);

        _ = await AcquireControllerAsync(new FakeControllerSource(), host, journal, ClawModels.Claw8A2Vm, mcu);

        // The firmware falls back to XInput in every sleep; its pad must already be hidden when it returns.
        Assert.Equal(
            [FakeControllerSource.Nodes[0].InstancePath, directInput.InstancePath],
            Assert.Single(host.PhysicalDeviceSets).Select(device => device.InstancePath));
    }

    [Fact]
    public async Task ControllerRelease_ReturnsToXInputAsHcDoes()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        FakeMcuTransport mcu = new();
        var controller = await AcquireControllerAsync(source, new TestPluginHostAdapter(FixtureId), journal,
            ClawModels.Claw8A2Vm, mcu);

        await controller.ReleaseControllerAsync(Deadline.After(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.Equal(ClawControllerMode.XInput, Assert.Single(mcu.ModeSwitches));
    }

    [Theory]
    [InlineData("QuickSettings", "Short")]
    [InlineData("QuickSettingsLong", "Long")]
    public async Task FirmwareChord_RaisesQuickSettingsWithoutMsiEvent(string chord, string press)
    {
        TestPluginHostAdapter host = new(FixtureId);
        DeviceTrace.Install(entry =>
        {
            if (entry.ChangeKey is { } key)
            {
                host.TraceChange((DeviceTraceLevel)entry.Level, entry.Scope, key, entry.Message);
            }
            else
            {
                host.Trace((DeviceTraceLevel)entry.Level, entry.Scope, entry.Message);
            }
        });
        OemButtonLatch latch = new();
        OemEventService oem = new(new FakeOemEventSource(), host, latch);
        FakeChordSuppressor suppressor = new();
        ChordSuppressorService service = new(suppressor, oem);
        _ = await service.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);

        suppressor.TriggerChord(Enum.Parse<FirmwareChord>(chord));
        await AsyncConditions.WaitForAsync(() => host.OemEvents.Count == 1);

        var raised = host.OemEvents.Single();
        Assert.Equal("oem2", raised.ControlId);
        Assert.Equal(Enum.Parse<OemPressKind>(press), raised.Press);
        Assert.Equal(CanonicalButtons.QuickAccess, latch.Current(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A2VmRumble_StaysProportional()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new();
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.Claw8A2Vm);

        await controller.ApplyHapticsAsync(Frame(0.5f, 0.25f), CancellationToken.None);

        Assert.Equal(250, host.PublishedOutput?.MaxFramesPerSecond);
        Assert.NotEqual(193, source.RumbleWrites.Single().Strong);
    }

    // HC clamps every value to cTDP before writing, so the CG3EM's 15 W override reaches the EC as 20.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cg3Em_NeverWritesBelowHcsTwentyWattFloor(bool boost)
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8ExCg3Em, TestTiming.NoDelay);

        var result = boost
            ? await power.ApplyLimitsAsync(
                Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(19)), 19, 19, CancellationToken.None)
            : await power.ApplyLimitsAsync(
                Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(15)), 15, 20,
                CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task Bz2EmSustainedChange_RewritesTheFastRegisterWithAnUnchangedBoost()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 20);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 28);
        wmi.SetData(ClawHardwareFacts.PowerFastAddress, 35);
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em, TestTiming.NoDelay);

        _ = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(18)), 18, 28, CancellationToken.None);

        Assert.Equal(28, wmi.ReadData(ClawHardwareFacts.PowerFastAddress));
    }

    [Fact]
    public async Task Bz2EmRestore_PutsTheCapturedFastValueBack()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerFastAddress, 35);
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em, TestTiming.NoDelay);
        var original = await power.ReadAsync(CancellationToken.None);
        _ = await power.ApplyLimitsAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(20)), 20, 20, CancellationToken.None);

        await power.RestoreAsync(original, CancellationToken.None);

        Assert.Equal(35, original.FastWatts);
        Assert.Equal(35, wmi.ReadData(ClawHardwareFacts.PowerFastAddress));
    }

    [Fact]
    public async Task Bz2EmFastRead_FailureLeavesTheValueUnknown()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em, TestTiming.NoDelay);

        var pair = await power.ReadAsync(CancellationToken.None);

        Assert.Null(pair.FastWatts);
    }

    [Fact]
    public async Task StartAsync_EntryFromAnotherEcIsDroppedAndPowerStaysAvailable()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(ClawServiceIds.Power, "ec:1T52EMS1.108;msi-acpi:8.0",
                ClawRecoveryState.Power(new PowerPair(20, 30, 0xC1)), CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        await using var plugin = Engine(Services(new FakeIdentityReader(), wmi));
        TestPluginHostAdapter host = new(FixtureId);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8A2Vm), CancellationToken.None);

        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Contains(host.CapabilityStates,
            capability => capability is { CapabilityId: CapabilityIds.PowerSustained, Available: true });
        var reopened = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(reopened.Entries<ClawRecoveryState>());
    }

    [Fact]
    public async Task LegacyRecoveryFilesAreIgnored()
    {
        using TemporaryDirectory state = new();
        await File.WriteAllTextAsync(Path.Combine(state.Root, "temporary-state.v1.json"), "{invalid legacy data}");
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(journal.OutstandingEntries);
    }

    [Theory]
    [InlineData(0, (uint)CanonicalButtons.X)]
    [InlineData(1, (uint)CanonicalButtons.A)]
    [InlineData(2, (uint)CanonicalButtons.B)]
    [InlineData(3, (uint)CanonicalButtons.Y)]
    [InlineData(4, (uint)CanonicalButtons.LeftShoulder)]
    [InlineData(5, (uint)CanonicalButtons.RightShoulder)]
    [InlineData(6, (uint)CanonicalButtons.None)]
    [InlineData(8, (uint)CanonicalButtons.View)]
    [InlineData(9, (uint)CanonicalButtons.Menu)]
    [InlineData(10, (uint)CanonicalButtons.LeftStick)]
    [InlineData(11, (uint)CanonicalButtons.RightStick)]
    [InlineData(15, (uint)CanonicalButtons.RearPaddle2)]
    [InlineData(16, (uint)CanonicalButtons.RearPaddle1)]
    public void DescriptorButtons_FollowHcsDirectInputIndices(int index, uint expected)
    {
        Assert.Equal((CanonicalButtons)expected, HidDescriptorGamepad.Button(index));
    }

    [Fact]
    public void DescriptorButtons_AgreeWithTheMeasuredReportLayout()
    {
        // The measured decoder's bits are DirectInput's button indices in order from byte 5 bit 4, so
        // both decoders must name the same button for every index they share.
        for (var index = 0; index < 20; index++)
        {
            if (index is 6 or 7)
            {
                continue;
            }

            var report = new byte[64];
            report[0] = 0x01;
            report[1] = report[2] = report[3] = report[4] = 0x80;
            report[5] = 0x08;
            var bit = index + 4;
            report[5 + bit / 8] |= (byte)(1 << (bit % 8));
            var measured = ClawControllerCodec.Decode(report, DateTimeOffset.UtcNow).Buttons;
            Assert.Equal(HidDescriptorGamepad.Button(index), measured);
        }
    }

    private static HapticOutputFrame Frame(float low, float high)
    {
        return new HapticOutputFrame
        {
            LowFrequency = low,
            HighFrequency = high,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static async Task<ControllerService> AcquireControllerAsync(
        FakeControllerSource source,
        TestPluginHostAdapter host,
        DeviceRecoveryJournal journal,
        ClawModel model,
        FakeMcuTransport? mcu = null)
    {
        ControllerService controller = new(
            mcu ?? new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource(), model),
            host,
            journal,
            model,
            TestTiming.NoDelay,
            _ => { })
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);
        return controller;
    }

    private static ControllerTopology DirectInputTopology()
    {
        return new ControllerTopology(
            ClawControllerMode.DirectInput,
            ClawHardwareFacts.DirectInputProductId,
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            []);
    }

    private static EngineStartContext Context(IDeviceSink host, string stateDirectory, ClawModel model)
    {
        return new EngineStartContext
        {
            Host = host,
            Identity = FakeIdentityReader.CreateState().Snapshot with { BaseboardProduct = model.BoardProduct },
            DeviceDefinitionId = model.DefinitionId,
            StateDirectory = stateDirectory,
            Journal = TestRecovery.Open(stateDirectory),
            ControllerManagementEnabled = false
        };
    }

    private static ServiceEngineFixture Engine(ClawHardwareServices services)
    {
        return new ServiceEngineFixture(context => new ClawDeviceEngine(context, services));
    }

    private static ClawHardwareServices Services(
        FakeIdentityReader identity,
        FakeWmiTransport? wmi = null,
        FakeMcuTransport? mcu = null)
    {
        return new ClawHardwareServices(
            wmi ?? new FakeWmiTransport(),
            new FakeOemEventSource(),
            mcu ?? new FakeMcuTransport(),
            new FakeControllerSource(),
            new FakeMotionSource(),
            new FakeChordSuppressor(),
            new OemButtonLatch(),
            TestTiming.NoDelay,
            identity.ReadOnAcPower);
    }
}
