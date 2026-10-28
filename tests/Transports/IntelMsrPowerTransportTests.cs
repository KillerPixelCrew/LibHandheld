using LibHandheld.Internal.Transports;

namespace LibHandheld.Tests.Transports;

public sealed class IntelMsrPowerTransportTests
{
    [Theory]
    [InlineData(0xA0E03UL, 0.125)]
    [InlineData(0xA0E0EUL, 1.0 / 16384)]
    [InlineData(0xFFFFFFF0UL, 1.0)]
    public void PowerUnitComesFromTheLowNibbleOfRaplPowerUnit(ulong raw, double watts)
    {
        Assert.Equal(watts, IntelMsrPowerTransport.UnitWatts(raw));
    }

    [Fact]
    public void ReadDecodesPl1AndReportsPl2AsSlowAndBoost()
    {
        using var transport = new IntelMsrPowerTransport(new Module(0x00438168_00DD8078UL));
        Assert.Equal(new CpuPowerLimits(15, 45, 45), transport.ReadLimits());
    }

    [Fact]
    public void WriteSendsHandheldCompanionsWholeRegisterOnce()
    {
        var module = new Module(0x00C21234_00DE1234UL);
        using var transport = new IntelMsrPowerTransport(module);
        transport.WriteLimits(new CpuPowerLimits(15, 28, 28));

        // HC's set_msr_limits writes 0x004380E0 0x00DD8078 for 15/28 W at 1/8 W units.
        var write = Assert.Single(module.Calls, call => call.Function == "ioctl_write_msr");
        Assert.Equal([0x610L, unchecked((long)0x004380E0_00DD8078UL)], write.Input);
        Assert.Equal(2, module.Calls.Count(call => call.Function == "ioctl_read_msr"));
    }

    [Fact]
    public void LockedRegisterRefusesTheTransportAtStart()
    {
        var module = new Module(0x80438168_00DD8078UL);
        var refusal = Assert.Throws<InvalidOperationException>(() => new IntelMsrPowerTransport(module));
        Assert.Contains("locked", refusal.Message);
        Assert.DoesNotContain(module.Calls, call => call.Function == "ioctl_write_msr");
    }

    [Fact]
    public void InvalidRequestsReachNoHardwareBeyondTheStartReads()
    {
        var module = new Module(0x00438168_00DD8078UL);
        using var transport = new IntelMsrPowerTransport(module);
        Assert.Throws<ArgumentException>(() => transport.WriteLimits(new CpuPowerLimits(15, 20, 28)));
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.WriteLimits(new CpuPowerLimits(0, 28, 28)));
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.WriteLimits(new CpuPowerLimits(15, 5000, 5000)));
        Assert.Equal([("ioctl_read_msr", 0x606L), ("ioctl_read_msr", 0x610L)],
            module.Calls.Select(call => (call.Function, call.Input[0])));
    }

    [Fact]
    public void DisposeReleasesTheModuleAndRefusesLaterCalls()
    {
        var module = new Module(0);
        var transport = new IntelMsrPowerTransport(module);
        transport.Dispose();
        Assert.True(module.Disposed);
        Assert.Throws<ObjectDisposedException>(() => transport.ReadLimits());
    }

    private sealed class Module(ulong packagePowerLimit) : IPawnIoModule
    {
        internal List<(string Function, long[] Input)> Calls { get; } = [];
        internal bool Disposed { get; private set; }

        public long[] Execute(string function, long[] input, int outputWords)
        {
            Calls.Add((function, [.. input]));
            if (function == "ioctl_read_msr")
            {
                Assert.Equal(1, outputWords);
                return input[0] switch
                {
                    0x606 => [0xA0E03],
                    0x610 => [unchecked((long)packagePowerLimit)],
                    _ => throw new InvalidOperationException($"Unexpected MSR 0x{input[0]:X}.")
                };
            }

            Assert.Equal("ioctl_write_msr", function);
            Assert.Equal(0, outputWords);
            return [];
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
