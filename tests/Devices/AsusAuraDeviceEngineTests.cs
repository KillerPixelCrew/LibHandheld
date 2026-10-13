using LibHandheld.Internal.Runtime;

namespace LibHandheld.Tests.Devices;

[Collection("plugin-trace")]
public sealed class HandheldObserverTests
{
    [Fact]
    public async Task InputOemAndHapticsUseTheDirectNativeBoundary()
    {
        var native = new FakeNative();
        var observer = new Observer();
        await using var device = Create(native, observer);
        await device.StartAsync();
        var sample = new CanonicalControllerSample { Timestamp = DateTimeOffset.UtcNow, Buttons = CanonicalButtons.A };
        var edge = new OemControlEvent("rear", OemPressKind.Short, sample.Timestamp, "one");
        await native.Sink!.PublishControllerSampleAsync(sample, CancellationToken.None);
        await native.Sink.PublishOemEventAsync(edge, CancellationToken.None);
        Assert.Equal(sample, Assert.Single(observer.Samples));
        Assert.Equal(edge, Assert.Single(observer.Events));
        var rumble = new HapticOutputFrame { Timestamp = sample.Timestamp, LowFrequency = 0.5f };
        await device.ApplyHapticsAsync(rumble);
        Assert.Equal(rumble, Assert.Single(native.Output));
    }

    [Fact]
    public async Task SuspendDropsInputAndResumeRestoresTheSameNativeOwner()
    {
        var native = new FakeNative();
        var observer = new Observer();
        await using var device = Create(native, observer);
        await device.StartAsync();
        await device.SuspendAsync(Deadline.Never);
        await native.Sink!.PublishControllerSampleAsync(
            new CanonicalControllerSample { Timestamp = DateTimeOffset.UtcNow }, CancellationToken.None);
        Assert.Empty(observer.Samples);
        await device.ResumeAsync(Deadline.Never);
        await native.Sink.PublishControllerSampleAsync(
            new CanonicalControllerSample { Timestamp = DateTimeOffset.UtcNow }, CancellationToken.None);
        Assert.Single(observer.Samples);
        Assert.Equal(1, native.Starts);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("stop")]
    [InlineData("fault")]
    public async Task ClosingInputAdmissionStillForwardsOemReleases(string transition)
    {
        var native = new FakeNative();
        var observer = new Observer();
        await using var device = Create(native, observer);
        await device.StartAsync();
        var press = new OemControlEvent("rear", OemPressKind.Short, DateTimeOffset.UtcNow, "held");
        await native.Sink!.PublishOemEventAsync(press, CancellationToken.None);
        switch (transition)
        {
            case "suspend":
                await device.SuspendAsync(Deadline.Never);
                break;
            case "stop":
                await device.StopAsync(Deadline.Never);
                break;
            default:
                native.Sink.ReportFault("input", "reader failed");
                break;
        }

        await native.Sink.PublishOemEventAsync(press with { DeduplicationId = "new" }, CancellationToken.None);
        var release = press with { Edge = OemControlEdge.Released };
        await native.Sink.PublishOemEventAsync(release, CancellationToken.None);
        Assert.Equal(new[] { press, release }, observer.Events);

        await device.DisposeAsync();
        await native.Sink.PublishOemEventAsync(release, CancellationToken.None);
        Assert.Equal(2, observer.Events.Count);
    }

    [Fact]
    public async Task ReplacementDescriptorsRemoveRetiredControlState()
    {
        var native = new FakeNative();
        await using var device = Create(native, new Observer());
        await device.StartAsync();
        await native.Sink!.PublishDescriptorsAsync(new CapabilityDescriptorSet { Descriptors = [Descriptor()] },
            CancellationToken.None);
        await native.Sink.PublishCapabilityStateAsync(State(20), CancellationToken.None);
        Assert.Single(device.Controls.Controls);
        await native.Sink.PublishDescriptorsAsync(new CapabilityDescriptorSet(), CancellationToken.None);
        await native.Sink.PublishCapabilityStateAsync(State(99), CancellationToken.None);
        Assert.Empty(device.Controls.Controls);
    }

    [Fact]
    public async Task NativeFaultDisablesNewWritesAndNotifiesTheObserver()
    {
        var native = new FakeNative();
        var observer = new Observer();
        await using var device = Create(native, observer);
        await device.StartAsync();
        native.Sink!.ReportFault("input", "reader failed");
        var result = await device.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "native.power", RequestedValue = CapabilityValue.Integer(20),
            Deadline = Deadline.Never
        });
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(native.Commands);
        Assert.Equal(("input", "reader failed"), Assert.Single(observer.Faults));
    }

    private static HandheldDevice Create(FakeNative native, Observer observer)
    {
        return new HandheldDevice(new HandheldDefinition("fake", "fake", "Fake", "fake"), new DeviceIdentitySnapshot(),
                "unused-state",
                null, native)
            { Observer = observer };
    }

    private static CapabilityState State(int watts)
    {
        return new CapabilityState
        {
            CapabilityId = "native.power", Available = true, ObservedValue = CapabilityValue.Integer(watts),
            Quality = HardwareStateQuality.Observed
        };
    }

    private static CapabilityDescriptor Descriptor()
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "native.power", Role = CapabilityRole.PowerSustainedLimit,
            ValueKind = CapabilityValueKind.Integer, Display = new CapabilityDisplay { Key = DisplayKey.Tdp },
            Minimum = 5, Maximum = 30, Step = 1, Unit = CapabilityUnit.Watt, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private sealed class FakeNative : NativeEngine
    {
        internal IDeviceSink? Sink { get; private set; }
        internal int Starts { get; private set; }
        internal List<HapticOutputFrame> Output { get; } = [];
        internal List<CapabilityCommand> Commands { get; } = [];

        public ValueTask<EngineStartResult> StartAsync(EngineStartContext context, CancellationToken token)
        {
            Starts++;
            Sink = context.Host;
            return ValueTask.FromResult(new EngineStartResult { State = EngineOperationalState.Active });
        }

        public ValueTask<EngineStartResult> ResumeAsync(EngineResumeContext context, CancellationToken token)
        {
            return ValueTask.FromResult(new EngineStartResult { State = EngineOperationalState.Active });
        }

        public ValueTask SuspendAsync(EngineQuiesceContext context, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<EngineStopResult> StopAsync(EngineStopContext context, CancellationToken token)
        {
            return ValueTask.FromResult(new EngineStopResult { Status = EngineStopStatus.Clean });
        }

        public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
            CancellationToken token)
        {
            Commands.Add(command);
            return ValueTask.FromResult(new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = CommandOutcome.Applied, CompletedAt = DateTimeOffset.UtcNow
            });
        }

        public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
        {
            Output.Add(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetControllerManagementAsync(EngineControllerManagementContext context,
            CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseControllerAsync(EngineControllerReleaseContext context, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken token)
        {
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Observer : IHandheldObserver
    {
        internal List<CanonicalControllerSample> Samples { get; } = [];
        internal List<OemControlEvent> Events { get; } = [];
        internal List<(string Scope, string Message)> Faults { get; } = [];

        public ValueTask ControllerSampleAsync(CanonicalControllerSample sample, CancellationToken token)
        {
            Samples.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask OemEventAsync(OemControlEvent controlEvent, CancellationToken token)
        {
            Events.Add(controlEvent);
            return ValueTask.CompletedTask;
        }

        public void Faulted(string scope, string message)
        {
            Faults.Add((scope, message));
        }

        public ValueTask DescriptorsChangedAsync(CapabilityDescriptorSet descriptors, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask StateChangedAsync(CapabilityState state, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask PhysicalDevicesChangedAsync(IReadOnlyList<PhysicalDeviceIdentity> devices,
            HapticCapabilities? output, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask OemControlsChangedAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }
    }
}
