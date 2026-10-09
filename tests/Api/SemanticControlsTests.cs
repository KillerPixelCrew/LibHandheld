using N = LibHandheld.Internal.Runtime;

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
    }

    [Fact]
    public async Task ResumeRequiresSuspensionAndTerminalStopRequiresANewInstance()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await device.ResumeAsync(Deadline.Never));
        var first = await device.StopAsync(Deadline.Never);
        Assert.Same(first, await device.StopAsync(Deadline.Never));
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
        engine.Outcomes.Enqueue(CommandOutcome.Applied);
        engine.Outcomes.Enqueue(CommandOutcome.Indeterminate);
        await using var device = Create(engine);
        await device.StartAsync(false);

        var result = await device.SetPowerLimitsAsync(new PowerLimits(20, 25, 30));

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(CommandOutcome.Applied, result.Results[0].Result.Outcome);
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
        Assert.Equal(CommandOutcome.Applied, Assert.Single(result.Results).Result.Outcome);
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
        Assert.Equal(CommandOutcome.Applied, result.Results[0].Result.Outcome);
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
            Descriptors = [Integer("private.charge", CapabilityRole.ChargeLimit, 60, 100, CapabilityUnit.Percent)]
        };
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Equal(CapabilityReasonCode.Unsupported, (await device.SetChargeBypassAsync(true)).Reason!.Code);
        Assert.True((await device.SetChargeLimitAsync(80)).Applied);
        Assert.Equal(80, device.Controls.ObservedChargeLimitPercent);
        Assert.Single(engine.Commands);
    }

    [Fact]
    public async Task UnknownAndInactivePublicationsCannotReplaceCurrentState()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await device.SetTdpAsync(20);
        await engine.PublishStateAsync("unknown.control", 99, 0, 0);
        Assert.Equal(20, device.Controls.ObservedPowerLimits!.SustainedWatts);
        await device.SuspendAsync(Deadline.Never);
        await engine.PublishStateAsync("vendor.private.sustained", 99, 0, 0);
        await engine.PublishDescriptorsAsync(2);
        Assert.Empty(device.Controls.Controls);
    }

    [Fact]
    public async Task ResumeReplacesControlsAfterSuspension()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await device.SuspendAsync(Deadline.Never);
        await engine.PublishDescriptorsAsync(2);
        Assert.Empty(device.Controls.Controls);
        await device.ResumeAsync(Deadline.Never);
        Assert.NotEmpty(device.Controls.Controls);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await device.ResumeAsync(Deadline.Never));
    }

    [Fact]
    public async Task StoppedPublicationsAreIgnoredAndCommandsAreRejected()
    {
        var engine = PairedEngine();
        await using var device = Create(engine);
        await device.StartAsync(false);
        await device.StopAsync(Deadline.Never);
        await engine.PublishDescriptorsAsync(3);
        Assert.Empty(device.Controls.Controls);
        Assert.Equal(CommandOutcome.Rejected, (await device.SetTdpAsync(20)).Outcome);
        Assert.Empty(engine.Commands);
    }

    [Fact]
    public void ExplicitUnknownModelDoesNotFallBackToAnotherFamily()
    {
        Assert.Throws<ArgumentException>(() =>
            HandheldDevice.Create(new HandheldDefinition("unknown", "unknown", "Unknown", "unknown"),
                new DeviceIdentitySnapshot(), "state"));
        Assert.Null(HandheldDevice.Detect(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "Unknown", SystemProduct = "Unknown"
        }));
    }

    private static HandheldDevice Create(FakeEngine engine)
    {
        return new HandheldDevice(new HandheldDefinition("fake", "fake", "Fake", "fake"), new DeviceIdentitySnapshot(),
            "fake-state", null, engine);
    }

    private static FakeEngine PairedEngine()
    {
        return new FakeEngine
        {
            Descriptors =
            [
                Integer("vendor.private.sustained", CapabilityRole.PowerSustainedLimit) with
                {
                    PairedPowerLimitId = "vendor.private.boost"
                },
                Integer("vendor.private.boost", CapabilityRole.PowerSlowLimit)
            ]
        };
    }

    private static FakeEngine UnpairedEngine()
    {
        return new FakeEngine
        {
            Descriptors =
            [
                Integer("vendor.sustained", CapabilityRole.PowerSustainedLimit),
                Integer("vendor.slow", CapabilityRole.PowerSlowLimit),
                Integer("vendor.fast", CapabilityRole.PowerFastLimit)
            ]
        };
    }

    private static CapabilityDescriptor Integer(string id, CapabilityRole role, int minimum = 5,
        int maximum = 40, CapabilityUnit unit = CapabilityUnit.Watt)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Native control" },
            Minimum = minimum, Maximum = maximum, Step = 1, Unit = unit, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Color(string id, string instance)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Native zone" },
            SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        };
    }

    private sealed class FakeEngine : N.NativeEngine
    {
        private N.IDeviceSink? _sink;
        public IReadOnlyList<CapabilityDescriptor> Descriptors { get; init; } = [];
        public List<CapabilityCommand> Commands { get; } = [];
        public Queue<CommandOutcome> Outcomes { get; } = [];
        public Action? AfterCommand { get; set; }
        public int? ThrowOnCommand { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }


        public async ValueTask<N.EngineStartResult> StartAsync(N.EngineStartContext context,
            CancellationToken cancellationToken)
        {
            Starts++;
            _sink = context.Host;
            await PublishDescriptorsAsync(1);
            return new N.EngineStartResult { State = N.EngineOperationalState.Active };
        }

        public async ValueTask<N.EngineStartResult> ResumeAsync(N.EngineResumeContext context,
            CancellationToken cancellationToken)
        {
            await PublishDescriptorsAsync(1);
            return new N.EngineStartResult { State = N.EngineOperationalState.Active };
        }

        public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (Commands.Count == ThrowOnCommand)
            {
                throw new IOException("A native write may have completed.");
            }

            var outcome = Outcomes.TryDequeue(out var next) ? next : CommandOutcome.Applied;
            if (outcome is CommandOutcome.Applied)
            {
                await _sink!.PublishCapabilityStateAsync(new CapabilityState
                {
                    CapabilityId = command.CapabilityId, InstanceId = command.InstanceId, Available = true,
                    ObservedValue = command.RequestedValue, Quality = HardwareStateQuality.Observed,
                    ObservedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            AfterCommand?.Invoke();
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = outcome, CompletedAt = DateTimeOffset.UtcNow
            };
        }

        public ValueTask SuspendAsync(N.EngineQuiesceContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetControllerManagementAsync(N.EngineControllerManagementContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseControllerAsync(N.EngineControllerReleaseContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        public ValueTask<N.EngineStopResult> StopAsync(N.EngineStopContext context, CancellationToken cancellationToken)
        {
            Stops++;
            return ValueTask.FromResult(new N.EngineStopResult { Status = N.EngineStopStatus.Clean });
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishDescriptorsAsync(long generation)
        {
            return _sink!.PublishDescriptorsAsync(new CapabilityDescriptorSet
            {
                Descriptors = Descriptors
            }, CancellationToken.None);
        }

        public ValueTask PublishStateAsync(string id, int value, long generation, long cycle)
        {
            return _sink!.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = id, Available = true, ObservedValue = CapabilityValue.Integer(value),
                Quality = HardwareStateQuality.Observed, ObservedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }
}
