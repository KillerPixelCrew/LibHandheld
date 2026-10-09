using N = LibHandheld.Internal.Runtime;

namespace LibHandheld.Tests.Api;

public sealed class ChargingSemanticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BooleanProtectionUsesItsDeclaredShape(bool enabled)
    {
        var engine = Engine(CapabilityRole.ChargeProtectionMode, CapabilityValueKind.Boolean);
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Null(device.Controls.ObservedChargeProtectionEnabled);
        Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeProtectionModeAsync("conservation")).Outcome);
        Assert.Empty(engine.Commands);
        Assert.True((await device.SetChargeProtectionAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(CapabilityValueKind.Boolean, command.RequestedValue!.Kind);
        Assert.Equal(enabled, command.RequestedValue.BooleanValue);
        Assert.Equal(enabled, device.Controls.ObservedChargeProtectionEnabled);
        Assert.Null(device.Controls.ObservedChargeProtectionMode);
    }

    [Fact]
    public async Task ChoiceProtectionRemainsExplicitAndBooleanProtectionDoesNotGuessAnOption()
    {
        var engine = Engine(CapabilityRole.ChargeProtectionMode, CapabilityValueKind.Choice,
            "normal", "conservation");
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeProtectionAsync(true)).Outcome);
        Assert.Empty(engine.Commands);
        Assert.True((await device.SetChargeProtectionModeAsync("conservation")).Applied);

        Assert.Equal("conservation", Assert.Single(engine.Commands).RequestedValue!.ChoiceValue);
        Assert.Equal("conservation", device.Controls.ObservedChargeProtectionMode);
        Assert.Null(device.Controls.ObservedChargeProtectionEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BooleanBypassUsesItsDeclaredShape(bool enabled)
    {
        var engine = Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Boolean);
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeBypassModeAsync("always")).Outcome);
        Assert.Empty(engine.Commands);
        Assert.True((await device.SetChargeBypassAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(CapabilityValueKind.Boolean, command.RequestedValue!.Kind);
        Assert.Equal(enabled, command.RequestedValue.BooleanValue);
        Assert.Equal(enabled, device.Controls.ObservedChargeBypassEnabled);
        Assert.Null(device.Controls.ObservedChargeBypassMode);
    }

    [Theory]
    [InlineData("disabled", false)]
    [InlineData("until-sleep", true)]
    [InlineData("always", true)]
    public async Task ChoiceBypassRetainsTheExactNativeMode(string mode, bool enabled)
    {
        var engine = Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always");
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.True((await device.SetChargeBypassModeAsync(mode)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(CapabilityValueKind.Choice, command.RequestedValue!.Kind);
        Assert.Equal(mode, command.RequestedValue.ChoiceValue);
        Assert.Equal(mode, device.Controls.ObservedChargeBypassMode);
        Assert.Equal(enabled, device.Controls.ObservedChargeBypassEnabled);
    }

    [Theory]
    [InlineData(true, "always")]
    [InlineData(false, "disabled")]
    public async Task BooleanBypassSelectsOnlyDeclaredAlwaysAndDisabledChoices(bool enabled, string mode)
    {
        var engine = Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always");
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.True((await device.SetChargeBypassAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(CapabilityValueKind.Choice, command.RequestedValue!.Kind);
        Assert.Equal(mode, command.RequestedValue.ChoiceValue);
        Assert.Equal(mode, device.Controls.ObservedChargeBypassMode);
    }

    [Fact]
    public async Task UnsupportedBypassShapesAndUndeclaredModesNeverDispatch()
    {
        var engines = new[]
        {
            Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Integer),
            Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Choice, "off", "on"),
            Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Choice, "disabled", "until-sleep"),
            Engine(CapabilityRole.ChargeLimit, CapabilityValueKind.Integer)
        };
        foreach (var engine in engines)
        {
            await using var device = Create(engine);
            await device.StartAsync(false);

            Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeBypassAsync(true)).Outcome);
            Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeBypassAsync(false)).Outcome);
            Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeBypassModeAsync("invented")).Outcome);
            Assert.Empty(engine.Commands);
        }
    }

    [Fact]
    public async Task UnknownChoiceObservationHasNoInventedBooleanMeaning()
    {
        var engine = Engine(CapabilityRole.ChargeBypass, CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always", "future-mode");
        await using var device = Create(engine);
        await device.StartAsync(false);
        await engine.PublishStateAsync(new CapabilityValue
        {
            Kind = CapabilityValueKind.Choice, ChoiceValue = "future-mode", BooleanValue = false
        });

        Assert.Equal("future-mode", device.Controls.ObservedChargeBypassMode);
        Assert.Null(device.Controls.ObservedChargeBypassEnabled);
        await engine.PublishStateAsync(CapabilityValue.Choice("always"), false);
        Assert.Null(device.Controls.ObservedChargeBypassMode);
        Assert.Null(device.Controls.ObservedChargeBypassEnabled);
        Assert.Empty(engine.Commands);
    }

    private static HandheldDevice Create(FakeEngine engine)
    {
        return new HandheldDevice(new HandheldDefinition("fake", "fake", "Fake", "fake"), new DeviceIdentitySnapshot(),
            "fake-state", null, engine);
    }

    private static FakeEngine Engine(CapabilityRole role, CapabilityValueKind kind, params string[] choices)
    {
        return new FakeEngine
        {
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = "private.charging.control", Role = role, ValueKind = kind,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Charging control" },
                Choices = choices.Select(value => new CapabilityChoice(value,
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = value })).ToArray(),
                Minimum = kind == CapabilityValueKind.Integer ? 0 : null,
                Maximum = kind == CapabilityValueKind.Integer ? 100 : null,
                Step = kind == CapabilityValueKind.Integer ? 1 : null,
                SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
            }
        };
    }

    private sealed class FakeEngine : N.NativeEngine
    {
        private N.IDeviceSink? _sink;
        public required CapabilityDescriptor Descriptor { get; init; }
        public List<CapabilityCommand> Commands { get; } = [];


        public async ValueTask<N.EngineStartResult> StartAsync(N.EngineStartContext context,
            CancellationToken cancellationToken)
        {
            _sink = context.Host;
            await _sink.PublishDescriptorsAsync(new CapabilityDescriptorSet
            {
                Descriptors = [Descriptor]
            }, cancellationToken);
            return new N.EngineStartResult { State = N.EngineOperationalState.Active };
        }

        public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            await PublishStateAsync(command.RequestedValue!);
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = CommandOutcome.Applied,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        public ValueTask<N.EngineStartResult> ResumeAsync(N.EngineResumeContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new N.EngineStartResult
            {
                State = N.EngineOperationalState.Active
            });
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
            return ValueTask.FromResult(new N.EngineStopResult { Status = N.EngineStopStatus.Clean });
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishStateAsync(CapabilityValue value, bool available = true)
        {
            return _sink!.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = Descriptor.CapabilityId, Available = available, ObservedValue = value,
                Quality = HardwareStateQuality.Observed,
                ObservedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }
}
