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
    public void WriteReplacesOnlyThePowerFieldsEnablesBothAndIsSentOnce()
    {
        var module = new Module(0x00C21234_00DE1234UL);
        using var transport = new IntelMsrPowerTransport(module);
        transport.WriteLimits(new CpuPowerLimits(15, 28, 28));

        var write = Assert.Single(module.Calls, call => call.Function == "ioctl_write_msr");
        Assert.Equal([0x610L, unchecked((long)0x00C280E0_00DE8078UL)], write.Input);
    }

    [Fact]
    public void ComposeKeepsEveryOtherBit()
    {
        Assert.Equal(0x7FFF8002_FFFF8001UL,
            IntelMsrPowerTransport.Compose(0x7FFFFFFF_FFFFFFFFUL, sustainedUnits: 1, boostUnits: 2));
        Assert.Equal(0x00008002_00008001UL, IntelMsrPowerTransport.Compose(current: 0, sustainedUnits: 1, boostUnits: 2));
    }

    [Fact]
    public void LockedRegisterIsRefusedWithoutWriting()
    {
        var module = new Module(0x80438168_00DD8078UL);
        using var transport = new IntelMsrPowerTransport(module);
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            transport.WriteLimits(new CpuPowerLimits(15, 28, 28)));
        Assert.Contains("locked", refusal.Message);
        Assert.DoesNotContain(module.Calls, call => call.Function == "ioctl_write_msr");
    }

    [Fact]
    public void InvalidRequestsReachNoHardwareBeyondTheUnitRead()
    {
        var module = new Module(0x00438168_00DD8078UL);
        using var transport = new IntelMsrPowerTransport(module);
        Assert.Throws<ArgumentException>(() => transport.WriteLimits(new CpuPowerLimits(15, 20, 28)));
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.WriteLimits(new CpuPowerLimits(0, 28, 28)));
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.WriteLimits(new CpuPowerLimits(15, 5000, 5000)));
        var only = Assert.Single(module.Calls);
        Assert.Equal(("ioctl_read_msr", 0x606L), (only.Function, only.Input[0]));
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
