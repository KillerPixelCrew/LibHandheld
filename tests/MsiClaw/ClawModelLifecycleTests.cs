using LibHandheld.Families.MsiClaw.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Testing;
using LibHandheld.Testing;
using static LibHandheld.Families.MsiClaw.Tests.Builders.ClawCommands;

namespace LibHandheld.Families.MsiClaw.Tests;

/// <summary>Per-model behaviour through the plugin's own lifecycle and services.</summary>
[Collection("plugin-trace")]
public sealed class ClawModelLifecycleTests
{
    [Fact]
    public async Task StartAsync_PublishesTheModelsPowerRangesAndPresets()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.Claw8ExCg3Em };
        await using ClawDeviceEngine plugin = new(Services(identity));
        TestPluginHostAdapter host = new(CycleGeneration);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8ExCg3Em), CancellationToken.None);

        var descriptors = host.DescriptorSets.Last().Descriptors;
        var sustained = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerSustained);
        var boost = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerBoost);
        Assert.Equal((20, 37), (sustained.Minimum, sustained.Maximum));
        Assert.Equal((20, 37), (boost.Minimum, boost.Maximum));
        Assert.Equal(ClawModels.Claw8ExCg3Em.PowerPresets, sustained.PowerPresets);
    }

    [Fact]
    public async Task StartAsync_RefusesAnIdentityOfAnotherModel()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.A1M };
        await using ClawDeviceEngine plugin = new(Services(identity));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("0211", 0x01FA)]
    [InlineData("0230", 0x024A)]
    public async Task StartAsync_ReadsLightingAtTheAddressForTheMcuRevision(string revision, int address)
    {
        using TemporaryDirectory state = new();
        FakeMcuTransport mcu = new();
        await using ClawDeviceEngine
            plugin = new(Services(new FakeIdentityReader { McuRevision = revision }, mcu: mcu));

        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);

        Assert.NotEmpty(mcu.ReadAddresses);
        Assert.All(mcu.ReadAddresses, read => Assert.Equal(address, read));
    }

    [Fact]
    public async Task ExecuteCommand_ReadsThePowerSourceButNotTheCycleIdentity()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new();
        await using ClawDeviceEngine plugin = new(Services(identity));
        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);

        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), CancellationToken.None);

        Assert.Equal(1, identity.ReadCount);
        Assert.Equal(1, identity.AcPowerReads);
    }

    [Theory]
    [InlineData(0xC6, "user")]
    [InlineData(0xC3, null)]
    [InlineData(0xC4, "sport")]
    [InlineData(0x86, "inactive")]
    public void ScenarioDecode_UsesTheCg3EmUserValue(int raw, string? expected)
    {
        Assert.Equal(expected, ClawDeviceEngine.Scenario((byte)raw, ClawModels.Claw8ExCg3Em)?.ChoiceValue);
    }

    [Fact]
    public void ScenarioDecode_KeepsUserAtThreeElsewhere()
    {
        Assert.Equal("user", ClawDeviceEngine.Scenario(0xC3, ClawModels.Claw8A2Vm)?.ChoiceValue);
    }

    [Fact]
    public async Task A1MRumble_IsOnOffAt193AndDeclaresItsPaceToTheHost()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new();
        TestPluginHostAdapter host = new(CycleGeneration);
        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
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
        TestPluginHostAdapter host = new(CycleGeneration);
        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
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
        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);

        _ = await AcquireControllerAsync(new FakeControllerSource(), new TestPluginHostAdapter(CycleGeneration),
            journal, ClawModels.Claw8A2Vm, mcu);

        // FakeIdentityReader reports MCU 0230: HC's nearest row is 0x0219, M1 0x00BA and M2 0x0163.
        Assert.Equal([(0x00BA, 1, 0), (0x0163, 1, 0)],
            mcu.Writes.Select(write => ((int)write.Address, (int)write.Payload[0], (int)write.Payload[1])));
        Assert.Equal(1, mcu.RomSyncs);
    }

    [Fact]
    public async Task ControllerRelease_ReturnsToXInputAsHcDoes()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        FakeMcuTransport mcu = new();
        var controller = await AcquireControllerAsync(source, new TestPluginHostAdapter(CycleGeneration), journal,
            ClawModels.Claw8A2Vm, mcu);

        await controller.ReleaseControllerAsync(Deadline.After(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.Equal(ClawControllerMode.XInput, Assert.Single(mcu.ModeSwitches));
    }

    [Theory]
    [InlineData("QuickSettings", "Short")]
    [InlineData("QuickSettingsLong", "Long")]
    public async Task FirmwareChord_RaisesQuickSettingsWithoutMsiEvent(string chord, string press)
    {
        TestPluginHostAdapter host = new(CycleGeneration);
        DeviceTrace.Install(host);
        OemButtonLatch latch = new();
        OemEventService oem = new(new FakeOemEventSource(), host, latch);
        FakeChordSuppressor suppressor = new();
        ChordSuppressorService service = new(suppressor, oem, host);
        _ = await service.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(CycleGeneration, Deadline.After(TimeSpan.FromSeconds(10)),
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
        TestPluginHostAdapter host = new(CycleGeneration);
        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
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

    [Theory]
    [InlineData("bios:E1T52IMS.114", "Pending", "bios:E1T52IMS.114", "Restore")]
    [InlineData("bios:E1T52IMS.114", "RestoreFailed", "bios:E1T52IMS.114", "Keep")]
    [InlineData("bios:E1T52IMS.114", "RestoredUnverified", "bios:E1T52IMS.114", "Keep")]
    [InlineData("bios:E1T52IMS.114", "RestoreFailed", "bios:E1T52IMS.115", "Discard")]
    [InlineData("bios:E1T52IMS.114", "Pending", null, "Wait")]
    public void Decide_RestoresAPendingEntryOnceOnTheSameBios(
        string bound,
        string status,
        string? current,
        string action)
    {
        Assert.Equal(
            Enum.Parse<ClawReconciliationAction>(action),
            ClawRecoveryJournal.Decide(PowerEntry(bound, Enum.Parse<DeviceRecoveryStatus>(status)), current,
                "ec:1T52EMS1.109;msi-acpi:8.0"));
    }

    [Theory]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "Pending", "ec:1T52EMS1.109;msi-acpi:8.0", "Restore")]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "RestoreFailed", "ec:1T52EMS1.109;msi-acpi:8.0", "Discard")]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "Pending", "ec:1T52EMS1.110;msi-acpi:8.0", "Discard")]
    [InlineData("ec:unknown;msi-acpi:8.0", "Pending", "ec:unknown;msi-acpi:8.0", "Discard")]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "Pending", null, "Wait")]
    public void Decide_MigratesALegacyEntryOnlyWhenItsOldBindingStillReads(
        string bound,
        string status,
        string? legacy,
        string action)
    {
        Assert.Equal(
            Enum.Parse<ClawReconciliationAction>(action),
            ClawRecoveryJournal.Decide(PowerEntry(bound, Enum.Parse<DeviceRecoveryStatus>(status)),
                "bios:E1T52IMS.114", legacy));
    }

    [Fact]
    public async Task StartAsync_EntryFromAnotherEcIsDroppedAndPowerStaysAvailable()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(ServiceIds.Power, "ec:1T52EMS1.108;msi-acpi:8.0",
                ClawRecoveryValues.Power(new PowerPair(20, 30, 0xC1)), CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        await using ClawDeviceEngine plugin = new(Services(new FakeIdentityReader(), wmi));
        TestPluginHostAdapter host = new(CycleGeneration);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8A2Vm), CancellationToken.None);

        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Contains(host.CapabilityStates,
            capability => capability is { CapabilityId: CapabilityIds.PowerSustained, Available: true });
        var reopened = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(reopened.OutstandingEntries);
    }

    [Fact]
    public async Task Journal_ARecordWithAMemberThisBuildDoesNotKnowStillLoads()
    {
        // An older prerelease wrote a capabilityId beside each entry. Refusing the record would keep
        // power, fans and the controller blocked with nothing in the product able to clear it.
        using TemporaryDirectory state = new();
        await File.WriteAllTextAsync(Path.Combine(state.Root, "temporary-state.v1.json"),
            """
            {"version":1,"entries":[{"serviceId":"msi-power","firmwareIdentity":"ec:1T52EMS1.108;msi-acpi:8.0",
            "capabilityId":"power.sustained","originalState":{"kind":"Power","sustainedWatts":20,"boostWatts":30,
            "scenario":193},"status":"Pending"}]}
            """);

        var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);

        Assert.Null(journal.FailureReason);
        Assert.Equal(20, journal.OriginalStateFor(ServiceIds.Power)?.SustainedWatts);
    }

    [Fact]
    public async Task IdentityReader_UndecodableEcBindsToTheBiosAndKeepsWmiAvailable()
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse("Get_WMI", 1, new byte[32]);
        wmi.SetResponse("Get_EC", 0, new byte[32]);
        WindowsClawIdentityReader reader = new(wmi, () => new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = ClawModels.A1M.BoardProduct,
            BiosVersion = "E1T41IMS.105"
        }, () => [], () => true, () => 0x0230);

        var identity = await reader.ReadAsync(CancellationToken.None);

        Assert.True(identity.WmiAvailable);
        Assert.Equal("bios:E1T41IMS.105", identity.RecoveryBinding);
        Assert.Equal("bios:E1T41IMS.105;msi-acpi:0.0", identity.LegacyRecoveryBinding);
        Assert.Equal("0230", identity.Snapshot.McuFirmwareVersion);
        Assert.Equal(ClawModels.A1M, identity.Model);
    }

    [Theory]
    [InlineData("bios:E1T41IMS.105;msi-acpi:0.0", true)]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", true)]
    [InlineData("ec:unknown;msi-acpi:1.0", true)]
    [InlineData("bios:E1T41IMS.105", false)]
    [InlineData("mcu", false)]
    [InlineData("msi-acpi:8.0", false)]
    public void IsLegacy_AcceptsAnyEcBinding(string identity, bool expected)
    {
        Assert.Equal(expected, ClawFirmwareIdentities.IsLegacy(identity));
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
        ClawRecoveryJournal journal,
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
            TestTiming.NoDelay)
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(CycleGeneration, Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState() with { Model = model }),
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

    private static DeviceRecoveryEntry<ClawRecoveryState> PowerEntry(string firmware, DeviceRecoveryStatus status)
    {
        return new DeviceRecoveryEntry<ClawRecoveryState>
        {
            ServiceId = ServiceIds.Power,
            FirmwareIdentity = firmware,
            OriginalState = ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
            Status = status
        };
    }

    private static PluginStartContext Context(IDeviceSink host, string stateDirectory, ClawModel model)
    {
        return new PluginStartContext
        {
            Host = host,
            CycleGeneration = host.CycleGeneration,
            DeviceDefinitionId = model.DefinitionId,
            StateDirectory = stateDirectory,
            ControllerManagementEnabled = false
        };
    }

    private static ClawHardwareServices Services(
        FakeIdentityReader identity,
        FakeWmiTransport? wmi = null,
        FakeMcuTransport? mcu = null)
    {
        return new ClawHardwareServices(
            identity,
            wmi ?? new FakeWmiTransport(),
            new FakeOemEventSource(),
            mcu ?? new FakeMcuTransport(),
            new FakeControllerSource(),
            new FakeMotionSource(),
            new FakeChordSuppressor(),
            new OemButtonLatch(),
            TestTiming.NoDelay);
    }
}
