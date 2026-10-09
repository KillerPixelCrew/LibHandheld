using LibHandheld.Contracts;
using N = LibHandheld.Internal.Plugin;
using NC = LibHandheld.Internal.Capabilities;
using NI = LibHandheld.Internal.Input;

namespace LibHandheld.Tests.Api;

public sealed class ChargingSemanticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BooleanProtectionUsesItsDeclaredShape(bool enabled)
    {
        var engine = Engine(NC.CapabilityRole.ChargeProtectionMode, NC.CapabilityValueKind.Boolean);
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Null(device.Controls.ObservedChargeProtectionEnabled);
        Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeProtectionModeAsync("conservation")).Outcome);
        Assert.Empty(engine.Commands);
        Assert.True((await device.SetChargeProtectionAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(NC.CapabilityValueKind.Boolean, command.RequestedValue!.Kind);
        Assert.Equal(enabled, command.RequestedValue.BooleanValue);
        Assert.Equal(enabled, device.Controls.ObservedChargeProtectionEnabled);
        Assert.Null(device.Controls.ObservedChargeProtectionMode);
    }

    [Fact]
    public async Task ChoiceProtectionRemainsExplicitAndBooleanProtectionDoesNotGuessAnOption()
    {
        var engine = Engine(NC.CapabilityRole.ChargeProtectionMode, NC.CapabilityValueKind.Choice,
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
        var engine = Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Boolean);
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.Equal(CommandOutcome.Rejected, (await device.SetChargeBypassModeAsync("always")).Outcome);
        Assert.Empty(engine.Commands);
        Assert.True((await device.SetChargeBypassAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(NC.CapabilityValueKind.Boolean, command.RequestedValue!.Kind);
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
        var engine = Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always");
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.True((await device.SetChargeBypassModeAsync(mode)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(NC.CapabilityValueKind.Choice, command.RequestedValue!.Kind);
        Assert.Equal(mode, command.RequestedValue.ChoiceValue);
        Assert.Equal(mode, device.Controls.ObservedChargeBypassMode);
        Assert.Equal(enabled, device.Controls.ObservedChargeBypassEnabled);
    }

    [Theory]
    [InlineData(true, "always")]
    [InlineData(false, "disabled")]
    public async Task BooleanBypassSelectsOnlyDeclaredAlwaysAndDisabledChoices(bool enabled, string mode)
    {
        var engine = Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always");
        await using var device = Create(engine);
        await device.StartAsync(false);

        Assert.True((await device.SetChargeBypassAsync(enabled)).Applied);

        var command = Assert.Single(engine.Commands);
        Assert.Equal(NC.CapabilityValueKind.Choice, command.RequestedValue!.Kind);
        Assert.Equal(mode, command.RequestedValue.ChoiceValue);
        Assert.Equal(mode, device.Controls.ObservedChargeBypassMode);
    }

    [Fact]
    public async Task UnsupportedBypassShapesAndUndeclaredModesNeverDispatch()
    {
        var engines = new[]
        {
            Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Integer),
            Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Choice, "off", "on"),
            Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Choice, "disabled", "until-sleep"),
            Engine(NC.CapabilityRole.ChargeLimit, NC.CapabilityValueKind.Integer)
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
        var engine = Engine(NC.CapabilityRole.ChargeBypass, NC.CapabilityValueKind.Choice,
            "disabled", "until-sleep", "always", "future-mode");
        await using var device = Create(engine);
        await device.StartAsync(false);
        await engine.PublishStateAsync(new NC.CapabilityValue
        {
            Kind = NC.CapabilityValueKind.Choice, ChoiceValue = "future-mode", BooleanValue = false
        });

        Assert.Equal("future-mode", device.Controls.ObservedChargeBypassMode);
        Assert.Null(device.Controls.ObservedChargeBypassEnabled);
        await engine.PublishStateAsync(NC.CapabilityValue.Choice("always"), false);
        Assert.Null(device.Controls.ObservedChargeBypassMode);
        Assert.Null(device.Controls.ObservedChargeBypassEnabled);
        Assert.Empty(engine.Commands);
    }

    private static HandheldDevice Create(FakeEngine engine)
    {
        return new HandheldDevice(new HandheldDefinition("fake", "fake", "Fake", "fake"), "fake-state", null,
            engine, () => new DeviceIdentitySnapshot());
    }

    private static FakeEngine Engine(NC.CapabilityRole role, NC.CapabilityValueKind kind, params string[] choices)
    {
        return new FakeEngine
        {
            Descriptor = new NC.CapabilityDescriptor
            {
                CapabilityId = "private.charging.control", Role = role, ValueKind = kind,
                Display = new NC.CapabilityDisplay { Key = NC.DisplayKey.Custom, CustomLabel = "Charging control" },
                Choices = choices.Select(value => new NC.CapabilityChoice(value,
                    new NC.CapabilityDisplay { Key = NC.DisplayKey.Custom, CustomLabel = value })).ToArray(),
                Minimum = kind == NC.CapabilityValueKind.Integer ? 0 : null,
                Maximum = kind == NC.CapabilityValueKind.Integer ? 100 : null,
                Step = kind == NC.CapabilityValueKind.Integer ? 1 : null,
                SupportsRead = true, SupportsWrite = true, Persistence = NC.CapabilityPersistence.DevicePersistent
            }
        };
    }

    private sealed class FakeEngine : N.IDeviceEngine
    {
        private long _cycle;
        private N.IDeviceSink? _sink;
        public required NC.CapabilityDescriptor Descriptor { get; init; }
        public List<NC.CapabilityCommand> Commands { get; } = [];
        public string PackageId => "fake";

        public ValueTask<N.PluginDetectionResult> DetectAsync(N.PluginDetectionContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new N.PluginDetectionResult
            {
                Matched = true, DeviceDefinitionId = "fake"
            });
        }

        public async ValueTask<N.PluginStartResult> StartAsync(N.PluginStartContext context,
            CancellationToken cancellationToken)
        {
            _sink = context.Host;
            _cycle = context.CycleGeneration;
            await _sink.PublishDescriptorsAsync(new NC.CapabilityDescriptorSet
            {
                Generation = 1, CycleGeneration = _cycle, Descriptors = [Descriptor]
            }, cancellationToken);
            return new N.PluginStartResult { State = N.PluginOperationalState.Active };
        }

        public async ValueTask<NC.CapabilityCommandResult> ExecuteCommandAsync(NC.CapabilityCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            await PublishStateAsync(command.RequestedValue!);
            return new NC.CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = NC.CommandOutcome.AppliedVerified,
                CompletedAt = DateTimeOffset.UtcNow, ReadbackValue = command.RequestedValue
            };
        }

        public ValueTask<N.PluginStartResult> ResumeAsync(N.PluginResumeContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new N.PluginStartResult
            {
                State = N.PluginOperationalState.Active
            });
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
            return ValueTask.FromResult(new N.PluginStopResult { Status = N.PluginStopStatus.Clean });
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishStateAsync(NC.CapabilityValue value, bool available = true)
        {
            return _sink!.PublishCapabilityStateAsync(new NC.CapabilityState
            {
                CapabilityId = Descriptor.CapabilityId, Available = available, ObservedValue = value,
                DescriptorGeneration = 1, CycleGeneration = _cycle, Quality = NC.HardwareStateQuality.Observed,
                ObservedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }
}
