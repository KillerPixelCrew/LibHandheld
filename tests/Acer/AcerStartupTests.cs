using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Acer;

public sealed class AcerStartupTests
{
    [Fact]
    public async Task UnsetChargeCeilingIsInitializedWithoutDisablingKnownOptimizedCharging()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new BatteryWmi { Enabled = true };
        var ec = new BatteryEc();
        var context = Context(directory.Root);
        var service = new AcerBatteryService(context, new Lease(ec), initializeUnknownValues: true, () => wmi);
        var cycle = Cycle(context);

        await service.AcquireAsync(cycle, CancellationToken.None);

        Assert.True(Value(service, "battery.charge-protection")?.BooleanValue);
        Assert.Equal(100, Value(service, "battery.charge-limit")?.IntegerValue);
        Assert.Empty(wmi.Writes);
        Assert.Equal([false], ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task KnownChargeSettingsStayUntouchedAndAnUncertainCeilingIsNotRetried()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new BatteryWmi { Enabled = false };
        var ec = new BatteryEc { Boundary = new AcerChargeBoundary(true, 80, 50) };
        var context = Context(directory.Root);
        var cycle = Cycle(context);
        var service = new AcerBatteryService(context, new Lease(ec), initializeUnknownValues: true, () => wmi);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(80, Value(service, "battery.charge-limit")?.IntegerValue);
        Assert.Empty(ec.Writes);
        await service.ReleaseAsync(cycle, CancellationToken.None);

        ec.Boundary = null;
        ec.FailWrites = true;
        service = new AcerBatteryService(context, new Lease(ec), initializeUnknownValues: true, () => wmi);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Null(Value(service, "battery.charge-limit"));
        await service.ReleaseAsync(cycle, CancellationToken.None);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Single(ec.Writes);
        Assert.Null(Value(service, "battery.charge-limit"));
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task UnreadableOptimizedChargingIsSetOnceWithoutAGetterGate()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new BatteryWmi();
        var context = Context(directory.Root);
        var service = new AcerBatteryService(context, new Lease(new BatteryEc()), initializeUnknownValues: true, () => wmi);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal([false], wmi.Writes);
        Assert.False(Value(service, "battery.charge-protection")?.BooleanValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoInitializationPublishesOnlyAcceptedValuesAndNeverRepeatsOnReacquisition(bool fail)
    {
        var hid = new LogoHid { FailWrites = fail };
        var service = new AcerLogoLightingService(initializeUnknownValues: true, open: () => hid);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("atlas"));

        Assert.Equal(DeviceServiceState.Owned, (await service.AcquireAsync(cycle, CancellationToken.None)).State);

        Assert.Single(hid.Settings);
        Assert.Equal(AcerLogoLightingService.Packet("off", brightness: 100, speed: 5, color: 0xFFFFFF), hid.Settings[0]);
        if (fail)
        {
            Assert.All(service.States, state => Assert.Null(state.ObservedValue));
        }
        else
        {
            Assert.All(service.States, state => Assert.NotNull(state.ObservedValue));
            Assert.Equal("off", Value(service, "lighting.effect", "logo")?.ChoiceValue);
            Assert.Equal(5, Value(service, "lighting.effect-speed", "logo")?.IntegerValue);
        }
        await service.ReleaseAsync(cycle, CancellationToken.None);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Single(hid.Settings);
        Assert.Equal(fail, Value(service, "lighting.effect-speed", "logo") is null);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task AModelWithoutStartupInitializationKeepsTheLogoUnwritten()
    {
        var hid = new LogoHid();
        var service = new AcerLogoLightingService(open: () => hid);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("atlas"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Empty(hid.Settings);
        Assert.All(service.States, state => Assert.Null(state.ObservedValue));
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    private static CapabilityValue? Value(ProfiledFeatureService service, string id, string? instance = null)
    {
        return service.States.Single(state => state.CapabilityId == id && state.InstanceId == instance).ObservedValue;
    }

    private static ProfiledFeatureContext Context(string directory)
    {
        return new ProfiledFeatureContext(directory, "atlas|V1.04", new TestPluginHostAdapter(1))
        {
            Journal = TestRecovery.Open(directory)
        };
    }

    private static DeviceCycleContext<ProfiledCycleIdentity> Cycle(ProfiledFeatureContext context)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity(context.Firmware));
    }

    private sealed class Lease(IAcerEcTransport transport) : IAcerLease<IAcerEcTransport>
    {
        public IAcerEcTransport Acquire() => transport;
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    private sealed class BatteryWmi : IAcerBatteryWmi
    {
        internal bool? Enabled { get; set; }
        internal List<bool> Writes { get; } = [];
        public (bool Supported, bool? Enabled) Read() => (true, Enabled);
        public void Write(bool enabled, CancellationToken token, Action? dispatched)
        {
            token.ThrowIfCancellationRequested();
            dispatched?.Invoke();
            Writes.Add(enabled);
            Enabled = enabled;
        }
        public void Dispose()
        {
        }
    }

    private sealed class BatteryEc : IAcerEcTransport
    {
        internal AcerChargeBoundary? Boundary { get; set; }
        internal bool FailWrites { get; set; }
        internal List<bool> Writes { get; } = [];
        public byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null)
        {
            token.ThrowIfCancellationRequested();
            if (payload[4] == 1)
            {
                dispatched?.Invoke();
                Writes.Add(payload[6] == 1);
                if (FailWrites)
                {
                    throw new IOException("Reply lost after dispatch.");
                }
                Boundary = new AcerChargeBoundary(payload[6] == 1, payload[7], payload[8]);
            }
            var reply = new byte[64];
            reply[1] = 0xE0;
            reply[2] = 3;
            reply[3] = 0x0B;
            if (Boundary is { } boundary)
            {
                reply[5] = 3;
                reply[6] = boundary.Enabled ? (byte)1 : (byte)0;
                reply[7] = boundary.Upper;
                reply[8] = boundary.Lower;
            }
            else
            {
                reply.AsSpan(5, 4).Fill(0xFF);
            }
            return reply;
        }
        public void Dispose()
        {
        }
    }

    private sealed class LogoHid : IAcerLogoLightingTransport
    {
        internal bool FailWrites { get; init; }
        internal List<byte[]> Settings { get; } = [];
        public void Read(byte[] report)
        {
            if (report[0] == 0xA1)
            {
                report[1] = 1;
                report[2] = 0x80;
            }
            else
            {
                report[1] = 0x80;
                report[2] = 1;
                report[4] = 1;
                report[5] = 7;
            }
        }
        public void Write(byte[] report)
        {
            if (report[0] != 0xA4)
            {
                return;
            }
            Settings.Add((byte[])report.Clone());
            if (FailWrites)
            {
                throw new IOException("Reply lost after dispatch.");
            }
        }
        public void Dispose()
        {
        }
    }
}
