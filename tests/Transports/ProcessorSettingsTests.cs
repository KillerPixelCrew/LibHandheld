using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Tests.Transports;

public sealed class ProcessorSettingsTests
{
    [Theory]
    [InlineData("ayaneo.3-hx370", "AMD Ryzen AI 9 HX 370", 100, 2900)]
    [InlineData("ayaneo.3-8840u", "AMD Ryzen 7 8840U", 100, 2700)]
    [InlineData("ayaneo.next-2", "AMD Ryzen AI Max 385", 100, 2800)]
    [InlineData("ayaneo.next-2", "AMD Ryzen AI Max+ 395", 100, 2900)]
    [InlineData("gpd-win5", "AMD Ryzen AI Max 385", 500, 2800)]
    [InlineData("gpd-win5", "AMD Ryzen AI Max+ 395", 500, 2900)]
    public void SharedModelClockBoundsKeepTheirHcProcessorVariant(string id, string processor, int minimum,
        int maximum)
    {
        Assert.Equal((minimum, maximum), ProcessorClockLimits.For(id, processor));
    }

    [Theory]
    [InlineData(1u, 0x55u, 0x54u, 0x64u)]
    [InlineData(13u, 0x55u, 0x54u, 0x64u)]
    [InlineData(22u, 0x55u, 0x54u, 0x64u)]
    [InlineData(10u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(12u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(23u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(24u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(25u, 0x4Cu, 0x4Bu, 0u)]
    [InlineData(30u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(31u, 0x4Cu, 0x4Bu, 0u)]
    [InlineData(32u, 0x4Cu, 0x4Bu, 0u)]
    [InlineData(33u, 0x4Cu, 0x4Bu, 0xB7u)]
    [InlineData(16u, 7u, 6u, 0u)]
    [InlineData(17u, 7u, 6u, 0u)]
    [InlineData(28u, 7u, 6u, 0u)]
    [InlineData(2u, 0u, 0u, 0u)]
    [InlineData(34u, 0u, 0u, 0u)]
    [InlineData(uint.MaxValue, 0u, 0u, 0u)]
    public void CurveCommandsMatchEachReviewedHcProcessor(uint codeName, uint all, uint per, uint gpu)
    {
        Assert.Equal(all, AmdSmuTransport.CurveCommandFor(codeName, AmdCurveKind.AllCores));
        Assert.Equal(per, AmdSmuTransport.CurveCommandFor(codeName, AmdCurveKind.PerCore));
        Assert.Equal(gpu, AmdSmuTransport.CurveCommandFor(codeName, AmdCurveKind.Gpu));
    }

    [Theory]
    [InlineData(2u, 0x1Fu, 0u, 0u)]
    [InlineData(6u, 0x1Fu, 0u, 0u)]
    [InlineData(7u, 0x1Fu, 0u, 0u)]
    [InlineData(15u, 0x1Fu, 0u, 0u)]
    [InlineData(1u, 0x19u, 0u, 0x38u)]
    [InlineData(13u, 0x19u, 0u, 0x38u)]
    [InlineData(22u, 0x19u, 0u, 0x38u)]
    [InlineData(12u, 0x19u, 0u, 0x33u)]
    [InlineData(10u, 0x19u, 0x63u, 0x33u)]
    [InlineData(23u, 0x19u, 0x63u, 0x33u)]
    [InlineData(24u, 0x19u, 0x63u, 0x33u)]
    [InlineData(25u, 0x19u, 0x63u, 0x33u)]
    [InlineData(30u, 0x19u, 0x63u, 0x33u)]
    [InlineData(31u, 0x19u, 0x63u, 0x33u)]
    [InlineData(32u, 0x19u, 0x63u, 0x33u)]
    [InlineData(33u, 0x19u, 0x63u, 0x33u)]
    [InlineData(34u, 0u, 0x63u, 0x33u)]
    [InlineData(16u, 0x3Fu, 0u, 0u)]
    [InlineData(17u, 0x3Fu, 0u, 0u)]
    [InlineData(28u, 0x3Fu, 0u, 0u)]
    [InlineData(uint.MaxValue, 0u, 0u, 0u)]
    public void ThermalCommandsMatchEachReviewedHcProcessor(uint codeName, uint tctl, uint chtc, uint skin)
    {
        Assert.Equal(tctl, AmdSmuTransport.ThermalCommandFor(codeName, AmdThermalLimit.Tctl));
        Assert.Equal(chtc, AmdSmuTransport.ThermalCommandFor(codeName, AmdThermalLimit.Chtc));
        Assert.Equal(skin, AmdSmuTransport.ThermalCommandFor(codeName, AmdThermalLimit.ApuSkin));
    }

    [Theory]
    [InlineData(0, 0x4CL)]
    [InlineData(1, 0x4BL)]
    [InlineData(2, 0xB7L)]
    public void CurveWriteUsesModulePathAndSignedTwentyBitOffset(int curve, long command)
    {
        using var module = new SettingsModule();
        using var smu = new AmdSmuTransport(module, codeName: 23, firmwareVersion: 1);
        smu.WriteCurve((AmdCurveKind)curve, steps: -24);
        var call = Assert.Single(module.Calls);
        Assert.Equal("ioctl_send_smu_command", call.Function);
        Assert.Equal(new long[] { command, 0xFFFE8, 0, 0, 0, 0, 0 }, call.Input);
    }

    [Fact]
    public void CurveAcknowledgementFailureDoesNotRetry()
    {
        using var module = new SettingsModule { WrongEcho = true };
        using var smu = new AmdSmuTransport(module, codeName: 23, firmwareVersion: 1);
        Assert.Throws<IOException>(() => smu.WriteCurve(AmdCurveKind.AllCores, steps: -10));
        Assert.Single(module.Calls);
    }

    [Fact]
    public void SkinTemperatureUsesUnsignedEightBitFraction()
    {
        using var module = new SettingsModule();
        using var smu = new AmdSmuTransport(module, codeName: 23, firmwareVersion: 1);
        smu.WriteThermalLimit(AmdThermalLimit.ApuSkin, celsius: 45);
        Assert.Contains(module.Calls, call => call.Function == "ioctl_write_smu_register"
                                             && call.Input.SequenceEqual([0x03B10998L, 45L * 256]));
        Assert.Contains(module.Calls, call => call.Function == "ioctl_write_smu_register"
                                             && call.Input.SequenceEqual([0x03B10528L, 0x33L]));
    }

    [Fact]
    public void UnsupportedOrInvalidSettingsReachNoHardware()
    {
        using var module = new SettingsModule();
        using var smu = new AmdSmuTransport(module, codeName: 2, firmwareVersion: 1);
        Assert.Throws<NotSupportedException>(() => smu.WriteCurve(AmdCurveKind.Gpu, steps: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => smu.WriteThermalLimit(AmdThermalLimit.Tctl, celsius: 121));
        Assert.Throws<NotSupportedException>(() => smu.ReadThermalLimit(AmdThermalLimit.ApuSkin));
        Assert.Throws<ArgumentOutOfRangeException>(() => smu.WriteGpuClock(megahertz: 0));
        Assert.Empty(module.Calls);
    }

    [Fact]
    public void OlderAmdGpuUsesMinThenMaxOnlyOnce()
    {
        using var module = new SettingsModule();
        using var smu = new AmdSmuTransport(module, codeName: 2, firmwareVersion: 1);
        smu.WriteGpuClock(megahertz: 800);
        Assert.Equal(new long[] { 0x47, 0x46 }, module.Calls.Select(call => call.Input[0]));
        Assert.All(module.Calls, call => Assert.Equal(800, call.Input[1]));
    }

    [Theory]
    [InlineData(0, 0x80000011u)]
    [InlineData(1, 0x80000111u)]
    [InlineData(2, 0x80000211u)]
    [InlineData(3, 0x80000411u)]
    public void IntelRailCommandsUseTheirOwnMailboxField(int rail, uint command)
    {
        Assert.Equal(command, IntelPowerTransport.UndervoltCommandFor((IntelUndervoltRail)rail));
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(-1, 0xFFE00000u)]
    [InlineData(-25, 0xFCC00000u)]
    [InlineData(-50, 0xF9A00000u)]
    [InlineData(50, 0xF9A00000u)]
    public void IntelUndervoltUsesHcRoundedVidPacking(int millivolts, uint expected)
    {
        Assert.Equal(expected, IntelPowerTransport.EncodeUndervolt(millivolts));
    }

    [Fact]
    public void IntelGpuClockPreservesTheAutomaticSentinelAndRejectsTruncation()
    {
        Assert.Equal((byte)24, IntelPowerTransport.EncodeGpuClock(megahertz: 1200));
        Assert.Equal(byte.MaxValue, IntelPowerTransport.EncodeGpuClock(megahertz: 12750));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntelPowerTransport.EncodeGpuClock(megahertz: 1201));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntelPowerTransport.EncodeGpuClock(megahertz: 12800));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntelPowerTransport.EncodeUndervolt(offsetMillivolts: -51));
    }

    private sealed class SettingsModule : IPawnIoModule
    {
        private readonly Dictionary<long, long> _registers = [];
        internal List<(string Function, long[] Input)> Calls { get; } = [];
        internal bool WrongEcho { get; init; }

        public long[] Execute(string function, long[] input, int outputWords)
        {
            Calls.Add((function, [.. input]));
            if (function == "ioctl_send_smu_command")
            {
                return [WrongEcho ? 0 : input[1], 0, 0, 0, 0, 0];
            }

            if (function == "ioctl_write_smu_register")
            {
                _registers[input[0]] = input[1];
                return [];
            }

            if (function == "ioctl_read_smu_register")
            {
                return [input[0] == 0x03B10578 ? 1 : _registers.GetValueOrDefault(input[0])];
            }

            return new long[outputWords];
        }

        public void Dispose()
        {
        }
    }
}
