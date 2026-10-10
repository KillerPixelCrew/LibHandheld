using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Generic;

public sealed class AynControlStateTests
{
    [Fact]
    public async Task RefreshDoesNotQueryFanSettingsAndWritesRemainUsableWithoutOriginals()
    {
        using var directory = new TemporaryDirectory();
        var ec = new WriteOnlyEc();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var service = new AynNativeService(
            new ProfiledFeatureContext(directory.Root, "firmware", new TestPluginHostAdapter(1))
            {
                Journal = journal
            }, () => ec);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("firmware"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, ec.Reads);
        Assert.All(service.States, state =>
        {
            Assert.True(state.Available);
            Assert.Null(state.ObservedValue);
        });

        var command = new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "fan.duty", Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Integer(55)
        };
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(command, "firmware", CancellationToken.None)).Outcome);
        Assert.Equal(1, ec.Reads);
        Assert.Equal(55, service.States.Single(state => state.CapabilityId == "fan.duty")
            .ObservedValue!.IntegerValue);

        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(command with { RequestedValue = CapabilityValue.Integer(65) }, "firmware",
                CancellationToken.None)).Outcome);
        Assert.Equal(1, ec.Reads);
        Assert.Equal(65, service.States.Single(state => state.CapabilityId == "fan.duty")
            .ObservedValue!.IntegerValue);
        Assert.Equal("custom", service.States.Single(state => state.CapabilityId == CapabilityIds.FanMode)
            .ObservedValue!.ChoiceValue);
        Assert.Equal(4, ec.Writes.Count);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(1, ec.Reads);
    }

    private sealed class WriteOnlyEc : IEmbeddedController
    {
        internal int Reads { get; private set; }
        internal List<(ushort Address, byte Value)> Writes { get; } = [];

        public byte ReadByte(ushort address)
        {
            Reads++;
            throw new NotSupportedException("The original cannot be captured.");
        }

        public void WriteByte(ushort address, byte value)
        {
            Writes.Add((address, value));
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
