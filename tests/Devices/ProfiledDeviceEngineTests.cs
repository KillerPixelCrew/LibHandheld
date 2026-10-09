using LibHandheld.Families.Gpd;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Testing;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

public sealed class ProfiledDeviceEngineTests
{
    [Fact]
    public async Task PairedPowerCommandsRestoreTheFirstOriginalAndRejectStaleGenerations()
    {
        using var directory = new TemporaryDirectory();
        var power = new FakePower();
        await using var engine = Engine(power);
        var host = await Start(engine, directory.Root);
        var stale = Command(host, CapabilityIds.PowerSustained, 20) with { ExpectedCycleGeneration = 0 };
        Assert.Equal(CommandOutcome.Rejected,
            (await engine.ExecuteCommandAsync(stale, CancellationToken.None)).Outcome);
        Assert.Empty(power.Writes);
        Assert.Equal(CommandOutcome.AppliedUnverified,
            (await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 20), CancellationToken.None))
            .Outcome);
        await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 18), CancellationToken.None);
        await engine.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never),
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
        Assert.Equal(CommandOutcome.AppliedUnverified,
            (await engine.ExecuteCommandAsync(Command(host, CapabilityIds.PowerSustained, 20), CancellationToken.None))
            .Outcome);
        Assert.Single(power.Writes);
        var stopped = await engine.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never),
            CancellationToken.None);
        Assert.Equal(PluginStopStatus.Unverified, stopped.Status);
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
            Controller = null, Motion = null, Touchpad = null, KeyboardChords = [], CreateNativeService = null
        };
        await using var engine = new ProfiledDeviceEngine(profile, () => power, _ => ec);
        await engine.DetectAsync(new PluginDetectionContext
        {
            Identity = new DeviceIdentitySnapshot
            {
                SystemManufacturer = "GPD", SystemProduct = "G1618-04", ProcessorName = profile.Processors[0],
                BiosVersion = "1"
            }
        }, CancellationToken.None);
        var host = new TestPluginHostAdapter(1);
        await engine.StartAsync(new PluginStartContext
        {
            Host = host, CycleGeneration = 1, DeviceDefinitionId = profile.Definition.Id,
            StateDirectory = directory.Root, ControllerManagementEnabled = false
        }, CancellationToken.None);
        Assert.Equal((byte)0x92, ec.Bytes[0x1060]);
        await engine.ExecuteCommandAsync(Command(host, "fan.duty", 50) with { PairedPowerLimitWatts = null },
            CancellationToken.None);
        Assert.Equal((byte)64, ec.Bytes[0xC311]);
        await engine.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting, Deadline.Never),
            CancellationToken.None);
        Assert.Equal((byte)20, ec.Bytes[0xC311]);
        Assert.Equal((byte)0x12, ec.Bytes[0x1060]);
    }

    private static ProfiledDeviceEngine Engine(FakePower power)
    {
        return new ProfiledDeviceEngine(GpdModels.All.Single(model => model.Definition.Id == "gpd-win4-8840u") with
        {
            Fan = null, Controller = null, Motion = null, Touchpad = null, KeyboardChords = [],
            CreateNativeService = null
        }, () => power, _ => throw new InvalidOperationException());
    }

    private static async Task<TestPluginHostAdapter> Start(ProfiledDeviceEngine engine, string directory)
    {
        var detected = await engine.DetectAsync(new PluginDetectionContext
        {
            Identity = new DeviceIdentitySnapshot
            {
                SystemManufacturer = "GPD", SystemProduct = "G1618-04",
                ProcessorName = "AMD Ryzen 7 8840U w/ Radeon 780M Graphics", BiosVersion = "1"
            }
        }, CancellationToken.None);
        var host = new TestPluginHostAdapter(1);
        await engine.StartAsync(new PluginStartContext
        {
            Host = host, CycleGeneration = 1, DeviceDefinitionId = detected.DeviceDefinitionId!,
            StateDirectory = directory, ControllerManagementEnabled = false
        }, CancellationToken.None);
        return host;
    }

    private static CapabilityCommand Command(TestPluginHostAdapter host, string id, int value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = CapabilityValue.Integer(value),
            PairedPowerLimitWatts = 25,
            ExpectedCycleGeneration = 1, ExpectedDescriptorGeneration = host.DescriptorSets[^1].Generation,
            Deadline = Deadline.Never
        };
    }

    private sealed class FakePower : ICpuPowerTransport
    {
        private CpuPowerLimits _limits = new(15, 20, 25);
        internal bool RefuseRead { get; init; }
        internal List<CpuPowerLimits> Writes { get; } = [];

        public CpuPowerLimits ReadLimits()
        {
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

        public byte ReadByte(ushort address)
        {
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
