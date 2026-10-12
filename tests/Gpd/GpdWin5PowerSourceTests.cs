using LibHandheld.Families.Gpd;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Tests.Gpd;

public sealed class GpdWin5PowerSourceTests
{
    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x10, true)]
    [InlineData(0x00, false)]
    [InlineData(0x01, false)]
    [InlineData(0x12, false)]
    public async Task ReadsTheExactNativePowerSourceWithoutWriting(int value, bool expected)
    {
        var ec = new FakeEc { Value = (byte)value };
        var service = new GpdWin5PowerSourceService(() => ec);
        await service.AcquireAsync(default, CancellationToken.None);
        var descriptor = Assert.Single(service.Descriptors);
        Assert.True(descriptor.SupportsRead);
        Assert.False(descriptor.SupportsWrite);
        Assert.False(descriptor.SupportsAction);
        var state = Assert.Single(service.States);
        Assert.Equal(expected, state.ObservedValue!.BooleanValue);
        Assert.Equal(HardwareStateQuality.Observed, state.Quality);
        Assert.Equal((ushort)0x0577, Assert.Single(ec.ReadAddresses));
        await service.ReleaseAsync(default, CancellationToken.None);
        Assert.True(ec.Disposed);
        Assert.Null(Assert.Single(service.States).ObservedValue);
    }

    [Fact]
    public async Task AFailedReadClearsTheOldObservationAndCanRecoverWithoutWrites()
    {
        var ec = new FakeEc { Value = 0x02 };
        var service = new GpdWin5PowerSourceService(() => ec);
        await service.AcquireAsync(default, CancellationToken.None);
        ec.Fail = true;
        await service.RefreshAsync(CancellationToken.None);
        var failed = Assert.Single(service.States);
        Assert.Null(failed.ObservedValue);
        Assert.Null(failed.ObservedAt);
        Assert.Equal(HardwareStateQuality.Unknown, failed.Quality);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, failed.Reason!.Code);
        ec.Fail = false;
        ec.Value = 0;
        await service.RefreshAsync(CancellationToken.None);
        Assert.False(Assert.Single(service.States).ObservedValue!.BooleanValue);
        Assert.Null(Assert.Single(service.States).Reason);
        await service.ReleaseAsync(default, CancellationToken.None);
    }

    [Fact]
    public void OnlyTheExactWin5DefinitionInstantiatesThePowerSourceService()
    {
        foreach (var model in GpdModels.All)
        {
            var services = model.ServiceFactories.Select(factory => factory(null!)).ToArray();
            Assert.Equal(model.Definition.Id == "gpd-win5",
                services.Any(service => service is GpdWin5PowerSourceService));
        }
    }

    private sealed class FakeEc : IEmbeddedController
    {
        internal byte Value { get; set; }
        internal bool Fail { get; set; }
        internal bool Disposed { get; private set; }
        internal List<ushort> ReadAddresses { get; } = [];

        public byte ReadByte(ushort address)
        {
            ReadAddresses.Add(address);
            return Fail ? throw new IOException("Read failed.") : Value;
        }

        public void WriteByte(ushort address, byte value)
        {
            throw new InvalidOperationException("Unexpected write.");
        }

        public ushort ReadUInt16(ushort address)
        {
            throw new InvalidOperationException("Unexpected read.");
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            throw new InvalidOperationException("Unexpected write.");
        }

        public T Transaction<T>(Func<T> operation)
        {
            return operation();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
