using LibHandheld.Families.Asus;
using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Generic;

public sealed class GenericAsusControlStateTests
{
    [Fact]
    public void XgDisableResetsAutomaticFanBeforeAcpiAndReleaseDoesNotRepeatIt()
    {
        var acpi = new WriteOnlyAcpi();
        List<byte[]> reports = [];
        using var transport = new AsusXgTransport(acpi, [], report =>
        {
            Assert.Empty(acpi.Writes);
            reports.Add(report);
        });
        transport.Fan(AsusAcpiProtocol.FlatFanCurve(55));
        reports.Clear();
        transport.Enable(false);
        transport.ReleaseFan();
        Assert.Equal(new byte[] { 0x5E, 0xD1, 2 }, Assert.Single(reports));
        Assert.Equal((AsusAcpiId.XgEnabled, 0U), Assert.Single(acpi.Writes));
    }

    [Fact]
    public void XgReleaseResetsCustomFanOnceAndNeverRepeatsAnUncertainReset()
    {
        var acpi = new WriteOnlyAcpi();
        var resets = 0;
        using var transport = new AsusXgTransport(acpi, [], report =>
        {
            if (report.SequenceEqual(new byte[] { 0x5E, 0xD1, 2 }))
            {
                resets++;
                throw new IOException("Reset result unknown");
            }
        });
        transport.Fan(AsusAcpiProtocol.FlatFanCurve(55));
        Assert.Throws<IOException>(transport.ReleaseFan);
        transport.ReleaseFan();
        Assert.Equal(1, resets);
        Assert.Empty(acpi.Writes);
    }

    [Fact]
    public async Task RefreshNeverQueriesControlStateAndFailedRestoreCaptureDoesNotGateModeWrites()
    {
        using var directory = new TemporaryDirectory();
        var acpi = new WriteOnlyAcpi();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var service = new GenericAsusService(
            new ProfiledFeatureContext(directory.Root, "firmware", new TestPluginHostAdapter(1))
            {
                Journal = journal
            }, openAcpi: () => acpi);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("firmware"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, acpi.Reads);
        Assert.All(service.States, state =>
        {
            Assert.True(state.Available);
            Assert.Null(state.ObservedValue);
        });

        var command = new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = CapabilityIds.Scenario, Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Choice("silent")
        };
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(command, "firmware", CancellationToken.None)).Outcome);
        var observed = service.States.Single(state => state.CapabilityId == CapabilityIds.Scenario);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(observed, service.States.Single(state => state.CapabilityId == CapabilityIds.Scenario));
        Assert.Equal(1, acpi.Reads);

        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(command with { RequestedValue = CapabilityValue.Choice("turbo") }, "firmware",
                CancellationToken.None)).Outcome);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("turbo", service.States.Single(state => state.CapabilityId == CapabilityIds.Scenario)
            .ObservedValue!.ChoiceValue);
        Assert.Equal(2, acpi.Writes.Count);
        Assert.Equal(1, acpi.Reads);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(1, acpi.Reads);
    }

    private sealed class WriteOnlyAcpi : IAsusAcpi
    {
        internal int Reads { get; private set; }
        internal List<(AsusAcpiId Id, uint Value)> Writes { get; } = [];

        public bool TryOpen()
        {
            return true;
        }

        public uint ReadStatus(AsusAcpiId id, uint selector = 0)
        {
            Reads++;
            throw new NotSupportedException("The original cannot be captured.");
        }

        public byte[] ReadBuffer(AsusAcpiId id, uint selector)
        {
            Reads++;
            throw new NotSupportedException("The original cannot be captured.");
        }

        public uint Write(AsusAcpiId id, uint value)
        {
            Writes.Add((id, value));
            return 0;
        }

        public uint WriteBuffer(AsusAcpiId id, ReadOnlySpan<byte> data)
        {
            return 0;
        }

        public void Dispose()
        {
        }
    }
}
