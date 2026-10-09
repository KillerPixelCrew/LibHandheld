using LibHandheld.Internal.Transports;

namespace LibHandheld.Tests.Transports;

public sealed class PrivilegedTransportTests
{
    [Fact]
    public void IndexedWordWritesLittleEndianAndAddressesEachByte()
    {
        using var module = new RecordingModule();
        using var ec = new PawnIoEmbeddedController(module, true);
        ec.WriteUInt16(0xC6C8, 2500);
        var portWrites = module.Calls.Where(call => call.Function == "ioctl_pio_outb")
            .Select(call => (Port: call.Input[0], Value: call.Input[1])).ToArray();
        Assert.Contains((0x4FL, 0xC6L), portWrites);
        Assert.Contains((0x4FL, 0xC8L), portWrites);
        Assert.Contains((0x4FL, 0xC9L), portWrites);
        Assert.Contains((0x4FL, 0xC4L), portWrites);
        Assert.Contains((0x4FL, 0x09L), portWrites);
        Assert.Equal(2, module.Calls.Count(call => call.Function == "ioctl_select_slot"));
    }

    [Fact]
    public void IndexedWordReadCombinesAdjacentBytes()
    {
        using var module = new RecordingModule();
        module.Reads.Enqueue(0xC4);
        module.Reads.Enqueue(0x09);
        using var ec = new PawnIoEmbeddedController(module, true);
        Assert.Equal(2500, ec.ReadUInt16(0xC6C0));
    }

    [Fact]
    public void AcpiReadSendsReadCommandAndAddress()
    {
        using var module = new RecordingModule();
        module.Reads.Enqueue(0x42);
        using var ec = new PawnIoEmbeddedController(module, false);
        Assert.Equal(0x42, ec.ReadByte(0xA5));
        Assert.Contains(module.Calls,
            call => call.Function == "ioctl_pio_write" && call.Input.SequenceEqual([0x66L, 0x80L]));
        Assert.Contains(module.Calls,
            call => call.Function == "ioctl_pio_write" && call.Input.SequenceEqual([0x62L, 0xA5L]));
    }

    [Fact]
    public void BankedReadRestoresOriginalBank()
    {
        using var module = new RecordingModule();
        module.Reads.Enqueue(7);
        module.Reads.Enqueue(0x42);
        using var ec = new PawnIoEmbeddedController(module, false);
        Assert.Equal(0x42, ec.ReadByte(0x02A5));
        var data = module.Calls.Where(call => call.Function == "ioctl_pio_write" && call.Input[0] == 0x62)
            .Select(call => call.Input[1]).ToArray();
        Assert.Equal(new long[] { 0xFF, 0xFF, 2, 0xA5, 0xFF, 7 }, data);
    }

    [Fact]
    public void DisposedSessionRejectsNewWork()
    {
        var module = new RecordingModule();
        var ec = new PawnIoEmbeddedController(module, true);
        ec.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ec.ReadByte(0xC6C0));
        Assert.Empty(module.Calls);
        Assert.True(module.Disposed);
    }

    [Fact]
    public async Task DisposalWaitsForTheNativeTransaction()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var disposing = new ManualResetEventSlim(false);
        using var module = new RecordingModule { Entered = entered, Release = release };
        using var ec = new PawnIoEmbeddedController(module, true);
        var write = Task.Run(() => ec.WriteByte(0xC6C8, 5));
        Task? retirement = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            retirement = Task.Run(() =>
            {
                disposing.Set();
                ec.Dispose();
            });
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(module.Disposed);
        }
        finally
        {
            release.Set();
            await write;
            if (retirement is not null)
            {
                await retirement;
            }
        }

        Assert.True(module.Disposed);
    }

    [Theory]
    [InlineData(10, 0x03B10528u, 0x03B10578u)]
    [InlineData(31, 0x03B10928u, 0x03B10978u)]
    [InlineData(28, 0x03B10530u, 0x03B1057Cu)]
    [InlineData(1, 0x03B10528u, 0x03B10564u)]
    public void AmdMailboxUsesProcessorFamily(int codeName, uint command, uint response)
    {
        var mailbox = AmdSmuTransport.MailboxFor((uint)codeName);
        Assert.Equal(command, mailbox.Command);
        Assert.Equal(response, mailbox.Response);
    }

    [Fact]
    public void RejectedSmuCommandIsNotRetried()
    {
        using var module = new RecordingModule { SmuStatus = 0xFD };
        using var smu = new AmdSmuTransport(module, 23, 1);
        var error = Assert.Throws<SmuCommandException>(() => smu.WriteLimit(CpuPowerLimit.Sustained, 15));
        Assert.Equal(0xFDu, error.Status);
        Assert.Single(module.Calls, call => call.Function == "ioctl_write_smu_register" && call.Input[0] == 0x03B10528);
    }

    [Fact]
    public void InvalidPowerRequestDoesNotReachHardware()
    {
        using var module = new RecordingModule();
        using var smu = new AmdSmuTransport(module, 23, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => smu.WriteLimits(new CpuPowerLimits(15, double.NaN, 20)));
        Assert.Empty(module.Calls);
        Assert.Equal(15000u, CpuPowerEncoding.Milliwatts(15));
        Assert.Equal(120, CpuPowerEncoding.IntelUnits(15, 0.125));
    }

    [Fact]
    public void IntelParsersRetainFullWidthValues()
    {
        Assert.Equal(0xFEDC0000L, IntelPowerTransport.ParseReturn("Return 4275830784\r\n"));
        Assert.Equal(0x0043810000DD8078UL, IntelPowerTransport.ParseMsr("Msr Data     : 0x00438100 0x00DD8078\r\n"));
        Assert.Null(IntelPowerTransport.ParseReturn("not a response"));
    }

    private sealed class RecordingModule : IPawnIoModule
    {
        private bool _submitted;
        internal List<(string Function, long[] Input)> Calls { get; } = [];
        internal Queue<long> Reads { get; } = new();
        internal uint SmuStatus { get; init; } = 1;
        internal bool Disposed { get; private set; }
        internal ManualResetEventSlim? Entered { get; init; }
        internal ManualResetEventSlim? Release { get; init; }

        public long[] Execute(string function, long[] input, int outputWords)
        {
            Entered?.Set();
            Release?.Wait();
            Calls.Add((function, [.. input]));
            if (function == "ioctl_pio_read" && input[0] == 0x66)
            {
                return [1];
            }

            if (function is "ioctl_pio_inb" or "ioctl_pio_read")
            {
                return [Reads.Dequeue()];
            }

            if (function == "ioctl_write_smu_register" && input[0] == 0x03B10528)
            {
                _submitted = true;
            }

            if (function == "ioctl_read_smu_register")
            {
                return [_submitted ? SmuStatus : 1];
            }

            return new long[outputWords];
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
