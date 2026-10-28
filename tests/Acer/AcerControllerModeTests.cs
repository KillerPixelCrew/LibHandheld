using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Acer;

public sealed class AcerControllerModeTests
{
    [Fact]
    public async Task CancelledAcquisitionDoesNotOpenHardware()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var opened = false;
        var service = new AcerControllerModeService(context, () =>
        {
            opened = true;
            return new FixtureMode();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.AcquireAsync(Cycle(context), new CancellationToken(canceled: true)));
        Assert.False(opened);
    }

    [Fact]
    public async Task AcquisitionJournalsMacroModeAndReleaseRestoresIt()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var transport = new FixtureMode();
        var service = new AcerControllerModeService(context, () => transport);
        var cycle = Cycle(context);
        Assert.True(service.RequiresControllerOwnership);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(new AcerControllerMode(Gamepad: 1, RearKeys: 1), transport.Mode);
        Assert.True(context.Journal.HasUnrestoredMutation(service.ServiceId));
        Assert.Equal(new AcerControllerMode(Gamepad: 1, RearKeys: 0),
            context.Journal.OriginalStateFor<AcerControllerMode>(service.ServiceId));

        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.Idle, release.State);
        Assert.Equal(new AcerControllerMode(Gamepad: 1, RearKeys: 0), transport.Mode);
        Assert.Equal(2, transport.Writes);
        Assert.Empty(context.Journal.OutstandingEntries);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task AnAlreadyManagedControllerNeedsNoWriteOrRestoration()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var transport = new FixtureMode { Mode = new AcerControllerMode(Gamepad: 1, RearKeys: 1) };
        var service = new AcerControllerModeService(context, () => transport);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(0, transport.Writes);
        Assert.Empty(context.Journal.OutstandingEntries);
    }

    [Fact]
    public async Task AnUncertainAcquisitionIsNotRepeatedAndItsOriginalRemainsRecoverable()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var transport = new FixtureMode { LoseWriteCompletion = true };
        var service = new AcerControllerModeService(context, () => transport);
        var cycle = Cycle(context);
        await Assert.ThrowsAsync<IOException>(async () => await service.AcquireAsync(cycle, CancellationToken.None));
        Assert.Equal(1, transport.Writes);
        Assert.True(context.Journal.HasUnrestoredMutation(service.ServiceId));
        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.Idle, release.State);
        Assert.Equal(2, transport.Writes);
        Assert.Equal(new AcerControllerMode(Gamepad: 1, RearKeys: 0), transport.Mode);
    }

    [Fact]
    public async Task AFailedRestoreReadbackIsNotReportedCleanOrRetried()
    {
        using var directory = new TemporaryDirectory();
        var context = Context(directory.Root);
        var transport = new FixtureMode();
        var service = new AcerControllerModeService(context, () => transport);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        transport.IgnoreWrites = true;
        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.ReleasedUnverified, release.State);
        Assert.True(context.Journal.HasUnrestoredMutation(service.ServiceId));
        Assert.Equal(2, transport.Writes);
    }

    [Fact]
    public void ModePacketsDoNotContainProfileOrOtherFeatureWrites()
    {
        Assert.Equal("0F00003C26", Convert.ToHexString(AcerControllerTransport.ModePacket().AsSpan(0, 5)));
        var packet = AcerControllerTransport.ModePacket(new AcerControllerMode(Gamepad: 1, RearKeys: 1));
        Assert.Equal(64, packet.Length);
        Assert.Equal("0F00003C240101", Convert.ToHexString(packet.AsSpan(0, 7)));
        Assert.All(packet.Skip(7), item => Assert.Equal(0, item));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerControllerTransport.ModePacket(
            new AcerControllerMode(Gamepad: 7, RearKeys: 0)));
    }

    [Fact]
    public void MeasuredRearCombinationEdgesStayHeldAcrossUnrelatedReports()
    {
        var edges = new List<(string Id, bool Down)>();
        using var state = new AcerButtonState((id, _, down) => edges.Add((id, down)));
        foreach (var hex in new[]
                 {
                     "9A0200000000000000000000000000", "9A0600000000000000000000000000",
                     "9B00000080FF7F0080FF7F00000000", "9A0200000000000000000000000000",
                     "9A0000000000000000000000000000"
                 })
        {
            state.HandleVendorReport(Convert.FromHexString(hex));
        }

        Assert.Equal(new[] { ("m1", true), ("m2", true), ("m2", false), ("m1", false) }, edges);
    }

    private static ProfiledFeatureContext Context(string root)
    {
        return new ProfiledFeatureContext(root, "atlas|V1.03", new TestPluginHostAdapter(1))
        {
            Journal = TestRecovery.Open(root)
        };
    }

    private static DeviceCycleContext<ProfiledCycleIdentity> Cycle(ProfiledFeatureContext context)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity(context.Firmware));
    }

    private sealed class FixtureMode : IAcerControllerModeTransport
    {
        public AcerControllerMode Mode { get; set; } = new(Gamepad: 1, RearKeys: 0);
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public bool LoseWriteCompletion { get; set; }
        public bool IgnoreWrites { get; set; }

        public ValueTask<AcerControllerMode> ReadModeAsync(CancellationToken token) => ValueTask.FromResult(Mode);

        public ValueTask WriteModeAsync(AcerControllerMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes++;
            if (!IgnoreWrites)
            {
                Mode = mode;
            }

            if (LoseWriteCompletion)
            {
                LoseWriteCompletion = false;
                throw new IOException("Lost completion after dispatch.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
