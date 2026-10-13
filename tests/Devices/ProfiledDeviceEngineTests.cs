using System.Collections.Concurrent;
using LibHandheld.Families.Gpd;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

public sealed class ProfiledDeviceEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuiescingForwardsTheReleaseOfAHeldNativeOemControl(bool stop)
    {
        using var directory = new TemporaryDirectory();
        var source = new FakeOemSource();
        var profile = GpdModels.All.Single(model => model.Definition.Id == "gpd-win4-8840u") with
        {
            UseStandardServices = false, Fan = null, Controller = null, Motion = null, Touchpad = null,
            KeyboardChords = [], ServiceFactories = [], CreateOemSource = () => source,
            NativeOemRequiresController = false
        };
        await using var engine = new ProfiledDeviceEngine(profile);
        var host = await Start(engine, directory.Root);
        source.Press();
        if (stop)
        {
            await engine.StopAsync(new EngineStopContext(Deadline.Never), CancellationToken.None);
        }
        else
        {
            await engine.SuspendAsync(new EngineQuiesceContext(Deadline.Never), CancellationToken.None);
        }

        Assert.Equal(new[] { OemControlEdge.Pressed, OemControlEdge.Released },
            host.OemEvents.Select(input => input.Edge));
        Assert.Equal(host.OemEvents[0].DeduplicationId, host.OemEvents[1].DeduplicationId);
    }

    [Fact]
    public async Task SharedFanMeasurementsRefreshBeforeHostExpiryWithoutReadingControls()
    {
        using var directory = new TemporaryDirectory();
        var power = new FakePower();
        var ec = new FakeEc();
        ec.Bytes[0x0478] = 0x12;
        ec.Bytes[0x0479] = 0x34;
        var profile = GpdModels.All.Single(model => model.Definition.Id == "gpd-win5") with
        {
            Controller = null, Motion = null, Touchpad = null, KeyboardChords = [],
            CreateOemSource = null, ServiceFactories = []
        };
        await using var engine = new ProfiledDeviceEngine(profile, () => power, _ => ec);
        var host = new TestPluginHostAdapter(1);
        await engine.StartAsync(new EngineStartContext
        {
            Host = host, Identity = new DeviceIdentitySnapshot { BiosVersion = "1" },
            DeviceDefinitionId = profile.Definition.Id, StateDirectory = directory.Root,
            Journal = TestRecovery.Open(directory.Root), ControllerManagementEnabled = false
        }, CancellationToken.None);
        ec.Reads.Clear();
        ec.Bytes[0x0478] = 0x56;
        ec.Bytes[0x0479] = 0x78;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!host.CapabilityStates.Any(value => value.CapabilityId == CapabilityIds.FanRpm
                                                   && value.ObservedValue?.IntegerValue == 0x5678))
        {
            await Task.Delay(20, deadline.Token);
        }

        Assert.Equal(new ushort[] { 0x0478, 0x0479 }, ec.Reads);
        Assert.Empty(power.Writes);
        Assert.Equal(0, power.Reads);
        await engine.StopAsync(new EngineStopContext(Deadline.Never), CancellationToken.None);
    }

    [Fact]
    public async Task PairedPowerCommandsRestoreTheFirstOriginalAndRejectExpiredDeadlines()
    {
        using var directory = new TemporaryDirectory();
        var power = new FakePower();
        await using var engine = Engine(power);
        var host = await Start(engine, directory.Root);
        var stale = Command(host, CapabilityIds.PowerSustained, 20) with { Deadline = Deadline.Expired };
        Assert.Equal(CommandOutcome.Rejected,
            (await engine.ExecuteCommandAsync(stale, CancellationToken.None)).Outcome);
        Assert.Empty(power.Writes);
        Assert.Equal(CommandOutcome.Applied,
            (await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 20), CancellationToken.None))
            .Outcome);
        await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 18), CancellationToken.None);
        await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.Equal(new CpuPowerLimits(15, 20, 25), power.Writes[^1]);
        Assert.Equal(new CpuPowerLimits(20, 25, 25), power.Writes[0]);
    }

    [Fact]
    public async Task MissingReadbackAllowsAWriteAndReportsItsCleanupLimit()
    {
        using var directory = new TemporaryDirectory();
        var power = new FakePower { RefuseRead = true };
        await using var engine = Engine(power);
        var host = await Start(engine, directory.Root);
        Assert.Equal(CommandOutcome.Applied,
            (await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 20), CancellationToken.None))
            .Outcome);
        Assert.Single(power.Writes);
        var stopped = await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.Equal(EngineStopStatus.Unverified, stopped.Status);
    }

    [Fact]
    public async Task OriginalWin4UnlockAndFanDutyRestoreIndependently()
    {
        using var directory = new TemporaryDirectory();
        var power = new FakePower();
        var ec = new FakeEc();
        ec.Bytes[0x2000] = 0x55;
        ec.Bytes[0x1060] = 0x12;
        ec.Bytes[0xC311] = 20;
        var profile = GpdModels.All.Single(model => model.Definition.Id == "gpd-win4-6800u") with
        {
            Controller = null, Motion = null, Touchpad = null, KeyboardChords = [], ServiceFactories = []
        };
        await using var engine = new ProfiledDeviceEngine(profile, () => power, _ => ec);
        var host = new TestPluginHostAdapter(1);
        await engine.StartAsync(new EngineStartContext
        {
            Host = host,
            Identity = new DeviceIdentitySnapshot
                { SystemManufacturer = "GPD", SystemProduct = "G1618-04", BiosVersion = "1" },
            DeviceDefinitionId = profile.Definition.Id,
            StateDirectory = directory.Root, Journal = TestRecovery.Open(directory.Root),
            ControllerManagementEnabled = false
        }, CancellationToken.None);
        Assert.Equal((byte)0x92, ec.Bytes[0x1060]);
        await engine.ExecuteCommandAsync(Command(host, "fan.duty", 50) with { PairedPowerLimitWatts = null },
            CancellationToken.None);
        Assert.Equal((byte)64, ec.Bytes[0xC311]);
        await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.Equal((byte)20, ec.Bytes[0xC311]);
        Assert.Equal((byte)0x12, ec.Bytes[0x1060]);
    }

    private static ProfiledDeviceEngine Engine(FakePower power)
    {
        return new ProfiledDeviceEngine(GpdModels.All.Single(model => model.Definition.Id == "gpd-win4-8840u") with
        {
            Fan = null, Controller = null, Motion = null, Touchpad = null, KeyboardChords = [],
            ServiceFactories = [], OpenPower = _ => power
        }, () => power, _ => throw new InvalidOperationException());
    }

    private static async Task<TestPluginHostAdapter> Start(ProfiledDeviceEngine engine, string directory)
    {
        var host = new TestPluginHostAdapter(1);
        await engine.StartAsync(new EngineStartContext
        {
            Host = host,
            Identity = new DeviceIdentitySnapshot
                { SystemManufacturer = "GPD", SystemProduct = "G1618-04", BiosVersion = "1" },
            DeviceDefinitionId = "gpd-win4-8840u",
            StateDirectory = directory, Journal = TestRecovery.Open(directory), ControllerManagementEnabled = false
        }, CancellationToken.None);
        return host;
    }

    private static CapabilityCommand Command(TestPluginHostAdapter host, string id, int value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = CapabilityValue.Integer(value),
            PairedPowerLimitWatts = 25,
            Deadline = Deadline.Never
        };
    }

    private sealed class FakeOemSource : IProfiledOemSource
    {
        private Action<string, CanonicalButtons, bool>? _publish;
        private bool _held;

        public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
        {
            _publish = publish;
            return ValueTask.FromResult(true);
        }

        internal void Press()
        {
            _held = true;
            _publish!("home", CanonicalButtons.Guide, true);
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            if (_held)
            {
                _held = false;
                _publish!("home", CanonicalButtons.Guide, false);
            }

            _publish = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakePower : ICpuPowerTransport
    {
        private CpuPowerLimits _limits = new(15, 20, 25);
        internal bool RefuseRead { get; init; }
        internal List<CpuPowerLimits> Writes { get; } = [];
        internal int Reads { get; private set; }

        public CpuPowerLimits ReadLimits()
        {
            Reads++;
            return RefuseRead ? throw new IOException("No readback") : _limits;
        }

        public void WriteLimits(CpuPowerLimits limits)
        {
            Writes.Add(limits);
            _limits = limits;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEc : IEmbeddedController
    {
        internal Dictionary<ushort, byte> Bytes { get; } = [];
        internal ConcurrentQueue<ushort> Reads { get; } = new();

        public byte ReadByte(ushort address)
        {
            Reads.Enqueue(address);
            return Bytes.GetValueOrDefault(address);
        }

        public ushort ReadUInt16(ushort address)
        {
            return (ushort)(ReadByte(address) | (ReadByte((ushort)(address + 1)) << 8));
        }

        public void WriteByte(ushort address, byte value)
        {
            Bytes[address] = value;
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            WriteByte(address, (byte)value);
            WriteByte((ushort)(address + 1), (byte)(value >> 8));
        }

        public T Transaction<T>(Func<T> operation)
        {
            return operation();
        }

        public void Dispose()
        {
        }
    }
}
