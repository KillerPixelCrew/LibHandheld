using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibHandheld.Families.MsiClaw.Tests.Fakes;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;
using LibHandheld.Testing;
using static LibHandheld.Families.MsiClaw.Tests.Builders.ClawCommands;

namespace LibHandheld.Families.MsiClaw.Tests;

// DeviceTrace is a process-wide static and the plugin installs its own sink in StartAsync, so any
// class that drives the lifecycle has to be serialized against one that asserts on traces.
[Collection("plugin-trace")]
public sealed class ClawDeviceEngineTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledStartupRestoreRetiresCompletedOriginalsAndRetainsIncompleteOnes(bool complete)
    {
        using TemporaryDirectory state = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        _ = await journal.BeginAsync(ClawServiceIds.Power, "bios:E1T52IMS.114",
            ClawRecoveryState.Power(new PowerPair(30, 37, 0xC1)), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        FakeWmiTransport wmi = new();
        wmi.AfterSetter = (_, package) =>
        {
            if (package[0] == (complete ? ClawHardwareFacts.PowerBoostAddress : ClawHardwareFacts.ScenarioAddress))
            {
                cancellation.Cancel();
            }
        };
        await using (var first = Engine(CreateServices(wmi)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await first.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root),
                    cancellation.Token));
        }

        var recovered = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        if (complete)
        {
            Assert.Empty(recovered.Entries<ClawRecoveryState>());
        }
        else
        {
            Assert.Single(recovered.Entries<ClawRecoveryState>());
        }

        wmi.AfterSetter = null;
        wmi.Writes.Clear();
        await using var next = Engine(CreateServices(wmi));
        _ = await next.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root),
            CancellationToken.None);
        Assert.Equal(complete ? 0 : 3, wmi.Writes.Count);
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Empty((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
            .Entries<ClawRecoveryState>());
        var diagnostics = await next.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Owned), diagnostics["msiclaw-native"]);
    }

    [Fact]
    public async Task CancellationAfterTheLastReleaseWriteStillClearsThePowerRecoveryEntry()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        _ = await plugin.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root),
            CancellationToken.None);
        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            }, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        wmi.AfterSetter = (_, package) =>
        {
            if (package[0] == ClawHardwareFacts.PowerBoostAddress)
            {
                cancellation.Cancel();
            }
        };

        _ = await plugin.StopAsync(new EngineStopContext(
            Deadline.After(TimeSpan.FromSeconds(10))), cancellation.Token);

        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(journal.Entries<ClawRecoveryState>());
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(37, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Fact]
    public async Task InitialWmiTimeoutLeavesServicesOwnedAndChargeWriteAvailable()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new() { GetterTimeout = true };
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var charge = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.ChargeLimit);
        Assert.Null(charge.ObservedValue);
        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Single(wmi.Writes);
    }

    [Fact]
    public async Task JournalSaveFailureLeavesHardwareWriteAvailable()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var journalPath = Path.Combine(state.Root, "recovery.json");
        File.Delete(journalPath);
        Directory.CreateDirectory(journalPath);
        try
        {
            var result = await plugin.ExecuteCommandAsync(
                Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
                {
                    PairedPowerLimitWatts = 20
                }, CancellationToken.None);
            Assert.Equal(CommandOutcome.Applied, result.Outcome);
            Assert.NotEmpty(wmi.Writes);
        }
        finally
        {
            Directory.Delete(journalPath);
        }
    }

    [Fact]
    public async Task CommandRefreshDoesNotReassertThePreviousPowerTarget()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            }, CancellationToken.None);
        Assert.Equal(2, wmi.Writes.Count);
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        wmi.Writes.Clear();
        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("sport")), CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Single(wmi.Writes);
        Assert.Equal(ClawHardwareFacts.ScenarioAddress, wmi.Writes[0].Package[0]);
    }

    [Fact]
    public async Task LiveTelemetryRefreshDoesNotReadSettingsOrReassertPower()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        ClawDeviceEngine? provider = null;
        await using var plugin = new ServiceEngineFixture(context =>
            provider = new ClawDeviceEngine(context, CreateServices(wmi)));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            }, CancellationToken.None);
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        wmi.Writes.Clear();
        var reads = wmi.Reads;
        var before = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.Temperature);

        await provider!.RefreshTelemetryAsync(CancellationToken.None);

        var after = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.Temperature);
        Assert.Equal(2, wmi.Reads - reads);
        Assert.Empty(wmi.Writes);
        Assert.Equal(HardwareStateQuality.Observed, after.Quality);
        Assert.True(after.ObservedAt >= before.ObservedAt);
        Assert.All(host.CapabilityStates.Where(value => value.CapabilityId == CapabilityIds.FanRpm)
            .TakeLast(2), value => Assert.Equal(after.ObservedAt, value.ObservedAt));
    }

    // HC's TDP watchdog writes the accepted pair again on the ten-second pass. A failed write leaves its
    // effect unknown: the limits must read unknown, the rest of the pass and the other controls carry
    // on, and the uncertain write is never sent again on its own.
    [Fact]
    public async Task WatchdogWriteFailureLeavesTheLimitsUnknownAndIsNotRetried()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        ManualClock clock = new();
        ClawDeviceEngine? provider = null;
        await using var plugin = new ServiceEngineFixture(context =>
            provider = new ClawDeviceEngine(context, CreateServices(wmi, time: clock)));
        _ = await plugin.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root),
            CancellationToken.None);
        var engine = provider!;
        Assert.Equal(CommandOutcome.Applied, (await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            }, CancellationToken.None)).Outcome);
        Assert.Equal(CommandOutcome.Applied, (await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("sport")), CancellationToken.None)).Outcome);
        var telemetryBefore = Row(CapabilityIds.Temperature).ObservedAt;

        clock.Advance(TimeSpan.FromSeconds(10));
        wmi.FailNextSetter = true;
        await Task.Delay(1);
        await engine.RefreshAsync(CancellationToken.None);

        foreach (var limit in new[] { CapabilityIds.PowerSustained, CapabilityIds.PowerBoost })
        {
            var row = Row(limit);
            Assert.True(row.Available);
            Assert.Null(row.Reason);
            Assert.Null(row.ObservedValue);
            Assert.Equal(HardwareStateQuality.Unknown, row.Quality);
        }

        Assert.Equal("sport", Row(CapabilityIds.Scenario).ObservedValue?.ChoiceValue);
        Assert.True(Row(CapabilityIds.Temperature).ObservedAt > telemetryBefore);

        wmi.Writes.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));
        await engine.RefreshAsync(CancellationToken.None);
        Assert.Empty(wmi.Writes);

        CapabilityState Row(string capabilityId)
        {
            return engine.States.Single(value => value.CapabilityId == capabilityId);
        }
    }

    [Fact]
    public async Task UnrelatedSettingWriteDoesNotRenewCachedTelemetry()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var before = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.Temperature);

        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(60)), CancellationToken.None);

        var after = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.Temperature);
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.Equal(before.ObservedValue, after.ObservedValue);
    }

    [Fact]
    public async Task SensorRefreshDoesNotRenewAcceptedControlsOrOwnership()
    {
        using TemporaryDirectory state = new();
        ClawDeviceEngine? provider = null;
        await using var plugin = new ServiceEngineFixture(context =>
            provider = new ClawDeviceEngine(context, CreateServices()));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var commands = new[]
        {
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            },
            Command(CapabilityIds.Scenario, null, CapabilityValue.Choice("sport")),
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(80)),
            Command(CapabilityIds.FanMode, null, CapabilityValue.Choice("custom")),
            Command("fan.duty", null, CapabilityValue.Integer(50)),
            Command(CapabilityIds.FanCurve, null,
                CapabilityValue.Curve(Enumerable.Range(0, 6)
                    .Select(index => new CurvePoint(index * 10, index * 10)).ToArray())),
            Command(CapabilityIds.LightingBrightness, null, CapabilityValue.Integer(80))
        };
        foreach (var command in commands)
        {
            Assert.Equal(CommandOutcome.Applied,
                (await plugin.ExecuteCommandAsync(command, CancellationToken.None)).Outcome);
        }

        var stableDescriptors = host.DescriptorSets.Last().Descriptors.Where(descriptor =>
            descriptor.Role is not (CapabilityRole.Telemetry or CapabilityRole.FanMeasuredRpm)).ToArray();
        var before = stableDescriptors.Select(descriptor => host.CapabilityStates.Last(value =>
            value.CapabilityId == descriptor.CapabilityId && value.InstanceId == descriptor.InstanceId)).ToArray();

        await provider!.RefreshTelemetryAsync(CancellationToken.None);
        await provider.RefreshTelemetryAsync(CancellationToken.None);

        var after = stableDescriptors.Select(descriptor => host.CapabilityStates.Last(value =>
            value.CapabilityId == descriptor.CapabilityId && value.InstanceId == descriptor.InstanceId)).ToArray();
        Assert.Equal(before, after);
        var chargeAt = after.Single(value => value.CapabilityId == CapabilityIds.ChargeLimit).ObservedAt;
        await Task.Delay(1);
        Assert.Equal(CommandOutcome.Applied, (await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(80)), CancellationToken.None)).Outcome);
        Assert.True(host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.ChargeLimit).ObservedAt
                    > chargeAt);
    }

    [Fact]
    public async Task StartupReadsOnlyLiveTelemetryAndOffersControlsWithoutReadback()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        FakeMcuTransport mcu = new();
        await using var plugin = Engine(CreateServices(wmi, mcu: mcu));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.Equal(2, wmi.Reads);
        Assert.Empty(mcu.ReadAddresses);
        foreach (var descriptor in host.DescriptorSets.Last().Descriptors.Where(value => value.SupportsWrite))
        {
            var control = host.CapabilityStates.Last(value => value.CapabilityId == descriptor.CapabilityId
                                                              && value.InstanceId == descriptor.InstanceId);
            Assert.True(control.Available);
            Assert.Null(control.ObservedValue);
        }
    }

    [Fact]
    public async Task AcceptedChargeIntentNeedsNoPostWriteReadbackAndSurvivesSensorReadFailure()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        ClawDeviceEngine? provider = null;
        await using var plugin = new ServiceEngineFixture(context =>
            provider = new ClawDeviceEngine(context, CreateServices(wmi)));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var reads = wmi.Reads;
        wmi.GetterTimeout = true;

        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(80)), CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(reads, wmi.Reads);
        await provider!.RefreshAsync(CancellationToken.None);

        var charge = host.CapabilityStates.Last(value => value.CapabilityId == CapabilityIds.ChargeLimit);
        Assert.True(charge.Available);
        Assert.Equal(HardwareStateQuality.Observed, charge.Quality);
        Assert.Equal(80, charge.ObservedValue?.IntegerValue);
        Assert.Single(wmi.Writes);
    }

    [Fact]
    public async Task InsufficientWriteBudgetRejectsBeforeAnySetter()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20, Deadline = Deadline.After(TimeSpan.FromSeconds(1))
            },
            CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
        var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(journal.Entries<ClawRecoveryState>());
        _ = await plugin.StopAsync(new EngineStopContext(
            Deadline.After(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public void ExactBaseboardMatchesWithoutMarketingName()
    {
        var model = ClawModels.Find(ExactIdentity() with { SystemProduct = "localized marketing name" });
        Assert.Equal(ClawModels.Claw8A2Vm.DefinitionId, model?.DefinitionId);
    }

    [Theory]
    [InlineData("manufacturer")]
    [InlineData("baseboard")]
    public void DifferentIdentitySignalFailsClosed(string signal)
    {
        var identity = signal == "manufacturer"
            ? ExactIdentity() with { BaseboardManufacturer = "Other vendor" }
            : ExactIdentity() with { BaseboardProduct = "not-a-claw" };
        Assert.Null(ClawModels.Find(identity));
    }

    [Theory]
    [InlineData("MS-1T42", "1T42.1", "ms-1t42")]
    [InlineData("MS-1T52", "unknown", "ms-1t52")]
    public void BoardMatchIgnoresSku(string board, string sku, string definition)
    {
        Assert.Equal(definition,
            ClawModels.Find(ExactIdentity() with { BaseboardProduct = board, SystemSku = sku })?.DefinitionId);
    }

    [Fact]
    public void SystemManufacturerDoesNotDecide()
    {
        Assert.NotNull(ClawModels.Find(ExactIdentity() with { SystemManufacturer = "MSI" }));
    }

    [Fact]
    public async Task StartAsync_ControllerManagementOffIsAnIntentionalActiveState()
    {
        using TemporaryDirectory state = new();
        await using var plugin = Engine(CreateServices());
        TestPluginHostAdapter host = new(FixtureId);

        var result = await plugin.StartAsync(
            StartContext(host, state.Root),
            CancellationToken.None);

        Assert.Equal(EngineOperationalState.Active, result.State);
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
            {
                CapabilityId: CapabilityIds.Controller,
                Available: false,
                Reason.Code: CapabilityReasonCode.ResourceReleased
            });
    }

    [Fact]
    public async Task StartAsync_StreamsMotionForTheWholeCycle()
    {
        using TemporaryDirectory state = new();
        FakeMotionSource motion = new();
        await using var plugin = Engine(CreateServices(motion: motion));
        TestPluginHostAdapter host = new(FixtureId);

        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.True(motion.Started);
        Assert.Equal(1, motion.StartCount);
    }

    [Fact]
    public async Task StartAsync_ReusesTheCallerIdentityForEveryServiceInTheCycle()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new();
        await using var plugin = Engine(CreateServices(identity: identity));
        TestPluginHostAdapter host = new(FixtureId);

        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.Equal(0, identity.ReadCount);
        Assert.Equal(1, identity.AcPowerReads);
    }

    // The lifecycle order is safety-relevant: stop releases the controller and motion before the
    // power, fan and charge state is restored, and chord suppression needs the OEM source first.
    // Reordering either array must be a deliberate change to this test.
    [Fact]
    public async Task StartAsync_OrdersServicesForAcquisitionAndSuspension()
    {
        using TemporaryDirectory state = new();
        ClawDeviceEngine? provider = null;
        await using var plugin =
            new ServiceEngineFixture(context => provider = new ClawDeviceEngine(context, CreateServices()));
        TestPluginHostAdapter host = new(FixtureId);

        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.NotNull(provider);
        Assert.Equal(
            [
                "msi-oem-events",
                "msi-power",
                "msi-power.direct",
                "msi-charge-limit",
                "msi-fans",
                "msi-telemetry",
                "claw-lighting",
                "claw-motion",
                "physical-controller",
                "firmware-chord-suppressor"
            ],
            provider.Services.Select(service => service.ServiceId));
        Assert.Equal(
            ["msi-oem-events", "claw-motion", "physical-controller", "firmware-chord-suppressor"],
            provider.Services.Where(service => service.Suspendable).Select(service => service.ServiceId));
    }

    [Fact]
    public async Task StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces()
    {
        using TemporaryDirectory state = new();
        FakeOemEventSource oem = new();
        await using var plugin = Engine(CreateServices(oemEvents: oem));
        TestPluginHostAdapter host = new(FixtureId);

        var result = await plugin.StartAsync(
            StartContext(host, state.Root),
            CancellationToken.None);
        await oem.EmitAsync(0x58, DateTimeOffset.UnixEpoch);

        Assert.Equal(EngineOperationalState.Active, result.State);
        var descriptors = Assert.Single(host.DescriptorSets);

        // Offline publication for the host's full Device-page visual fixture. No live hardware is used.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "claw-ui-publication.json"),
            JsonSerializer.Serialize(new { Descriptors = descriptors, States = host.CapabilityStates }, IndentedJson));

        Assert.All(descriptors.Descriptors, descriptor => Assert.True(descriptor.Display.TryValidate(out _)));
        var sustained = Assert.Single(descriptors.Descriptors,
            descriptor => descriptor.CapabilityId == CapabilityIds.PowerSustained);
        Assert.Equal(CapabilityIds.PowerBoost, sustained.PairedPowerLimitId);
        Assert.True(PowerPairContract.IsValid(descriptors.Descriptors));
        // Ownership rows carry the labels every family publishes, never the raw choice ids.
        Assert.All(descriptors.Descriptors.Where(descriptor =>
                descriptor.CapabilityId is CapabilityIds.Controller or CapabilityIds.Motion),
            descriptor => Assert.Equal(["Device", "Plugin", "Unavailable"],
                descriptor.Choices!.Select(choice => choice.Display.CustomText)));
        Assert.True(DevicePowerPreset.TryValidate(descriptors.Descriptors, out var presetError), presetError);
        Assert.Equal([
            new DevicePowerPreset("super-battery", "Super Battery", 8, 9, DevicePowerMode.BetterBattery)
                { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)
                { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("extreme-performance", "Extreme Performance", 30, 31, DevicePowerMode.BestPerformance)
                { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("full-power", "Full Power", 37, 37, DevicePowerMode.BestPerformance)
                { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }
        ], sustained.PowerPresets);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor is
            {
                CapabilityId: CapabilityIds.ChargeLimit,
                Role: CapabilityRole.ChargeLimit,
                Persistence: CapabilityPersistence.DevicePersistent
            });
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor is { CapabilityId: CapabilityIds.LightingColor, InstanceId: CapabilityInstances.Buttons });
        Assert.Equal(descriptors.Descriptors.Count, host.CapabilityStates.Count);
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
                { CapabilityId: CapabilityIds.PowerSustained, Available: true, ObservedValue: null });
        Assert.Contains(host.CapabilityStates, capability =>
            capability is { CapabilityId: CapabilityIds.ChargeLimit, Available: true, ObservedValue: null });
        Assert.Contains(host.CapabilityStates, capability =>
            capability is { CapabilityId: CapabilityIds.Controller, Available: false });
        Assert.Equal(4, Assert.Single(host.OemControlSets).Count);
        var controlEvent = Assert.Single(host.OemEvents);
        Assert.Equal("oem2", controlEvent.ControlId);
        Assert.Equal(OemPressKind.Short, controlEvent.Press);
    }

    [Fact]
    public async Task StartAsync_PublicationFailure_RetractsEveryAcceptedSurface()
    {
        using TemporaryDirectory state = new();
        await using var plugin = Engine(CreateServices());
        ControllablePluginHostAdapter host = new(FixtureId)
        {
            FailNextNonEmptyOemPublication = true
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None));

        Assert.Equal(2, host.DescriptorSets.Count);
        Assert.NotEmpty(host.DescriptorSets[0].Descriptors);
        Assert.Empty(host.DescriptorSets[1].Descriptors);
        Assert.Equal(2, host.OemControlSets.Count);
        Assert.NotEmpty(host.OemControlSets[0]);
        Assert.Empty(host.OemControlSets[1]);
        Assert.Empty(Assert.Single(host.PhysicalDeviceSets));

        var diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal(nameof(DeviceServiceState.Idle), diagnostics["msiclaw-native"]);
        Assert.Empty((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None)).OutstandingEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerStop_CancelsAndUnblocksAnInFlightHostPublication(bool rearOemEvent)
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        ControllablePluginHostAdapter host = new(FixtureId)
        {
            BlockControllerSamples = !rearOemEvent,
            BlockOemEvents = rearOemEvent
        };
        var journal = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource(), ClawModels.Claw8A2Vm),
            host,
            journal,
            ClawModels.Claw8A2Vm,
            TestTiming.NoDelay,
            _ => { })
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(
                Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);
        var publication = source.EmitAsync(new CanonicalControllerSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = rearOemEvent ? CanonicalButtons.RearPaddle1 : CanonicalButtons.None
        }).AsTask();
        var blockedPublication = rearOemEvent ? host.OemEventEntered : host.ControllerSampleEntered;
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        await controller.ReleaseControllerAsync(
            Deadline.After(TimeSpan.FromSeconds(10)),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DeviceServiceState.Idle, controller.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await publication);
    }

    // The reader fails on a transport thread, outside any serialized turn, and the telemetry tick
    // publishes only the readings it samples. The controller row must change when the pad goes away
    // and again when it comes back, not at the next full refresh.
    [Fact]
    public async Task ControllerReaderFault_PublishesTheLostAndReturnedPadFromTheCallback()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource controller = new();
        ClawDeviceEngine? provider = null;
        await using var plugin = new ServiceEngineFixture(context =>
            provider = new ClawDeviceEngine(context, CreateServices(controller: controller)));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root) with { ControllerManagementEnabled = true },
            CancellationToken.None);
        var engine = provider!;
        Assert.Equal(SourceOwnership.Managed, ControllerRow(engine).ObservedValue?.ChoiceValue);

        controller.TriggerReaderFault(new IOException("Synthetic reader loss."));

        var lost = ControllerRow(engine);
        Assert.False(lost.Available);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, lost.Reason?.Code);
        Assert.Equal(SourceOwnership.Unavailable, lost.ObservedValue?.ChoiceValue);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!ControllerRow(engine).Available && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(ControllerRow(engine).Available);
        Assert.Equal(SourceOwnership.Managed, ControllerRow(engine).ObservedValue?.ChoiceValue);

        static CapabilityState ControllerRow(ClawDeviceEngine engine)
        {
            return engine.States.Single(value => value.CapabilityId == CapabilityIds.Controller);
        }
    }

    [Fact]
    public async Task StartAsync_PowerEntryWaitsWhileTheBiosVersionIsUnknown()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(ClawServiceIds.Power, "bios:E1T52IMS.114",
                ClawRecoveryState.Power(new PowerPair(30, 37, 0xC1)), CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        await using (var plugin = Engine(CreateServices(wmi)))
        {
            _ = await plugin.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root) with
            {
                Identity = ExactIdentity()
            }, CancellationToken.None);
            _ = await plugin.StopAsync(new EngineStopContext(Deadline.After(TimeSpan.FromSeconds(10))),
                CancellationToken.None);
        }

        // Neither the start nor the stop can compare the binding, so nothing is written and the entry waits.
        Assert.Empty(wmi.Writes);
        Assert.NotNull((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
            .EntryFor<ClawRecoveryState>(ClawServiceIds.Power));
    }

    // Kept, an entry that cannot be restored would hold the place of the next capture forever: the
    // journal hands it back instead of reading a fresh original, and release finds nothing to write.
    [Fact]
    public async Task StartAsync_IncompleteEntryIsRetiredWithoutAWriteSoTheNextCommandCaptures()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(ClawServiceIds.Power, "bios:E1T52IMS.114",
                new ClawRecoveryState { Kind = ClawRecoveryStateKind.Power, SustainedWatts = 30 },
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        _ = await plugin.StartAsync(StartContext(new TestPluginHostAdapter(FixtureId), state.Root),
            CancellationToken.None);

        Assert.Empty(wmi.Writes);
        Assert.Null((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
            .EntryFor<ClawRecoveryState>(ClawServiceIds.Power));
        _ = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 20
            }, CancellationToken.None);
        var captured = (await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
            .EntryFor<ClawRecoveryState>(ClawServiceIds.Power);
        Assert.NotNull(captured?.OriginalState.ToPower());
    }

    [Fact]
    public async Task ChordSuppressor_BackgroundFaultDegradesOnlyThatService()
    {
        FakeOemEventSource oemSource = new();
        ControllablePluginHostAdapter host = new(FixtureId);
        OemEventService oem = new(oemSource, host, new OemButtonLatch());
        _ = await oem.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(
                Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);
        FakeChordSuppressor hook = new();
        ChordSuppressorService suppressor = new(hook, oem);
        _ = await suppressor.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(
                Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);

        hook.TriggerFault(new IOException(new string('x', 1500) + "\nsecond line"));

        // A lost keyboard hook costs the chord, never the rest of the device.
        Assert.Equal(DeviceServiceState.Degraded, suppressor.State);
        Assert.Empty(host.Faults);
        Assert.DoesNotContain('\n', suppressor.Reason?.Detail ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteCommandAsync_PowerWritePublishesIntentAndStopRestoresCompactJournal()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var command = Command(
                CapabilityIds.PowerSustained,
                null,
                CapabilityValue.Integer(25)) with
            {
                PairedPowerLimitWatts = 37
            };

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(25, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        {
            var pending = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            var entry = Assert.Single(pending.Entries<ClawRecoveryState>());
            Assert.Equal(ClawServiceIds.Power, entry.ServiceId);
            Assert.NotNull(entry);
            Assert.Equal(new PowerPair(30, 37, 0xC1), entry.OriginalState.ToPower());
        }

        var stop = await plugin.StopAsync(
            new EngineStopContext(
                Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.Equal(EngineStopStatus.Clean, stop.Status);
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(
            [25, 30],
            wmi.Writes
                .Where(write => write.Method == "Set_Data"
                                && write.Package[0] == ClawHardwareFacts.PowerSustainedAddress)
                .Select(write => BinaryPrimitives.ReadInt32LittleEndian(write.Package.AsSpan(1, sizeof(int)))));
        var completed = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(completed.Entries<ClawRecoveryState>());
    }

    [Theory]
    [InlineData("comfort", 0xC0)]
    [InlineData("green", 0xC1)]
    [InlineData("eco", 0xC2)]
    [InlineData("user", 0xC3)]
    [InlineData("sport", 0xC4)]
    public async Task ScenarioSelectionPublishesIntentAndStopRestoresFirstOriginal(string scenario, int expected)
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, 0x81);
        wmi.AfterSetter = (method, package) =>
        {
            if (method != "Set_Data" || package[0] != ClawHardwareFacts.ScenarioAddress)
            {
                return;
            }

            wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 8);
            wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 9);
        };
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        Assert.Null(host.CapabilityStates.Last(s => s.CapabilityId == CapabilityIds.Scenario).ObservedValue);
        var result = await plugin.ExecuteCommandAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice(scenario)), CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(expected, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
        Assert.Null(host.CapabilityStates.Last(s => s.CapabilityId == CapabilityIds.PowerSustained).ObservedValue);
        var stop = await plugin.StopAsync(new EngineStopContext(
            Deadline.After(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Equal(EngineStopStatus.Clean, stop.Status);
        Assert.Equal(0x81, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(37, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedPostCommandPublicationHonorsTimeoutOrStopWithoutChangingAcceptedWrite(bool stop)
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        ControllablePluginHostAdapter host = new(FixtureId);
        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        wmi.AfterSetter = (method, package) =>
        {
            if (method == "Set_Data" && package[0] == ClawHardwareFacts.ScenarioAddress && package[1] == 0xC4)
            {
                host.CapabilityPublicationBlock = blocked;
            }
        };
        var command = Command(CapabilityIds.Scenario, null,
                CapabilityValue.Choice("sport")) with
            {
                Deadline = Deadline.After(TimeSpan.FromSeconds(10))
            };
        var applying = plugin.ExecuteCommandAsync(command, CancellationToken.None).AsTask();
        try
        {
            await host.CapabilityPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.CapabilityPublicationBlock = null;
            var stopping = stop
                ? plugin.StopAsync(new EngineStopContext(Deadline.After(TimeSpan.FromSeconds(5))),
                        CancellationToken.None)
                    .AsTask()
                : null;
            var result = await applying.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CommandOutcome.Applied, result.Outcome);
            if (stopping is not null)
            {
                Assert.Equal(EngineStopStatus.Clean, (await stopping).Status);
            }
        }
        finally
        {
            blocked.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_ChargeLimitPersistsAcrossPluginStop()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var command = Command(
            CapabilityIds.ChargeLimit,
            null,
            CapabilityValue.Integer(60));

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);
        var stop = await plugin.StopAsync(
            new EngineStopContext(
                Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(EngineStopStatus.Clean, stop.Status);
        // Bit 7 is Battery Master, which the limit needs to be enforced.
        Assert.Equal(0x80 | 60, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    // After BIOS E1T52IMS.114 the reference unit's charge-limit register read 0x80: Battery Master
    // flag set, percentage zero. That is a reset, not a fault: the capability must stay available
    // with an unknown observed value so the configured limit can be written back over it.
    [Fact]
    public async Task StartAsync_ChargeLimitResetByFirmware_StaysWritable()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ChargeLimitAddress, 0x80);
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);

        var start = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.ChargeLimit, null, CapabilityValue.Integer(80)),
            CancellationToken.None);

        Assert.Equal(EngineOperationalState.Active, start.State);
        Assert.Contains(host.CapabilityStates, capability =>
            capability is { CapabilityId: CapabilityIds.ChargeLimit, Available: true, ObservedValue: null });
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(0x80 | 80, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    [Fact]
    public async Task StopAsync_RestoresStateCapturedImmediatelyBeforeFirstMutation()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        // Another manager may legitimately change the resource after plugin acquisition but before
        // WSGM's first write. The command journal, not the stale acquisition observation, owns the
        // value that handoff must restore.
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 28);
        var command = Command(
                CapabilityIds.PowerSustained,
                null,
                CapabilityValue.Integer(25)) with
            {
                PairedPowerLimitWatts = 37
            };

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);
        var stop = await plugin.StopAsync(
            new EngineStopContext(
                Deadline.After(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(EngineStopStatus.Clean, stop.Status);
        Assert.Equal(28, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
    }

    [Fact]
    public async Task ApplyHaptics_FailedWriteDoesNotSuppressIdenticalRetry()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology(), FailNextRumble = true };
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource(), ClawModels.Claw8A2Vm),
            host,
            journal,
            ClawModels.Claw8A2Vm,
            TestTiming.NoDelay,
            _ => { })
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(
                Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);
        HapticOutputFrame frame = new()
        {
            LowFrequency = 0.5f,
            HighFrequency = 0.25f,
            Timestamp = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await controller.ApplyHapticsAsync(frame, CancellationToken.None));
        await controller.ApplyHapticsAsync(frame, CancellationToken.None);

        Assert.Equal(2, source.RumbleWriteAttempts);
    }

    [Fact]
    public async Task ReleaseController_SourceStopFailureStillEndsIdle()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology(), FailStop = true };
        TestPluginHostAdapter host = new(FixtureId);
        var journal = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource(), ClawModels.Claw8A2Vm),
            host,
            journal,
            ClawModels.Claw8A2Vm,
            TestTiming.NoDelay,
            _ => { })
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new DeviceCycleContext<ClawIdentityState>(
                Deadline.After(TimeSpan.FromSeconds(10)),
                FakeIdentityReader.CreateState()),
            CancellationToken.None);

        await controller.ReleaseControllerAsync(
            Deadline.After(TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        // Best effort, as HC's Close: a source that fails to stop does not keep the service down.
        Assert.Equal(DeviceServiceState.Idle, controller.State);
    }

    [Fact]
    public async Task BeginAsync_UnfinishedWrite_RetainsFirstOriginalAcrossReopen()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            var operation = await journal.BeginAsync(
                ClawServiceIds.Power,
                "ec:1T52EMS1.109;msi-acpi:8.0",
                ClawRecoveryState.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);

            Assert.True(Assert.IsType<DeviceRecoveryOperation<ClawRecoveryState>>(operation).Opened);
            Assert.Single(journal.Entries<ClawRecoveryState>());
        }

        var reopened = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        var entry = Assert.Single(reopened.Entries<ClawRecoveryState>());
        Assert.Equal(new PowerPair(30, 37, 0xC1), entry.OriginalState.ToPower());

        var existing = await reopened.BeginAsync(
            ClawServiceIds.Power,
            "ec:1T52EMS1.109;msi-acpi:8.0",
            ClawRecoveryState.Power(new PowerPair(25, 37, 0xC1)),
            CancellationToken.None);
        Assert.False(Assert.IsType<DeviceRecoveryOperation<ClawRecoveryState>>(existing).Opened);
        Assert.Equal(new PowerPair(30, 37, 0xC1),
            Assert.IsType<DeviceRecoveryOperation<ClawRecoveryState>>(existing).Entry.OriginalState.ToPower());
    }

    [Fact]
    public async Task StartAsync_OutstandingSharedPowerOriginal_RestoresBeforeNewOwnership()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(
                ClawServiceIds.Power,
                "bios:E1T52IMS.114",
                ClawRecoveryState.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 25);
        await using var plugin = Engine(CreateServices(wmi));
        TestPluginHostAdapter host = new(FixtureId);

        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
                { CapabilityId: CapabilityIds.PowerSustained, Available: true, ObservedValue: null });
        var reconciled = await DeviceRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(reconciled.Entries<ClawRecoveryState>());
    }

    [Fact]
    public async Task StartAsync_AFailedRestoreKeepsTheOriginalAndAllowsExplicitCommands()
    {
        using TemporaryDirectory state = new();
        {
            var journal = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            _ = await journal.BeginAsync(
                ClawServiceIds.Power,
                "bios:E1T52IMS.114",
                ClawRecoveryState.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new() { FailNextSetter = true };
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 25);
        await using (var firstCycle = Engine(CreateServices(wmi)))
        {
            TestPluginHostAdapter firstHost = new(FixtureId);
            _ = await firstCycle.StartAsync(
                StartContext(firstHost, state.Root),
                CancellationToken.None);
            var diagnostics = await firstCycle.GetDiagnosticsAsync(CancellationToken.None);

            Assert.Equal(nameof(DeviceServiceState.Owned), diagnostics["msiclaw-native"]);
            Assert.NotNull((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
                .EntryFor<ClawRecoveryState>(ClawServiceIds.Power));
            // Dispose only closes handles, as after an interrupted process lifetime. A clean stop would
            // attempt restoration again and retire the original after an accepted write.
        }

        var failed = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.NotNull(failed.EntryFor<ClawRecoveryState>(ClawServiceIds.Power));
        wmi.Writes.Clear();
        await using (var secondCycle = Engine(CreateServices(wmi)))
        {
            TestPluginHostAdapter secondHost = new(FixtureId + 1);
            _ = await secondCycle.StartAsync(
                StartContext(secondHost, state.Root),
                CancellationToken.None);
            var diagnostics = await secondCycle.GetDiagnosticsAsync(CancellationToken.None);

            // A new owner restores the retained original; it has no permanent failed-write lockout.
            Assert.Equal(3, wmi.Writes.Count);
            Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
            Assert.Equal(nameof(DeviceServiceState.Owned), diagnostics["msiclaw-native"]);
            Assert.Empty((await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
                .Entries<ClawRecoveryState>());
            var result = await secondCycle.ExecuteCommandAsync(
                Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(20)) with
                {
                    PairedPowerLimitWatts = 20
                }, CancellationToken.None);
            Assert.Equal(CommandOutcome.Applied, result.Outcome);

            // A subsequent explicit command captures the restored state for this ownership period.
            _ = await secondCycle.StopAsync(
                new EngineStopContext(
                    Deadline.After(TimeSpan.FromSeconds(10))),
                CancellationToken.None);
            Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
            var reconciled = await DeviceRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
            Assert.Empty(reconciled.Entries<ClawRecoveryState>());
        }
    }

    private static ServiceEngineFixture Engine(ClawHardwareServices services)
    {
        return new ServiceEngineFixture(context => new ClawDeviceEngine(context, services));
    }

    private static EngineStartContext StartContext(IDeviceSink host, string stateDirectory)
    {
        return new EngineStartContext
        {
            Host = host,
            Identity = ExactIdentity() with { BiosVersion = "E1T52IMS.114" },
            DeviceDefinitionId = ClawModels.Claw8A2Vm.DefinitionId,
            StateDirectory = stateDirectory,
            Journal = TestRecovery.Open(stateDirectory),
            ControllerManagementEnabled = false
        };
    }

    private static ClawHardwareServices CreateServices(
        FakeWmiTransport? wmi = null,
        FakeOemEventSource? oemEvents = null,
        FakeMcuTransport? mcu = null,
        FakeControllerSource? controller = null,
        FakeMotionSource? motion = null,
        FakeChordSuppressor? chordSuppressor = null,
        FakeIdentityReader? identity = null,
        TimeProvider? time = null)
    {
        return new ClawHardwareServices(
            wmi ?? new FakeWmiTransport(),
            oemEvents ?? new FakeOemEventSource(),
            mcu ?? new FakeMcuTransport(),
            controller ?? new FakeControllerSource(),
            motion ?? new FakeMotionSource(),
            chordSuppressor ?? new FakeChordSuppressor(),
            new OemButtonLatch(),
            TestTiming.NoDelay,
            (identity ?? new FakeIdentityReader()).ReadOnAcPower,
            time);
    }

    private static DeviceIdentitySnapshot ExactIdentity()
    {
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = ClawModels.Claw8A2Vm.BoardProduct,
            SystemSku = "1T52.1",
            UsbEndpoints =
            [
                new UsbEndpointObservation
                {
                    VendorId = ClawHardwareFacts.Hex(ClawHardwareFacts.UsbVendorId),
                    ProductId = ClawHardwareFacts.Hex(ClawHardwareFacts.XInputProductId),
                    DeviceRelease = "0229"
                }
            ]
        };
    }

    private static ControllerTopology DirectInputTopology()
    {
        return new ControllerTopology(
            ClawControllerMode.DirectInput,
            ClawHardwareFacts.DirectInputProductId,
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            [
                new PhysicalDeviceIdentity
                {
                    InstancePath = @"HID\VID_0DB0&PID_1902\TEST",
                    LocationPath = "PCIROOT(0)#USBROOT(0)#USB(2)",
                    VendorId = ClawHardwareFacts.Hex(ClawHardwareFacts.UsbVendorId),
                    ProductId = ClawHardwareFacts.Hex(ClawHardwareFacts.DirectInputProductId),
                    RequiresHiding = true
                }
            ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcPowerFailureKeepsAConservativePowerSource(bool comFailure)
    {
        // The shared probe the Claw and the Ally both read: a failed probe is battery.
        Assert.False(WindowsMachineIdentity.OnAcPower(() => throw (comFailure
            ? new COMException("test failure")
            : new UnauthorizedAccessException("test refusal"))));
    }
}
