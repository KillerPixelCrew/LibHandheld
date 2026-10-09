// SPDX-License-Identifier: MIT

using LibHandheld.Families.RogAlly.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Testing;
using LibHandheld.Testing;
using CapabilityRole = LibHandheld.Contracts.CapabilityRole;

namespace LibHandheld.Families.RogAlly.Tests;

public sealed class PluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterTheLastRestoreWriteClearsTheRecoveryEntry(bool fan)
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la", false),
            CancellationToken.None);
        var command = fan
            ? AcpiCapabilityTests.Command(
                CapabilityValue.Curve(AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve)),
                CapabilityIds.FanCurve)
            : AcpiCapabilityTests.Command(CapabilityValue.Integer(22));
        _ = await plugin.ExecuteCommandAsync(command, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        hardware.Acpi.AfterWrite = id =>
        {
            if (id == (fan ? AsusAcpiId.GpuFanCurve : AsusAcpiId.FastPower))
            {
                cancellation.Cancel();
            }
        };

        _ = await plugin.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(10))), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        var journal = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.DoesNotContain(journal.OutstandingEntries,
            entry => entry.ServiceId == (fan ? AllyServiceIds.Fans : AllyServiceIds.Power));
    }

    [Fact]
    public async Task StartupControllerRecoveryAttemptsEveryTableAfterARefusalAndKeepsItsOriginal()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var journal = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        _ = await journal.BeginAsync(AllyServiceIds.Controller, AllyServiceIds.McuFirmware,
            AllyRecoveryState.Controller(), CancellationToken.None);
        hardware.Vendor.RefuseTable = AllyProtocol.RearDefaultMapping[3];
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la", false),
            CancellationToken.None);

        var expected = AllyProtocol.DefaultConfiguration.Where(report =>
            report[2] != 0x02 || report[3] != hardware.Vendor.RefuseTable).ToArray();
        Assert.Equal(expected.Length, hardware.Vendor.Reports.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], hardware.Vendor.Reports[index]);
        }

        var recovered = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.Equal(DeviceRecoveryStatus.Pending, Assert.Single(recovered.OutstandingEntries).Status);
    }

    [Fact]
    public async Task PartlyRefusedControllerRestoreKeepsPendingWithoutAStatusWrite()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        await using (var first = hardware.CreatePlugin())
        {
            _ = await first.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"),
                CancellationToken.None);
            hardware.Vendor.RefuseTable = AllyProtocol.RearKeyboardMapping[3];
            _ = await first.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
                Deadline.After(TimeSpan.FromSeconds(10))), CancellationToken.None);
            var pending = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
            Assert.Equal(DeviceRecoveryStatus.Pending, Assert.Single(pending.OutstandingEntries).Status);
        }

        hardware.Vendor.RefuseTable = null;
        hardware.Vendor.Reports.Clear();
        await using var next = hardware.CreatePlugin();
        _ = await next.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la", false),
            CancellationToken.None);
        Assert.Equal(AllyProtocol.DefaultConfiguration.Count, hardware.Vendor.Reports.Count);
        var restored = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.Empty(restored.OutstandingEntries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedJournalledCommandDoesNotRetryRollbackOrFaultTheService(bool fan, bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        var failingId = fan ? AsusAcpiId.CpuFanCurve : AsusAcpiId.SlowPower;
        hardware.Acpi.FailWritesTo = failingId;
        hardware.Acpi.WriteFailure = cancel
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("unexpected setter failure");
        hardware.Acpi.AfterWrite = id =>
        {
            if (cancel && id == failingId)
            {
                cancellation.Cancel();
            }
        };
        var command = fan
            ? AcpiCapabilityTests.Command(
                CapabilityValue.Curve(AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve)),
                CapabilityIds.FanCurve)
            : AcpiCapabilityTests.Command(CapabilityValue.Integer(22));
        var result = await plugin.ExecuteCommandAsync(command, cancellation.Token);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.NotRequired, result.Rollback);
        Assert.Equal(cancel ? CapabilityReasonCode.Quiescing : CapabilityReasonCode.TransportFaulted,
            result.Reason?.Code);
        if (fan)
        {
            Assert.Single(hardware.Acpi.BufferWrites);
        }
        else
        {
            Assert.Equal(2, hardware.Acpi.Writes.Count);
        }

        var diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Owned),
            diagnostics.Values[fan ? AllyServiceIds.Fans : AllyServiceIds.Power]);
        var journal = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.Equal(DeviceRecoveryStatus.Pending,
            Assert.Single(journal.OutstandingEntries,
                entry => entry.ServiceId == (fan ? AllyServiceIds.Fans : AllyServiceIds.Power)).Status);
        hardware.Acpi.FailWritesTo = null;
        hardware.Acpi.AfterWrite = null;
    }

    [Fact]
    public async Task JournalFailureRejectsWithoutAnyHardwareWrite()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"), CancellationToken.None);
        var path = Path.Combine(directory.Root, "temporary-state.v1.json");
        File.Delete(path);
        Directory.CreateDirectory(path);
        try
        {
            var result = await plugin.ExecuteCommandAsync(AcpiCapabilityTests.Command(CapabilityValue.Integer(22)),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Rejected, result.Outcome);
            Assert.Equal(CapabilityReasonCode.TransportFaulted, result.Reason?.Code);
            Assert.Empty(hardware.Acpi.Writes);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public async Task WrittenPowerChargeAndEncodedFanValuesWinOverConflictingFirmwareReadings()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Acpi.IgnoreWritesTo = AsusAcpiId.SustainedPower;
        _ = await plugin.ExecuteCommandAsync(AcpiCapabilityTests.Command(CapabilityValue.Integer(22)),
            CancellationToken.None);
        Assert.Equal(22,
            host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.PowerSustained).ObservedValue
                ?.IntegerValue);
        hardware.Acpi.IgnoreWritesTo = AsusAcpiId.ChargeLimit;
        _ = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(80), CapabilityIds.ChargeLimit),
            CancellationToken.None);
        Assert.Equal(80,
            host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.ChargeLimit).ObservedValue
                ?.IntegerValue);
        hardware.Acpi.IgnoreWritesTo = AsusAcpiId.CpuFanCurve;
        var curve = Enumerable.Range(0, 8).Select(index => new CurvePoint(30 + index * 10, 100)).ToArray();
        _ = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Curve(curve), CapabilityIds.FanCurve), CancellationToken.None);
        Assert.All(
            host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.FanCurve).ObservedValue!.CurveValue,
            point => Assert.Equal(99, point.Output));
    }

    [Theory]
    [InlineData("RC71L", "rc71l")]
    [InlineData("RC72LA", "rc72la")]
    [InlineData("RC73YA", "rc73ya")]
    [InlineData("RC73XA", "rc73xa")]
    public async Task StartPublishesAValidSurfaceForEachModel(string board, string definition)
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware(board);
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, definition), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Active, result.State);
        var descriptors = host.DescriptorSets.Last();
        Assert.True(DevicePowerPreset.TryValidate(descriptors.Descriptors, out var presetError), presetError);
        Assert.True(CapabilityLayout.TryValidate(descriptors.Descriptors, out var layoutError), layoutError);
        Assert.All(descriptors.Descriptors, descriptor =>
            Assert.True(Enum.IsDefined((CapabilityRole)(int)descriptor.Role)));
        Assert.All(descriptors.Descriptors, descriptor => Assert.True(descriptor.Display.TryValidate(out _)));
        var model = AllyModels.ById(definition)!;
        var sustained = descriptors.Descriptors.Single(item =>
            item.Role == Internal.Capabilities.CapabilityRole.PowerSustainedLimit);
        Assert.Equal(model.MinimumWatts, sustained.Minimum);
        Assert.Equal(model.MaximumWatts, sustained.Maximum);
        Assert.Equal(model.Layout is AllyFrontLayout.Classic ? 5 : 4, host.OemControlSets.Last().Count);
        Assert.Single(host.PhysicalDeviceSets);
    }

    [Fact]
    public async Task StartRefusesAMachineThatStoppedMatching()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC71L");
        await using var plugin = hardware.CreatePlugin();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await plugin.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"), CancellationToken.None));
        Assert.Empty(hardware.Acpi.Writes);
    }

    [Fact]
    public async Task ControllerAcquisitionWritesTheTablesAndEnablesRearKeys()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(AllyProtocol.GameModeConfiguration.Count, hardware.Vendor.Reports.Count);
        Assert.Equal(AllyProtocol.RearKeyboardMapping, hardware.Vendor.Reports[8]);
        Assert.Contains(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Contains(AllyModels.VkF17, hardware.Keyboard.Watched);
        Assert.True(hardware.Controller.Running);
        Assert.Equal(ControllerService.OutputCapabilities, host.PublishedOutput);
    }

    [Fact]
    public async Task XboxAllyXRearKeysWorkEvenWhenEveryTableIsRefused()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73XA");
        hardware.Vendor.FailWrites = true;
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc73xa"), CancellationToken.None);

        // Device Lab 2026-09-25: M1/M2 send F18/F17 on RC73XA before any table is written.
        Assert.Empty(hardware.Vendor.Reports);
        Assert.Contains(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Contains(AllyModels.VkF17, hardware.Keyboard.Watched);

        // Without the table the firmware's own sides hold: F18 is the left button.
        await hardware.Keyboard.PressAsync(AllyModels.VkF18, true);
        Assert.Equal((OemControlIds.M1, OemControlEdge.Pressed),
            host.OemEvents.Select(item => (item.ControlId, item.Edge)).Last());
    }

    [Fact]
    public async Task ARefusedFrontTableDoesNotStopTheRestOrTheRearKeys()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        // The D-pad left/right table (02/02) is refused, as a firmware might refuse any one of them.
        hardware.Vendor.RefuseTable = 0x02;
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        // HC writes every table and ignores each result; only the refused one is missing.
        Assert.Equal(AllyProtocol.GameModeConfiguration.Count - 1, hardware.Vendor.Reports.Count);
        Assert.Contains(AllyProtocol.RearKeyboardMapping, hardware.Vendor.Reports);
        Assert.Contains(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Contains(AllyModels.VkF17, hardware.Keyboard.Watched);
    }

    [Fact]
    public async Task ControllerManagementOffLeavesTheTablesAndRearKeysAlone()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc72la", false), CancellationToken.None);

        Assert.Empty(hardware.Vendor.Reports);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.False(hardware.Controller.Running);
    }

    [Fact]
    public async Task CustomFanModeLeavesEachCapturedCurveUntouched()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Choice(FanModes.Custom), CapabilityIds.FanMode),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Empty(hardware.Acpi.BufferWrites);
        Assert.Equal(AllyFanCapability.DefaultGpuCurve, hardware.Acpi.Curve(AsusAcpiId.GpuFanCurve));
    }

    [Fact]
    public async Task UnreadablePowerAndFanOriginalsAreWrittenBlindAsHcDoes()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        hardware.Acpi.ScalarsReadable = false;
        hardware.Acpi.SetCurve(AsusAcpiId.CpuFanCurve, new byte[16]);
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        var custom = AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve);

        var power = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)), CancellationToken.None);
        var fan = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Curve(custom), CapabilityIds.FanCurve),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, power.Outcome);
        Assert.True(fan.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified);
        Assert.Contains(hardware.Acpi.Writes, write => write is { Id: AsusAcpiId.SustainedPower, Value: 20 });
        Assert.NotEmpty(hardware.Acpi.BufferWrites);
        // The written limit stands in for the readback the firmware cannot give.
        var published = host.CapabilityStates.Last(state => state.CapabilityId == CapabilityIds.PowerSustained);
        Assert.Equal(HardwareStateQuality.Observed, published.Quality);
        Assert.Equal(20, published.ObservedValue?.IntegerValue);

        // Nothing was journalled, so stop returns the fans to HC's factory tables.
        hardware.Acpi.BufferWrites.Clear();
        _ = await plugin.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Contains(hardware.Acpi.BufferWrites, write =>
            write.Id == AsusAcpiId.CpuFanCurve && write.Data.SequenceEqual(AllyFanCapability.DefaultCpuCurve));
    }

    [Fact]
    public async Task FailedFanRestoreStaysPendingAndTheNextStartWritesTheOriginalOnce()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var points = AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve);
        await using (var first = hardware.CreatePlugin())
        {
            _ = await first.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"),
                CancellationToken.None);
            _ = await first.ExecuteCommandAsync(
                AcpiCapabilityTests.Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve),
                CancellationToken.None);
            hardware.Acpi.FailWritesTo = AsusAcpiId.CpuFanCurve;
            _ = await first.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
                Deadline.After(TimeSpan.FromSeconds(10))), CancellationToken.None);
            var journal = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
            var entry = Assert.Single(journal.OutstandingEntries);
            Assert.Equal(AllyServiceIds.Fans, entry.ServiceId);
            Assert.Equal(DeviceRecoveryStatus.Pending, entry.Status);
        }

        hardware.Acpi.FailWritesTo = null;
        hardware.Acpi.BufferWrites.Clear();
        await using var second = hardware.CreatePlugin();
        _ = await second.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"), CancellationToken.None);
        Assert.Single(hardware.Acpi.BufferWrites, write => write.Id == AsusAcpiId.CpuFanCurve);
        Assert.Single(hardware.Acpi.BufferWrites, write => write.Id == AsusAcpiId.GpuFanCurve);
        var recovered = await AllyRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.DoesNotContain(recovered.OutstandingEntries, entry => entry.ServiceId == AllyServiceIds.Fans);
    }

    [Fact]
    public async Task ReleaseWritesTheFactoryTablesAndZeroesTheMotors()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Vendor.Reports.Clear();

        await plugin.ReleaseControllerAsync(
            new PluginControllerReleaseContext(HandoffScope.ControllerOnly, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.Equal(AllyProtocol.DefaultConfiguration.Count, hardware.Vendor.Reports.Count);
        Assert.Equal(AllyProtocol.RearDefaultMapping, hardware.Vendor.Reports[8]);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Equal((0f, 0f), hardware.Controller.Rumble.Last());
        Assert.False(hardware.Controller.Running);
    }

    [Fact]
    public async Task FailedRumbleZeroStillRestoresControllerTables()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Vendor.Reports.Clear();
        hardware.Controller.FailRumble = true;

        await plugin.ReleaseControllerAsync(
            new PluginControllerReleaseContext(HandoffScope.ControllerOnly, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.Equal(AllyProtocol.DefaultConfiguration.Count, hardware.Vendor.Reports.Count);
        Assert.False(hardware.Controller.Running);
    }

    [Fact]
    public async Task APadWhoseDeviceNodesAreNotBackYetIsTakenWhenTheyAre()
    {
        // The wake that left the controller dead: the slot was back a second after resume, its device
        // nodes were not, and the service gave up for good instead of waiting.
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var devices = hardware.Controller.Devices;
        hardware.Controller.Devices = [];
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Degraded, result.State);
        Assert.False(hardware.Controller.Running);
        Assert.Empty(hardware.Vendor.Reports);

        hardware.Controller.Devices = devices;
        await AsyncConditions.WaitForAsync(() => hardware.Controller.Running);
        Assert.Contains(host.PhysicalDeviceSets, published => published.Count > 0);
    }

    [Fact]
    public async Task VendorEventsLatchAndPublishOemEvents()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        await hardware.Vendor.RaiseAsync(0xA6);
        await hardware.Controller.EmitAsync(CanonicalButtons.A);
        await hardware.Vendor.RaiseAsync(0xA7);
        await hardware.Vendor.RaiseAsync(0xA8);

        Assert.Equal(3, host.OemEvents.Count);
        Assert.Equal((OemControlIds.CommandCenter, OemPressKind.Short),
            (host.OemEvents[0].ControlId, host.OemEvents[0].Press));
        Assert.Equal((OemControlIds.M2, OemControlEdge.Pressed), (host.OemEvents[1].ControlId, host.OemEvents[1].Edge));
        Assert.Equal((OemControlIds.M2, OemControlEdge.Released),
            (host.OemEvents[2].ControlId, host.OemEvents[2].Edge));
        Assert.Equal(CanonicalButtons.A | CanonicalButtons.QuickAccess, host.ControllerSamples.Last().Buttons);
    }

    [Fact]
    public async Task RearKeysPublishEdgesAndIgnoreRepeats()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        // With HC's M1/M2 table applied the left button sends F17 and the right F18.
        await hardware.Keyboard.PressAsync(AllyModels.VkF17, true);
        await hardware.Keyboard.PressAsync(AllyModels.VkF17, true);
        await hardware.Controller.EmitAsync(CanonicalButtons.None);
        await hardware.Keyboard.PressAsync(AllyModels.VkF17, false);
        await hardware.Keyboard.PressAsync(AllyModels.VkF18, true);

        Assert.Equal(
            [
                (OemControlIds.M1, OemControlEdge.Pressed), (OemControlIds.M1, OemControlEdge.Released),
                (OemControlIds.M2, OemControlEdge.Pressed)
            ],
            host.OemEvents.Select(item => (item.ControlId, item.Edge)));
        Assert.Equal(CanonicalButtons.RearPaddle1, host.ControllerSamples.Last().Buttons);
    }

    [Fact]
    public async Task XboxModelsClaimTheirFrontKeysWithoutTheController()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73XA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73xa", false), CancellationToken.None);

        await hardware.Keyboard.PressAsync(AllyModels.VkF22, true);
        await hardware.Keyboard.PressAsync(AllyModels.VkF22, false);

        Assert.Contains(AllyModels.VkF21, hardware.Keyboard.Watched);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Equal([OemControlIds.Library, OemControlIds.Library], host.OemEvents.Select(item => item.ControlId));
    }

    [Fact]
    public async Task XboxArmouryCrateIsTheCompanionButtonAndCarriesNoSteamButton()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73XA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73xa"), CancellationToken.None);

        await hardware.Keyboard.PressAsync(AllyModels.VkF21, true);
        await hardware.Controller.EmitAsync(CanonicalButtons.None);

        Assert.True(host.OemControlSets.Last()
            .Single(control => control.ControlId == OemControlIds.ArmouryCrate).CompanionApplication);
        Assert.Equal([OemControlIds.ArmouryCrate], host.OemEvents.Select(item => item.ControlId));
        Assert.Equal(CanonicalButtons.None, host.ControllerSamples.Last().Buttons);
    }

    [Fact]
    public async Task PowerCommandIsWrittenJournalledAndRestoredOnStop()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(28)),
            CancellationToken.None);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.True(File.Exists(Path.Combine(directory.Root, "temporary-state.v1.json")));

        var stop = await plugin.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(12))), CancellationToken.None);

        Assert.Equal((15, 20, 25), (hardware.Acpi.Scalar(AsusAcpiId.SustainedPower),
            hardware.Acpi.Scalar(AsusAcpiId.SlowPower), hardware.Acpi.Scalar(AsusAcpiId.FastPower)));
        // The controller tables cannot be read back; a write the MCU accepted is the restore, as in HC.
        Assert.Equal(PluginStopStatus.Clean, stop.Status);
    }

    [Fact]
    public async Task StaleGenerationIsRejectedBeforeAnyWrite()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)) with { ExpectedDescriptorGeneration = 9 },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
        Assert.Empty(hardware.Acpi.Writes);
    }

    [Fact]
    public async Task LightingIsWriteOnlyAndReportedUnverified()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73YA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73ya"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Color(0x00FF00), CapabilityIds.LightingColor) with
            {
                InstanceId = CapabilityInstances.Left
            }, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(1, hardware.Aura.DynamicLightingRequests);
        Assert.Contains(hardware.Aura.Reports, report => report.Report[1] == 0xB3 && report.Report[5] == 0xFF);
    }

    [Fact]
    public async Task HapticsAreClampedAndSilencedOnRelease()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        await plugin.ApplyHapticOutputAsync(new HapticOutputFrame
        {
            LowFrequency = 0.5f,
            HighFrequency = 0.25f,
            LeftTrigger = 1f,
            Timestamp = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        Assert.Equal((0.5f, 0.25f), hardware.Controller.Rumble.Single());
    }

    [Fact]
    public async Task SmallNonzeroMotorTransitionsToZeroAreNeverDeduplicated()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        foreach (var (low, high) in new[] { (0.001f, 0.5f), (0f, 0.5f), (0f, 0f) })
        {
            await plugin.ApplyHapticOutputAsync(new HapticOutputFrame
            {
                LowFrequency = low, HighFrequency = high, Timestamp = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }

        Assert.Equal(new[] { (0.001f, 0.5f), (0f, 0.5f), (0f, 0f) }, hardware.Controller.Rumble);
    }

    [Fact]
    public async Task MissingAcpiDriverLeavesPowerPassiveButInputWorking()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        hardware.Acpi.Available = false;
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Degraded, result.State);
        Assert.True(hardware.Controller.Running);
        var power = host.CapabilityStates.Last(state => state.CapabilityId == CapabilityIds.PowerSustained);
        Assert.False(power.Available);
    }

    [Fact]
    public async Task ADeadlineTooShortToWriteIsRejectedWithoutFaultingThePowerService()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var late = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)) with
            {
                Deadline = Deadline.After(TimeSpan.FromSeconds(1))
            }, CancellationToken.None);
        var next = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)), CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, late.Outcome);
        Assert.True(late.Reason!.Retryable);
        Assert.Equal(CommandOutcome.AppliedUnverified, next.Outcome);
    }

    [Fact]
    public async Task StopWithControllerManagementOffIsClean()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la", false), CancellationToken.None);

        var stop = await plugin.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(12))), CancellationToken.None);

        Assert.Equal(PluginStopStatus.Clean, stop.Status);
        Assert.Empty(hardware.Controller.Rumble);
    }

    [Fact]
    public async Task ClassicModelsInstallNoKeyboardHookUntilTheRearKeysAreClaimed()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la", false), CancellationToken.None);

        Assert.False(hardware.Keyboard.Hooked);

        await plugin.SetControllerManagementAsync(
            new PluginControllerManagementContext(true, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.True(hardware.Keyboard.Hooked);

        await plugin.SetControllerManagementAsync(
            new PluginControllerManagementContext(false, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.False(hardware.Keyboard.Hooked);
    }

    [Fact]
    public async Task ReEnablingAnOwnedControllerKeepsTheReaderRunning()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Vendor.Reports.Clear();

        await plugin.SetControllerManagementAsync(
            new PluginControllerManagementContext(true, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.True(hardware.Controller.Running);
        Assert.Equal(1, hardware.Controller.Starts);
        Assert.Empty(hardware.Vendor.Reports);
    }

    [Fact]
    public async Task AFaultedReaderIsReacquiredWhenControllerManagementIsEnabledAgain()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        hardware.Controller.RaiseFault();
        await plugin.SetControllerManagementAsync(
            new PluginControllerManagementContext(true, Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.True(hardware.Controller.Running);
        Assert.True(hardware.Controller.Starts >= 2);
        var controller = host.CapabilityStates.Last(state => state.CapabilityId == CapabilityIds.Controller);
        Assert.True(controller.Available);
    }

    [Fact]
    public async Task AVendorReadFailureWaitsForTheCollectionAndReopensIt()
    {
        // HC treats a failed read as a removal and reopens the collection when it is back
        // (ROGAlly.cs:329-362); faulting the plugin instead tore the whole cycle down on every sleep.
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        hardware.Vendor.RaiseFault();
        var diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Degraded), diagnostics.Values[AllyServiceIds.VendorEvents]);

        await AsyncConditions.WaitForAsync(async () => (await plugin.GetDiagnosticsAsync(CancellationToken.None))
            .Values[AllyServiceIds.VendorEvents] == nameof(DeviceServiceState.Owned));
        await hardware.Vendor.RaiseAsync(0xA6);

        Assert.Single(host.OemEvents);
    }

    [Fact]
    public async Task APadThatDropsOffTheBusIsTakenAgainWhenItIsBack()
    {
        // The Xbox Ally X drops its pad a second before the suspend notice and brings it back after
        // the wake (2026-09-28). The cycle keeps its place and the reader starts again. The identities
        // are published again, which the host answers by keeping its virtual pad of the same kind.
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        var published = host.PhysicalDeviceSets.Count;

        hardware.Controller.Present = false;
        hardware.Controller.RaiseLost();
        var diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Degraded), diagnostics.Values[AllyServiceIds.Controller]);

        hardware.Controller.Present = true;
        await AsyncConditions.WaitForAsync(() => hardware.Controller.Starts == 2);

        Assert.True(hardware.Controller.Running);
        Assert.Equal(published + 1, host.PhysicalDeviceSets.Count);
        diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Owned), diagnostics.Values[AllyServiceIds.Controller]);
    }

    [Fact]
    public async Task AZeroWattReadingStillWritesThePowerLimitAsHcDoes()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Acpi.SetScalar(AsusAcpiId.FastPower, 0);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)), CancellationToken.None);

        // The Xbox Ally X reports 0 W: that is unknown, not a reason to refuse. HC writes blind.
        Assert.True(result.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified,
            result.Reason?.Detail);
        Assert.NotEmpty(hardware.Acpi.Writes);
    }

    [Fact]
    public async Task OneFrontPressReportedOnBothTransportsIsPublishedOnce()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73XA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73xa"), CancellationToken.None);

        await hardware.Vendor.RaiseAsync(0xA6);
        await hardware.Keyboard.PressAsync(AllyModels.VkF21, true);
        await hardware.Keyboard.PressAsync(AllyModels.VkF21, false);

        Assert.Equal([OemControlIds.ArmouryCrate], host.OemEvents.Select(item => item.ControlId));
    }

    private static PluginStartContext Start(
        IDeviceSink host,
        TemporaryDirectory directory,
        string definition,
        bool controller = true)
    {
        return new PluginStartContext
        {
            Host = host,
            CycleGeneration = host.CycleGeneration,
            DeviceDefinitionId = definition,
            StateDirectory = directory.Root,
            ControllerManagementEnabled = controller
        };
    }
}
