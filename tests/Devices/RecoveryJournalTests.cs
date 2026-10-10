using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

[Collection("plugin-trace")]
public sealed class RecoveryJournalTests
{
    [Fact]
    public async Task FirstOriginalIsCapturedOnceAndSurvivesReopening()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var captures = 0;
        await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(15);
        }, CancellationToken.None);
        await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(25);
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        var reopened = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        Assert.Equal(15, reopened.OriginalStateFor<Original>("power")?.Value);
    }

    [Fact]
    public async Task SaveFailurePreservesOriginalWithoutBlockingTheNextCapture()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "recovery.json"));
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        await journal.BeginAsync("power", "bios1", new Original(15), CancellationToken.None);
        await journal.BeginAsync("fan", "bios1", new Original(30), CancellationToken.None);
        Assert.Equal(15, journal.OriginalStateFor<Original>("power")?.Value);
        Assert.Equal(30, journal.OriginalStateFor<Original>("fan")?.Value);
    }

    [Fact]
    public async Task MalformedFileLeavesCaptureAvailable()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Root, "recovery.json"), "{invalid}");
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        await journal.BeginAsync("power", "bios1", new Original(15), CancellationToken.None);
        Assert.Equal(15, journal.OriginalStateFor<Original>("power")?.Value);
    }

    [Fact]
    public async Task AcceptedRestoreRetiresOriginalAndFailedRestoreRetainsIt()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        await journal.BeginAsync("power", "bios1", new Original(15), CancellationToken.None);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None);
        Assert.Equal(15, journal.PendingOriginalFor<Original>("power")?.Value);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None);
        Assert.Null(journal.OriginalStateFor<Original>("power"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCaptureIsNotRetriedAfterOurWriteAndTheNextCycleCanCapture(bool throws)
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var captures = 0;
        var hardwareValue = 15;
        Assert.Null(await journal.BeginAsync<Original>("power", "bios1", () =>
        {
            captures++;
            return throws ? throw new IOException("Original unavailable") : null;
        }, CancellationToken.None));

        // The family dispatches a supported write even though its original could not be captured.
        hardwareValue = 25;
        Assert.Null(await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(hardwareValue);
        }, CancellationToken.None));
        Assert.Equal(1, captures);
        Assert.Null(journal.OriginalStateFor<Original>("power"));

        await journal.BeginCycleAsync(CancellationToken.None);
        var next = await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(hardwareValue);
        }, CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal(2, captures);
        Assert.Equal(25, next.Entry.OriginalState.Value);
    }

    [Fact]
    public async Task BeginningTheNextCyclePreservesAnOutstandingOriginal()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        await journal.BeginAsync("power", "bios1", new Original(15), CancellationToken.None);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None);
        await journal.BeginCycleAsync(CancellationToken.None);
        var captures = 0;
        var pending = await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(25);
        }, CancellationToken.None);
        Assert.Equal(0, captures);
        Assert.Equal(15, pending!.Entry.OriginalState.Value);
        Assert.Equal(15, journal.PendingOriginalFor<Original>("power")!.Value);
    }

    [Fact]
    public async Task AnotherWriteAfterAnAcceptedRestoreReusesTheCycleOriginal()
    {
        using var directory = new TemporaryDirectory();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var captures = 0;
        await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(15);
        }, CancellationToken.None);
        await journal.SetStatusAsync("power", DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None);
        var reenlisted = await journal.BeginAsync("power", "bios1", () =>
        {
            captures++;
            return new Original(25);
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(15, reenlisted!.Entry.OriginalState.Value);
        Assert.Equal(15, journal.PendingOriginalFor<Original>("power")!.Value);
    }

    private sealed record Original(int Value);
}
