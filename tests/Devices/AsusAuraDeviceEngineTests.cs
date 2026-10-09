using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Settings;
using LibHandheld.Internal.Testing;

namespace LibHandheld.Tests.Devices;

public sealed class AsusAuraDeviceEngineTests
{
    [Fact]
    public async Task MergedGenerationsTranslateToTheCoreGenerationAndRouteAccessoryCommands()
    {
        var host = new Host();
        var core = new CoreEngine();
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await engine.StartAsync(Start(host), CancellationToken.None);
        var merged = host.Records.DescriptorSets.Last();
        Assert.NotEqual(core.Generation, merged.Generation);
        Assert.Equal(CommandOutcome.AppliedUnverified, (await engine.ExecuteCommandAsync(
            Command("core.power", null, merged.Generation), CancellationToken.None)).Outcome);
        Assert.Equal(core.Generation, Assert.Single(core.Commands).ExpectedDescriptorGeneration);
        await engine.ExecuteCommandAsync(Command("aura.color", "external", merged.Generation), CancellationToken.None);
        Assert.Single(core.Commands);
        Assert.Single(accessory.Commands);
        var rejected = await engine.ExecuteCommandAsync(Command("core.power", null, merged.Generation - 1),
            CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, rejected.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, rejected.Reason?.Code);
        Assert.Single(core.Commands);
    }

    [Fact]
    public async Task AccessoryTopologyReplaysKnownCoreStatesWithTheNewMergedGeneration()
    {
        var host = new Host();
        var core = new CoreEngine();
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await engine.StartAsync(Start(host), CancellationToken.None);
        var oldGeneration = host.Records.DescriptorSets.Last().Generation;
        var changed = host.Expect(surface => surface.Generation > oldGeneration && surface.Descriptors.Count == 1);
        accessory.RemoveModule();
        var replacement = await changed.WaitAsync(TimeSpan.FromSeconds(6));
        await host.ExpectState(state =>
                state.CapabilityId == "core.power" && state.DescriptorGeneration == replacement.Generation)
            .WaitAsync(TimeSpan.FromSeconds(2));
        var state = host.Records.CapabilityStates.Last(item => item.CapabilityId == "core.power");
        Assert.Equal(35, state.ObservedValue?.IntegerValue);
        Assert.Equal(replacement.Generation, state.DescriptorGeneration);
        Assert.Equal(CommandOutcome.Rejected, (await engine.ExecuteCommandAsync(
            Command("aura.color", "external", oldGeneration), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task OrdinaryInputOemAndHapticsPassThroughWithoutASusTranslation()
    {
        var host = new Host();
        var core = new CoreEngine();
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await engine.StartAsync(Start(host), CancellationToken.None);
        var sample = new CanonicalControllerSample
            { Timestamp = DateTimeOffset.UtcNow, Buttons = CanonicalButtons.A | CanonicalButtons.RearPaddle1 };
        var edge = new OemControlEvent("rear", OemPressKind.Short, sample.Timestamp, "one");
        await core.Sink!.PublishControllerSampleAsync(sample, CancellationToken.None);
        await core.Sink.PublishOemEventAsync(edge, CancellationToken.None);
        Assert.Equal(sample, Assert.Single(host.Records.ControllerSamples));
        Assert.Equal(edge, Assert.Single(host.Records.OemEvents));
        var rumble = new HapticOutputFrame { Timestamp = sample.Timestamp, LowFrequency = 0.5f };
        await engine.ApplyHapticOutputAsync(rumble, CancellationToken.None);
        Assert.Equal(rumble, Assert.Single(core.Rumble));
        Assert.Empty(accessory.Commands);
        await engine.SetControllerManagementAsync(new PluginControllerManagementContext(true, Deadline.Never),
            CancellationToken.None);
        Assert.Equal(1, core.ControllerChanges);
    }

    [Fact]
    public async Task SuspendClosesAccessoryAndResumeReacquiresItWithoutWritingPreferences()
    {
        var host = new Host();
        var core = new CoreEngine();
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await engine.StartAsync(Start(host), CancellationToken.None);
        await engine.SuspendAsync(new PluginQuiesceContext(Deadline.Never), CancellationToken.None);
        Assert.False(core.Active);
        Assert.False(accessory.ResourceOwned);
        Assert.Equal(1, accessory.Releases);
        host.CycleGeneration = 2;
        await engine.ResumeAsync(new PluginResumeContext(2, Deadline.Never), CancellationToken.None);
        Assert.True(core.Active);
        Assert.True(accessory.ResourceOwned);
        Assert.Equal(2, accessory.Acquires);
        Assert.Empty(accessory.Commands);
        Assert.All(
            host.Records.CapabilityStates.Where(state =>
                state.DescriptorGeneration == host.Records.DescriptorSets.Last().Generation),
            state => Assert.Equal(2, state.CycleGeneration));
        await engine.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never),
            CancellationToken.None);
        Assert.False(core.Active);
        Assert.False(accessory.ResourceOwned);
    }

    [Fact]
    public async Task CoreFailureDuringStartupStillUnwindsTheWrapper()
    {
        var core = new CoreEngine { FailStart = true };
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await Assert.ThrowsAsync<IOException>(() =>
            engine.StartAsync(Start(new Host()), CancellationToken.None).AsTask());
        Assert.False(core.Active);
        Assert.True(core.Stops > 0);
        Assert.False(accessory.ResourceOwned);
    }

    [Fact]
    public async Task CancellationAfterAccessoryAcquisitionReleasesBothOwners()
    {
        using var cancellation = new CancellationTokenSource();
        var core = new CoreEngine();
        var accessory = new Accessory { BeforeAcquireCompletion = cancellation.Cancel };
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.StartAsync(Start(new Host()), cancellation.Token).AsTask());
        Assert.False(core.Active);
        Assert.False(accessory.ResourceOwned);
        Assert.True(accessory.Releases > 0);
    }

    [Fact]
    public async Task CancellationDuringResumeCannotLeaveTheCoreResumed()
    {
        var host = new Host();
        var core = new CoreEngine();
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        await engine.StartAsync(Start(host), CancellationToken.None);
        await engine.SuspendAsync(new PluginQuiesceContext(Deadline.Never), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        accessory.BeforeAcquireCompletion = cancellation.Cancel;
        host.CycleGeneration = 2;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ResumeAsync(new PluginResumeContext(2, Deadline.Never), cancellation.Token).AsTask());
        Assert.False(core.Active);
        Assert.False(accessory.ResourceOwned);
    }

    [Fact]
    public async Task UnknownAccessoryValuesRemainUnknownAndHealthyAuraMakesAPassiveCoreDegraded()
    {
        var host = new Host();
        var core = new CoreEngine { OperationalState = PluginOperationalState.Passive };
        var accessory = new Accessory();
        await using var engine = new AsusAuraDeviceEngine(core, _ => accessory);
        var result = await engine.StartAsync(Start(host), CancellationToken.None);
        Assert.Equal(PluginOperationalState.Degraded, result.State);
        var aura = host.Records.CapabilityStates.Last(state => state.CapabilityId == "aura.color");
        Assert.Null(aura.ObservedValue);
        Assert.Null(aura.ObservedAt);
        Assert.Equal(HardwareStateQuality.Unknown, aura.Quality);
        Assert.True(aura.Available);
    }

    [Fact]
    public async Task HidOnlyCompositionNeverAdvertisesAsusLaptopFirmwareOnAnotherCore()
    {
        var host = new Host();
        var core = new CoreEngine();
        var aura = new AsusAuraController(() => [],
            _ => throw new InvalidOperationException("There are no actual module endpoints"));
        await using var engine =
            new AsusAuraDeviceEngine(core, context => new GenericAsusService(context, aura, false));
        await engine.StartAsync(Start(host), CancellationToken.None);
        Assert.Equal(core.PackageId, engine.PackageId);
        Assert.Equal("core.power", Assert.Single(host.Records.DescriptorSets.Last().Descriptors).CapabilityId);
        Assert.DoesNotContain(host.Records.DescriptorSets.Last().Descriptors,
            descriptor =>
                descriptor.Role is CapabilityRole.ChargeLimit or CapabilityRole.ScenarioMode
                    or CapabilityRole.FanCurve);
    }

    private static PluginStartContext Start(Host host)
    {
        return new PluginStartContext
        {
            Host = host, CycleGeneration = host.CycleGeneration, DeviceDefinitionId = "core", StateDirectory = "unused",
            ControllerManagementEnabled = false
        };
    }

    private static CapabilityCommand Command(string id, string? instance, long generation)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, InstanceId = instance,
            RequestedValue = CapabilityValue.Integer(40),
            ExpectedCycleGeneration = 1, ExpectedDescriptorGeneration = generation, Deadline = Deadline.Never
        };
    }

    private static CapabilityDescriptor Descriptor(string id, string? instance)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.GenericRange,
            ValueKind = CapabilityValueKind.Integer,
            Minimum = 0, Maximum = 100, Step = 1,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id },
            SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        };
    }

    private sealed class Accessory() : ProfiledFeatureService("fake-accessory")
    {
        internal int Acquires;
        internal Action? BeforeAcquireCompletion;
        internal int Releases;
        internal bool ResourceOwned;
        private long _generation;
        private volatile bool _module = true;
        internal List<CapabilityCommand> Commands { get; } = [];
        internal override long DescriptorGeneration => Interlocked.Read(ref _generation);

        internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
            _module ? [Descriptor("aura.color", "external")] : [];

        internal override IReadOnlyList<CapabilityState> States => _module
            ?
            [
                new CapabilityState
                {
                    CapabilityId = "aura.color", InstanceId = "external", Available = ResourceOwned,
                    Quality = HardwareStateQuality.Unknown, DescriptorGeneration = 0, CycleGeneration = 0
                }
            ]
            : [];

        public override bool Suspendable => true;

        internal void RemoveModule()
        {
            _module = false;
            Interlocked.Increment(ref _generation);
        }

        public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
            CancellationToken token)
        {
            Acquires++;
            ResourceOwned = true;
            BeforeAcquireCompletion?.Invoke();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Set(DeviceServiceState.Owned));
        }

        public override ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
            CancellationToken token)
        {
            Releases++;
            ResourceOwned = false;
            return ValueTask.FromResult(Set(DeviceServiceState.Idle));
        }

        internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(command);
            return ValueTask.FromResult(CommandResults.Unverified(command));
        }
    }

    private sealed class CoreEngine : IDeviceEngine
    {
        internal bool Active;
        internal int ControllerChanges;
        internal long Cycle;
        internal bool FailStart;
        internal long Generation = 7;
        internal PluginOperationalState OperationalState = PluginOperationalState.Active;
        internal IDeviceSink? Sink;
        internal int Stops;
        internal List<CapabilityCommand> Commands { get; } = [];
        internal List<HapticOutputFrame> Rumble { get; } = [];
        public string PackageId => "fake-core";

        public ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context, CancellationToken token)
        {
            return ValueTask.FromResult(new PluginDetectionResult { Matched = true, DeviceDefinitionId = "core" });
        }

        public async ValueTask<PluginStartResult> StartAsync(PluginStartContext context, CancellationToken token)
        {
            Sink = context.Host;
            Cycle = context.CycleGeneration;
            Active = true;
            await Publish(token);
            if (FailStart)
            {
                throw new IOException("Core acquired resources before failing");
            }

            return new PluginStartResult { State = OperationalState };
        }

        public async ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context, CancellationToken token)
        {
            Active = true;
            Cycle = context.CycleGeneration;
            Generation++;
            await Publish(token);
            return new PluginStartResult { State = OperationalState };
        }

        public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
            CancellationToken token)
        {
            Commands.Add(command);
            return ValueTask.FromResult(CommandResults.Unverified(command));
        }

        public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken token)
        {
            Active = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken token)
        {
            Active = false;
            Stops++;
            return ValueTask.FromResult(new PluginStopResult { Status = PluginStopStatus.Clean });
        }

        public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context,
            CancellationToken token)
        {
            ControllerChanges++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
        {
            Rumble.Add(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken token)
        {
            return ValueTask.FromResult(new PluginDiagnostics());
        }

        public ValueTask DisposeAsync()
        {
            Active = false;
            return ValueTask.CompletedTask;
        }

        private async ValueTask Publish(CancellationToken token)
        {
            await Sink!.PublishDescriptorsAsync(new CapabilityDescriptorSet
                {
                    Generation = Generation, CycleGeneration = Cycle, Descriptors = [Descriptor("core.power", null)]
                },
                token);
            await Sink.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = "core.power", Available = OperationalState != PluginOperationalState.Passive,
                ObservedValue = OperationalState == PluginOperationalState.Passive ? null : CapabilityValue.Integer(35),
                Quality = OperationalState == PluginOperationalState.Passive
                    ? HardwareStateQuality.Unknown
                    : HardwareStateQuality.Observed,
                ObservedAt = OperationalState == PluginOperationalState.Passive ? null : DateTimeOffset.UtcNow,
                DescriptorGeneration = Generation, CycleGeneration = Cycle
            }, token);
        }
    }

    private sealed class Host : IDeviceSink
    {
        private Func<CapabilityState, bool>? _expectState;
        private Func<CapabilityDescriptorSet, bool>? _expectSurface;
        private TaskCompletionSource<CapabilityState>? _state;
        private TaskCompletionSource<CapabilityDescriptorSet>? _surface;
        internal TestPluginHostAdapter Records { get; } = new(1);
        public long CycleGeneration { get; set; } = 1;

        public async ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet surface, CancellationToken token)
        {
            await Records.PublishDescriptorsAsync(surface, token);
            if (_expectSurface?.Invoke(surface) == true)
            {
                _surface?.TrySetResult(surface);
            }
        }

        public async ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken token)
        {
            await Records.PublishCapabilityStateAsync(state, token);
            if (_expectState?.Invoke(state) == true)
            {
                _state?.TrySetResult(state);
            }
        }

        public ValueTask PublishControllerSampleAsync(CanonicalControllerSample sample, CancellationToken token)
        {
            return Records.PublishControllerSampleAsync(sample, token);
        }

        public ValueTask PublishPhysicalDevicesAsync(IReadOnlyList<PhysicalDeviceIdentity> identities,
            HapticCapabilities? output, CancellationToken token)
        {
            return Records.PublishPhysicalDevicesAsync(identities, output, token);
        }

        public ValueTask PublishOemControlsAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken token)
        {
            return Records.PublishOemControlsAsync(controls, token);
        }

        public ValueTask PublishOemEventAsync(OemControlEvent control, CancellationToken token)
        {
            return Records.PublishOemEventAsync(control, token);
        }

        public ValueTask PublishSettingsManifestAsync(PluginSettingsManifest manifest, CancellationToken token)
        {
            return Records.PublishSettingsManifestAsync(manifest, token);
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
        }

        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
        }

        internal Task<CapabilityDescriptorSet> Expect(Func<CapabilityDescriptorSet, bool> predicate)
        {
            _surface = new TaskCompletionSource<CapabilityDescriptorSet>(TaskCreationOptions
                .RunContinuationsAsynchronously);
            _expectSurface = predicate;
            return _surface.Task;
        }

        internal Task<CapabilityState> ExpectState(Func<CapabilityState, bool> predicate)
        {
            _state = new TaskCompletionSource<CapabilityState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _expectState = predicate;
            foreach (var state in Records.CapabilityStates)
            {
                if (predicate(state))
                {
                    _state.TrySetResult(state);
                }
            }

            return _state.Task;
        }
    }
}
