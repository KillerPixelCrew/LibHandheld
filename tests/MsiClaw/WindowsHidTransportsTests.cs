using LibHandheld.Internal.Lifecycle;

namespace LibHandheld.Families.MsiClaw.Tests;

[Collection("plugin-trace")]
public sealed class WindowsHidTransportsTests
{
    [Fact]
    public async Task DisposalLetsTheCurrentOwnerReleaseAndWaitersRejectWithoutOpeningHardware()
    {
        WindowsClawMcuTransport transport = new();
        // Hold the same gate as a transport operation, before any native endpoint is opened.
        var gate = transport.Serializer;
        await gate.WaitAsync();
        Task read = transport.ReadProfileAsync(0, 1, CancellationToken.None).AsTask();
        var write = transport.WriteProfileAsync(0, new byte[1], CancellationToken.None).AsTask();
        Task mode = transport.SwitchModeAsync(ClawControllerMode.XInput, "test-location",
            Deadline.After(TimeSpan.FromSeconds(5)), CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);
        Assert.False(write.IsCompleted);
        Assert.False(mode.IsCompleted);

        await transport.DisposeAsync();
        gate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mode.WaitAsync(TimeSpan.FromSeconds(2)));
        await transport.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void RumblePayloadPadsToTheAdvertisedHidOutputLength()
    {
        var report = ClawControllerCodec.EncodeRumble(0x22, 0x44, 64);

        Assert.Equal(64, report.Length);
        Assert.Equal(0x05, report[0]);
        Assert.Equal(0x01, report[1]);
        Assert.Equal(0x22, report[4]);
        Assert.Equal(0x44, report[5]);
        Assert.All(report[6..], value => Assert.Equal(0, value));
    }
}
