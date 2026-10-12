using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

public sealed class ProfiledHardwareServiceTests
{
    [Fact]
    public async Task PowerReadsOnlyItsFirstRestoreOriginalAndRetainsAcceptedTargets()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var transport = new RecordingPower();
        var service = new ProfiledPowerService(() => transport, journal);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("test"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, transport.Reads);
        Assert.All(service.States, state =>
        {
            Assert.True(state.Available);
            Assert.Null(state.ObservedValue);
        });

        await service.WriteAsync(new CpuPowerLimits(18, 22, 22), "test", CancellationToken.None, () => { });
        var at = service.ObservedAt;
        await service.RefreshAsync(CancellationToken.None);
        await service.WriteAsync(new CpuPowerLimits(20, 25, 25), "test", CancellationToken.None, () => { });
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, transport.Reads);
        Assert.Equal(new CpuPowerLimits(20, 25, 25), service.Observed);
        Assert.True(service.ObservedAt >= at);
        Assert.Equal(DeviceServiceState.Idle, (await service.ReleaseAsync(cycle, CancellationToken.None)).State);
        Assert.Equal(new CpuPowerLimits(15, 20, 25), transport.Writes[^1]);
        Assert.Equal(1, transport.Reads);
    }

    [Fact]
    public async Task FailedOriginalCaptureDoesNotGateOrRepeatPowerWrites()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var transport = new RecordingPower { RefuseCapture = true };
        var service = new ProfiledPowerService(() => transport, journal);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("test"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.WriteAsync(new CpuPowerLimits(18, 22, 22), "test", CancellationToken.None, () => { });
        await service.WriteAsync(new CpuPowerLimits(20, 25, 25), "test", CancellationToken.None, () => { });
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, transport.Reads);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(new CpuPowerLimits(20, 25, 25), service.Observed);
        Assert.Equal(DeviceServiceState.ReleasedUnverified,
            (await service.ReleaseAsync(cycle, CancellationToken.None)).State);
    }

    [Fact]
    public async Task FanRefreshReadsRpmWithoutQueryingModeOrDuty()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var ec = new SettingsEc();
        var fan = new ProfiledFanService(new EcFanProfile(0x4E, 0x4F, [0x10], 100,
            0x11, RpmHighAddress: 0x20, RpmLowAddress: 0x42), () => ec, journal);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("test"));
        await fan.AcquireAsync(cycle, CancellationToken.None);
        await fan.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain((ushort)0x10, ec.Reads);
        Assert.DoesNotContain((ushort)0x11, ec.Reads);
        Assert.All(fan.Descriptors.Where(descriptor => descriptor.SupportsWrite),
            descriptor => Assert.False(descriptor.SupportsRead));

        await fan.WriteAsync("custom", 55, "test", CancellationToken.None, () => { });
        var at = fan.ObservedAt;
        var captureReads = ec.Reads.Count(address => address is 0x10 or 0x11);
        await fan.RefreshAsync(CancellationToken.None);
        await fan.WriteAsync("custom", 65, "test", CancellationToken.None, () => { });
        Assert.Equal(2, captureReads);
        Assert.Equal(captureReads, ec.Reads.Count(address => address is 0x10 or 0x11));
        Assert.Equal(65, fan.Duty);
        Assert.Equal("custom", fan.Mode);
        Assert.True(fan.ObservedAt >= at);
        Assert.Equal(0x1234, fan.Rpm);
        await fan.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(captureReads, ec.Reads.Count(address => address is 0x10 or 0x11));
    }

    [Fact]
    public async Task RpmReadsNoncontiguousHighAndLowBytesInOneTransaction()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var ec = new RecordingEc();
        var fan = new ProfiledFanService(new EcFanProfile(0x4E, 0x4F, [0x10], 100,
            RpmHighAddress: 0x20, RpmLowAddress: 0x42, ReadDuty: false), () => ec, journal);
        await fan.AcquireAsync(new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("test")), CancellationToken.None);
        Assert.Equal(0x1234, fan.Rpm);
        Assert.Equal(1, ec.Transactions);
        await fan.DisposeAsync();
    }

    [Fact]
    public async Task ActualFanDutyDoesNotReplaceAcceptedTargetsOrGateWritesWhenItsReadFails()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var ec = new SettingsEc { DutyValue = 50 };
        var fan = new ProfiledFanService(new EcFanProfile(0x4E, 0x4F, [0x10], 100,
            0x11, ObserveDuty: true), () => ec, journal);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("test"));
        await fan.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(50, fan.MeasuredDuty);
        await fan.WriteAsync("custom", 65, "test", CancellationToken.None, () => { });
        await fan.RefreshAsync(CancellationToken.None);
        Assert.Equal(50, fan.MeasuredDuty);
        Assert.Equal(65, fan.Duty);
        ec.FailDutyRead = true;
        await fan.RefreshAsync(CancellationToken.None);
        Assert.Null(fan.MeasuredDuty);
        Assert.Null(fan.MeasuredDutyObservedAt);
        Assert.Equal(65, fan.Duty);
        Assert.True(fan.States.Single(state => state.CapabilityId == "fan.duty").Available);
        await fan.WriteAsync("custom", 70, "test", CancellationToken.None, () => { });
        Assert.Equal(70, fan.Duty);
        await fan.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task OriginalWin4UsesTheHcAutomaticRegisterAndRestoresBothDistinctRegisters()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var ec = new AutomaticFanEc();
        var fan = new ProfiledFanService(new EcFanProfile(0x2E, 0x2F, [0xC311], 127,
            AutomaticDutyAddress: 0xC880), () => ec, journal);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("test"));
        await fan.AcquireAsync(cycle, CancellationToken.None);
        await fan.WriteAsync("custom", 50, "test", CancellationToken.None, () => { });
        Assert.Equal(((ushort)0xC311, (byte)64), Assert.Single(ec.Writes));
        ec.Writes.Clear();
        await fan.WriteAsync("automatic", null, "test", CancellationToken.None, () => { });
        Assert.Equal(((ushort)0xC880, (byte)0), Assert.Single(ec.Writes));
        ec.Writes.Clear();
        await fan.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(new (ushort, byte)[] { (0xC311, 20), (0xC880, 35) }, ec.Writes);
    }

    private sealed class AutomaticFanEc : IEmbeddedController
    {
        internal List<(ushort Address, byte Value)> Writes { get; } = [];

        public byte ReadByte(ushort address)
        {
            return address switch { 0xC311 => 20, 0xC880 => 35, _ => 0 };
        }

        public void WriteByte(ushort address, byte value)
        {
            Writes.Add((address, value));
        }

        public T Transaction<T>(Func<T> operation)
        {
            return operation();
        }

        public void Dispose()
        {
        }

        public ushort ReadUInt16(ushort address)
        {
            throw new NotSupportedException();
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingEc : IEmbeddedController
    {
        private bool _inTransaction;
        internal int Transactions { get; private set; }

        public byte ReadByte(ushort address)
        {
            Assert.True(_inTransaction);
            return address switch { 0x20 => 0x12, 0x42 => 0x34, _ => throw new InvalidOperationException() };
        }

        public T Transaction<T>(Func<T> operation)
        {
            Transactions++;
            _inTransaction = true;
            try
            {
                return operation();
            }
            finally
            {
                _inTransaction = false;
            }
        }

        public ushort ReadUInt16(ushort address)
        {
            throw new NotSupportedException();
        }

        public void WriteByte(ushort address, byte value)
        {
            throw new NotSupportedException();
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingPower : ICpuPowerTransport
    {
        internal int Reads { get; private set; }
        internal bool RefuseCapture { get; init; }
        internal List<CpuPowerLimits> Writes { get; } = [];

        public CpuPowerLimits ReadLimits()
        {
            Reads++;
            if (RefuseCapture)
            {
                throw new NotSupportedException("The original limits cannot be captured.");
            }

            return new CpuPowerLimits(15, 20, 25);
        }

        public void WriteLimits(CpuPowerLimits limits)
        {
            Writes.Add(limits);
        }

        public void Dispose()
        {
        }
    }

    private sealed class SettingsEc : IEmbeddedController
    {
        internal List<ushort> Reads { get; } = [];
        internal byte DutyValue { get; init; } = 20;
        internal bool FailDutyRead { get; set; }

        public byte ReadByte(ushort address)
        {
            Reads.Add(address);
            if (address == 0x10 && FailDutyRead)
            {
                throw new IOException("Fan measurement unavailable");
            }

            return address switch { 0x10 => DutyValue, 0x11 => 1, 0x20 => 0x12, 0x42 => 0x34, _ => 0 };
        }

        public void WriteByte(ushort address, byte value)
        {
        }

        public ushort ReadUInt16(ushort address)
        {
            throw new NotSupportedException();
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            throw new NotSupportedException();
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
