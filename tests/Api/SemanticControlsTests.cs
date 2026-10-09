using LibHandheld.Contracts;
using N = LibHandheld.Internal.Plugin;
using NC = LibHandheld.Internal.Capabilities;
using NI = LibHandheld.Internal.Input;

namespace LibHandheld.Tests.Api;

public sealed class SemanticControlsTests
{
    [Fact]
    public async Task RepeatedStartCannotReplaceNativeOwnership()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await device.StartAsync(false));
        Assert.Equal(1, engine.Starts);
        Assert.Equal(1, device.CycleGeneration);
    }

    [Fact]
    public async Task ResumeRequiresSuspensionAndTerminalStopRequiresANewInstance()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await device.ResumeAsync(Deadline.Never));
        Assert.Equal(1, device.CycleGeneration);
        var first = await device.StopAsync(DeviceStopReason.Exiting, Deadline.Never);
        Assert.Same(first, await device.StopAsync(DeviceStopReason.Exiting, Deadline.Never));
        Assert.Equal(1, engine.Stops);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await device.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await device.ResumeAsync(Deadline.Never));
    }

    [Fact]
    public async Task UniformTdpRoutesByRoleAndWritesOneNativePair()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetTdpAsync(25);

        Assert.True(result.Applied);
        var command = Assert.Single(engine.Commands);
        Assert.Equal("vendor.private.sustained", command.CapabilityId);
        Assert.Equal(25, command.RequestedValue!.IntegerValue);
        Assert.Equal(25, command.PairedPowerLimitWatts);
        Assert.Equal(1, command.ExpectedCycleGeneration);
        Assert.Equal(1, command.ExpectedDescriptorGeneration);
    }

    [Fact]
    public async Task ExplicitPowerPairKeepsDistinctTargets()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 30));

        Assert.True(result.Applied);
        var command = Assert.Single(engine.Commands);
        Assert.Equal(20, command.RequestedValue!.IntegerValue);
        Assert.Equal(30, command.PairedPowerLimitWatts);
    }

    [Fact]
    public async Task EveryPowerBoundIsValidatedBeforeAnyWrite()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 99));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason!.Code);
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task UndeclaredFastLimitIsNotInventedFromSlowPower()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 30, 35));

        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason!.Code);
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task MissingPairAndInvertedRelationshipAreRejectedWithoutWrites()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.False((await device.SetPowerLimitsAsync(new PowerLimits(20))).Applied);
        Assert.False((await device.SetPowerLimitsAsync(new PowerLimits(30, 20))).Applied);
        Assert.False((await device.SetControlAsync(CapabilityRole.PowerSlowLimit,
            CapabilityValue.Integer(25))).Applied);
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public async Task PartialNativeOutcomesRemainVisibleAndStopFurtherDispatch()
    {
        var engine = UnpairedEngine();
        engine.Outcomes.Enqueue(NC.CommandOutcome.AppliedVerified);
        engine.Outcomes.Enqueue(NC.CommandOutcome.Indeterminate);
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 25, 30));

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Results[0].Result.Outcome);
        Assert.Equal(CommandOutcome.Indeterminate, result.Results[1].Result.Outcome);
        Assert.Equal(2, engine.Commands.Count);
    }

    [Fact]
    public async Task CancellationAfterOneWritePreservesItsOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = UnpairedEngine();
        engine.AfterCommand = () => cancellation.Cancel();
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 25, 30),
            cancellationToken: cancellation.Token);

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(CommandOutcome.AppliedVerified, Assert.Single(result.Results).Result.Outcome);
        Assert.Single(engine.Commands);
    }

    [Fact]
    public async Task NativeExceptionAfterDispatchRetainsEarlierResultsAndReportsUncertainty()
    {
        var engine = UnpairedEngine();
        engine.ThrowOnCommand = 2;
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 25, 30));

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Results[0].Result.Outcome);
        Assert.Equal(CommandOutcome.Indeterminate, result.Results[1].Result.Outcome);
        Assert.Equal(2, engine.Commands.Count);
    }

    [Fact]
    public async Task LogicalLightingZoneIsRequiredWhenThereAreTwo()
    {
        var engine = new FakeEngine
        {
            Descriptors =
            [
                Color("private.rgb.0", "left-ring"), Color("private.rgb.1", "right-ring")
            ]
        };
        await using var device = Create(engine);
        await device.StartAsync(false);

        var ambiguous = await device.SetLightingColorAsync(0x12AB34);
        var selected = await device.SetLightingColorAsync(0x12AB34, "right-ring");

        Assert.Equal(CommandOutcome.Rejected, ambiguous.Outcome);
        Assert.True(selected.Applied);
        var command = Assert.Single(engine.Commands);
        Assert.Equal("private.rgb.1", command.CapabilityId);
        Assert.Equal("right-ring", command.InstanceId);
        Assert.Equal(0x12AB34, command.RequestedValue!.ColorValue);
    }

    [Fact]
    public async Task ChargingDoesNotPretendAChargeCeilingImplementsBypass()
    {
        var engine = new FakeEngine
        {
            Descriptors = [Integer("private.charge", NC.CapabilityRole.ChargeLimit, 60, 100, NC.CapabilityUnit.Percent)]
        };
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Equal(CapabilityReasonCode.Unsupported, (await device.SetChargeBypassAsync(true)).Reason!.Code);
        Assert.True((await device.SetChargeLimitAsync(80)).Applied);
        Assert.Equal(80, device.Controls.ObservedChargeLimitPercent);
        Assert.Single(engine.Commands);
    }

    [Fact]
    public async Task SupersededDescriptorAndCyclePublicationsCannotReplaceCurrentState()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await device.SetTdpAsync(20);
        await engine.PublishStateAsync("vendor.private.sustained", 99, 0, 1);
        await engine.PublishStateAsync("vendor.private.sustained", 99, 1, 0);

        Assert.Equal(20, device.Controls.ObservedPowerLimits!.SustainedWatts);
        await engine.PublishDescriptorsAsync(2);
        Assert.Null(device.Controls.ObservedPowerLimits);
        await engine.PublishStateAsync("vendor.private.sustained", 99, 1, 1);
        Assert.Null(device.Controls.ObservedPowerLimits);
    }

    [Fact]
    public async Task ConvenienceLifecyclesOwnFreshCyclesAndRejectMixedAuthority()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        Assert.Equal(1, device.CycleGeneration);
        await device.SuspendAsync(Deadline.Never);
        await engine.PublishDescriptorsAsync(2);
        Assert.Empty(device.Controls.Controls);
        await device.ResumeAsync(Deadline.Never);
        Assert.Equal(2, device.CycleGeneration);
        Assert.NotEmpty(device.Controls.Controls);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await device.ResumeAsync(3, Deadline.Never));
    }

    [Fact]
    public async Task ConsumerCyclesMustIncreaseAndStoppedPublicationsAreIgnored()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(7, false);
        await device.SuspendAsync(Deadline.Never);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await device.ResumeAsync(7, Deadline.Never));
        await device.ResumeAsync(8, Deadline.Never);
        await device.StopAsync(DeviceStopReason.Exiting, Deadline.Never);
        await engine.PublishDescriptorsAsync(3);
        Assert.Empty(device.Controls.Controls);
    }

    [Fact]
    public void ExplicitUnknownModelDoesNotFallBackToAnotherFamily()
    {
        Assert.Throws<ArgumentException>(() => HandheldDevice.Create("unknown", "state"));
        Assert.Null(HandheldDevice.Detect(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "Unknown", SystemProduct = "Unknown"
        }));
    }

    private static HandheldDevice Create(FakeEngine engine)
    {
        return new HandheldDevice(new HandheldDefinition("fake", "fake", "Fake", "fake"), "fake-state", null, engine,
            () => new DeviceIdentitySnapshot());
    }

    private static FakeEngine PairedEngine()
    {
        return new FakeEngine
        {
            Descriptors =
            [
                Integer("vendor.private.sustained", NC.CapabilityRole.PowerSustainedLimit) with
                {
                    PairedPowerLimitId = "vendor.private.boost"
                },
                Integer("vendor.private.boost", NC.CapabilityRole.PowerSlowLimit)
            ]
        };
    }

    private static FakeEngine UnpairedEngine()
    {
        return new FakeEngine
        {
            Descriptors =
            [
                Integer("vendor.sustained", NC.CapabilityRole.PowerSustainedLimit),
                Integer("vendor.slow", NC.CapabilityRole.PowerSlowLimit),
                Integer("vendor.fast", NC.CapabilityRole.PowerFastLimit)
            ]
        };
    }

    private static NC.CapabilityDescriptor Integer(string id, NC.CapabilityRole role, int minimum = 5,
        int maximum = 40, NC.CapabilityUnit unit = NC.CapabilityUnit.Watt)
    {
        return new NC.CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = NC.CapabilityValueKind.Integer,
            Display = new NC.CapabilityDisplay { Key = NC.DisplayKey.Custom, CustomLabel = "Native control" },
            Minimum = minimum, Maximum = maximum, Step = 1, Unit = unit, SupportsWrite = true,
            Persistence = NC.CapabilityPersistence.Volatile
        };
    }

    private static NC.CapabilityDescriptor Color(string id, string instance)
    {
        return new NC.CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = NC.CapabilityRole.LightingZoneColor,
            ValueKind = NC.CapabilityValueKind.Color,
            Display = new NC.CapabilityDisplay { Key = NC.DisplayKey.Custom, CustomLabel = "Native zone" },
            SupportsWrite = true, Persistence = NC.CapabilityPersistence.Volatile
        };
    }

    private sealed class FakeEngine : N.IDeviceEngine
    {
        private long _cycle;
        private long _generation = 1;
        private N.IDeviceSink? _sink;
        public IReadOnlyList<NC.CapabilityDescriptor> Descriptors { get; init; } = [];
        public List<NC.CapabilityCommand> Commands { get; } = [];
        public Queue<NC.CommandOutcome> Outcomes { get; } = [];
        public Action? AfterCommand { get; set; }
        public int? ThrowOnCommand { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public string PackageId => "fake";

        public ValueTask<N.PluginDetectionResult> DetectAsync(N.PluginDetectionContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new N.PluginDetectionResult { Matched = true, DeviceDefinitionId = "fake" });
        }

        public async ValueTask<N.PluginStartResult> StartAsync(N.PluginStartContext context,
            CancellationToken cancellationToken)
        {
            Starts++;
            _sink = context.Host;
            _cycle = context.CycleGeneration;
            await PublishDescriptorsAsync(1);
            return new N.PluginStartResult { State = N.PluginOperationalState.Active };
        }

        public async ValueTask<N.PluginStartResult> ResumeAsync(N.PluginResumeContext context,
            CancellationToken cancellationToken)
        {
            _cycle = context.CycleGeneration;
            await PublishDescriptorsAsync(1);
            return new N.PluginStartResult { State = N.PluginOperationalState.Active };
        }

        public async ValueTask<NC.CapabilityCommandResult> ExecuteCommandAsync(NC.CapabilityCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (Commands.Count == ThrowOnCommand)
            {
                throw new IOException("A native write may have completed.");
            }

            var outcome = Outcomes.TryDequeue(out var next) ? next : NC.CommandOutcome.AppliedVerified;
            if (outcome is NC.CommandOutcome.AppliedVerified or NC.CommandOutcome.AppliedUnverified)
            {
                await _sink!.PublishCapabilityStateAsync(new NC.CapabilityState
                {
                    CapabilityId = command.CapabilityId, InstanceId = command.InstanceId, Available = true,
                    ObservedValue = command.RequestedValue, DescriptorGeneration = _generation,
                    CycleGeneration = _cycle, Quality = NC.HardwareStateQuality.Observed,
                    ObservedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            AfterCommand?.Invoke();
            return new NC.CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = outcome, CompletedAt = DateTimeOffset.UtcNow,
                ReadbackValue = outcome == NC.CommandOutcome.AppliedVerified ? command.RequestedValue : null
            };
        }

        public ValueTask SuspendAsync(N.PluginQuiesceContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetControllerManagementAsync(N.PluginControllerManagementContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseControllerAsync(N.PluginControllerReleaseContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyHapticOutputAsync(NI.HapticOutputFrame frame, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<N.PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new N.PluginDiagnostics());
        }

        public ValueTask<N.PluginStopResult> StopAsync(N.PluginStopContext context, CancellationToken cancellationToken)
        {
            Stops++;
            return ValueTask.FromResult(new N.PluginStopResult { Status = N.PluginStopStatus.Clean });
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishDescriptorsAsync(long generation)
        {
            _generation = generation;
            return _sink!.PublishDescriptorsAsync(new NC.CapabilityDescriptorSet
            {
                Generation = generation, CycleGeneration = _cycle, Descriptors = Descriptors
            }, CancellationToken.None);
        }

        public ValueTask PublishStateAsync(string id, int value, long generation, long cycle)
        {
            return _sink!.PublishCapabilityStateAsync(new NC.CapabilityState
            {
                CapabilityId = id, Available = true, ObservedValue = NC.CapabilityValue.Integer(value),
                DescriptorGeneration = generation, CycleGeneration = cycle,
                Quality = NC.HardwareStateQuality.Observed, ObservedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }
}
