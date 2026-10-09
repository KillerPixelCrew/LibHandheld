using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

public sealed class ProfiledHardwareServiceTests
{
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
}
