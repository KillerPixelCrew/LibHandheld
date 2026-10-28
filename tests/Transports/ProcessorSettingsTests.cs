using System.ComponentModel;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Transports;

public sealed class ProcessorSettingsTests
{
    [Theory]
    [InlineData(2u, false)]
    [InlineData(23u, true)]
    public async Task ResetRestoresAutomaticClocksWhereSupportedAndExplainsWhenFirmwareNeedsRestart(uint codeName,
        bool needsRestart)
    {
        using var directory = new TemporaryDirectory();
        using var module = new SettingsModule();
        var service = new ProfiledProcessorService(
            new ProfiledFeatureContext(directory.Root, "bios", new TestPluginHostAdapter(1))
            {
                Journal = TestRecovery.Open(directory.Root)
            }, CpuPowerKind.Amd, minimumGpuClock: 100, maximumGpuClock: 2900,
            openAmd: () => new AmdSmuTransport(module, codeName, firmwareVersion: 1));
        await service.AcquireAsync(new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("bios")), CancellationToken.None);
        var command = new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "processor.gpu-clock", Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Integer(800)
        };
        Assert.Equal(CommandOutcome.Applied,
            (await service.ExecuteAsync(command, "bios", CancellationToken.None)).Outcome);
        if (needsRestart)
        {
            await service.ExecuteAsync(command with
            {
                CapabilityId = "processor.cpu-curve-all", RequestedValue = CapabilityValue.Integer(-24)
            }, "bios", CancellationToken.None);
        }

        module.Calls.Clear();
        var result = await service.ExecuteAsync(command with
        {
            CapabilityId = "processor.reset", RequestedValue = null
        }, "bios", CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Null(service.States.Single(state => state.CapabilityId == "processor.gpu-clock").ObservedValue);
        if (needsRestart)
        {
            Assert.Contains("Restart Windows", result.Reason?.Detail);
            Assert.Equal(0, service.States.Single(state => state.CapabilityId == "processor.cpu-curve-all")
                .ObservedValue!.IntegerValue);
            Assert.Equal(new long[] { 0x4C, 0x4B, 0xB7 }, module.Calls.Select(call => call.Input[0]));
            Assert.All(module.Calls, call => Assert.Equal(0, call.Input[1]));
        }
        else
        {
            Assert.Null(result.Reason);
            Assert.Equal(new long[] { 0x47, 0x46 }, module.Calls.Select(call => call.Input[0]));
            Assert.Equal(new long[] { 100, 2900 }, module.Calls.Select(call => call.Input[1]));
        }

        await service.DisposeAsync();
    }

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

    [Fact]
    public async Task ResetStopsAtTheFirstUncertainWriteWithoutRetryingOrWritingThermalDefaults()
    {
        using var directory = new TemporaryDirectory();
        using var module = new SettingsModule();
        var service = new ProfiledProcessorService(
            new ProfiledFeatureContext(directory.Root, "bios", new TestPluginHostAdapter(1))
            {
                Journal = TestRecovery.Open(directory.Root)
            }, CpuPowerKind.Amd, openAmd: () => new AmdSmuTransport(module, codeName: 23, firmwareVersion: 1));
        await service.AcquireAsync(new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("bios")), CancellationToken.None);
        module.Calls.Clear();
        module.WrongEcho = true;
        var result = await service.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "processor.reset", Deadline = Deadline.Never
        }, "bios", CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Single(module.Calls);
        Assert.Contains("Restart Windows", service.States.Single(state => state.CapabilityId == "processor.reset")
            .Reason?.Detail);
        await service.DisposeAsync();
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
        Assert.Equal(command, IntelMsrSettings.UndervoltCommandFor((IntelUndervoltRail)rail));
    }

    [Theory]
    [InlineData(87, 0xFEu, true)]
    [InlineData(31, 0xFFu, true)]
    [InlineData(1359, 0xFDu, false)]
    [InlineData(170, 0xFCu, false)]
    public void ModuleRefusalsAreDecodedAndOnlyUnknownOrFailedAreDefinite(int win32, uint status, bool definite)
    {
        using var module = new SettingsModule { Refused = { 0x4B }, RefusalCode = win32 };
        using var smu = new AmdSmuTransport(module, codeName: 31, firmwareVersion: 1);
        var refusal = Assert.Throws<SmuCommandException>(() => smu.WriteCurve(AmdCurveKind.PerCore, steps: 0));
        Assert.Equal(status, refusal.Status);
        Assert.Equal(definite, AmdSmuTransport.IsDefiniteRefusal(refusal));
        Assert.Contains("0x4B", refusal.Message);
        Assert.Single(module.Calls);
    }

    [Fact]
    public void ModuleTimeoutStaysUncertainAndOtherFailuresKeepTheirCode()
    {
        using var timeout = new SettingsModule { Refused = { 0x4B }, RefusalCode = 121 };
        using var smu = new AmdSmuTransport(timeout, codeName: 31, firmwareVersion: 1);
        var unknown = Assert.Throws<TimeoutException>(() => smu.WriteCurve(AmdCurveKind.PerCore, steps: 0));
        Assert.False(AmdSmuTransport.IsDefiniteRefusal(unknown));

        using var other = new SettingsModule { Refused = { 0x4B }, RefusalCode = 5 };
        using var second = new AmdSmuTransport(other, codeName: 31, firmwareVersion: 1);
        var failure = Assert.Throws<Win32Exception>(() => second.WriteCurve(AmdCurveKind.PerCore, steps: 0));
        Assert.Equal(5, failure.NativeErrorCode);
        Assert.False(AmdSmuTransport.IsDefiniteRefusal(failure));
    }

    [Fact]
    public async Task StartSettlesWriteOnlyOffsetsAtZeroAndLeavesClocksAndTemperaturesUnknown()
    {
        using var directory = new TemporaryDirectory();
        using var module = new SettingsModule();
        var service = Processor(directory, module, codeName: 31, firmware: "bios");
        await Acquire(service);

        Assert.Equal(new long[] { 0x4C, 0x4B }, module.Calls
            .Where(call => call.Function == "ioctl_send_smu_command").Select(call => call.Input[0]));
        Assert.All(module.Calls.Where(call => call.Function == "ioctl_send_smu_command"),
            call => Assert.Equal(0, call.Input[1]));
        Assert.Equal(0, Observed(service, "processor.cpu-curve-all"));
        Assert.Equal(0, Observed(service, "processor.cpu-curve-core0"));
        Assert.Null(Observed(service, "processor.gpu-clock"));
        Assert.Null(Observed(service, "processor.tctl-limit"));
        await service.DisposeAsync();
    }

    [Fact]
    public async Task RefusedSettingIsHiddenFromStartAndRememberedUntilTheFirmwareChanges()
    {
        using var directory = new TemporaryDirectory();
        using (var refusing = new SettingsModule { Refused = { 0x4B } })
        {
            var first = Processor(directory, refusing, codeName: 31, firmware: "bios-1");
            await Acquire(first);
            Assert.DoesNotContain(first.Descriptors, d => d.CapabilityId == "processor.cpu-curve-core0");
            Assert.Contains(first.Descriptors, d => d.CapabilityId == "processor.cpu-curve-all");
            Assert.True(File.Exists(Path.Combine(directory.Root, UnsupportedSettings.FileName)));
            await first.DisposeAsync();
        }

        using (var same = new SettingsModule())
        {
            var second = Processor(directory, same, codeName: 31, firmware: "bios-1");
            await Acquire(second);
            Assert.DoesNotContain(second.Descriptors, d => d.CapabilityId == "processor.cpu-curve-core0");
            Assert.DoesNotContain(same.Calls,
                call => call.Function == "ioctl_send_smu_command" && call.Input[0] == 0x4B);
            await second.DisposeAsync();
        }

        using var updated = new SettingsModule();
        var third = Processor(directory, updated, codeName: 31, firmware: "bios-2");
        await Acquire(third);
        Assert.Contains(third.Descriptors, d => d.CapabilityId == "processor.cpu-curve-core0");
        Assert.Contains(updated.Calls, call => call.Function == "ioctl_send_smu_command" && call.Input[0] == 0x4B);
        await third.DisposeAsync();
    }

    [Fact]
    public async Task DefinitelyRefusedUserWriteStaysOnScreenAndIsLeftOutFromTheNextStart()
    {
        using var directory = new TemporaryDirectory();
        using var module = new SettingsModule();
        var service = Processor(directory, module, codeName: 31, firmware: "bios");
        await Acquire(service);
        service.AcknowledgeDescriptors();
        module.Refused.Add(0x89);
        module.RefusalCode = 31;

        var result = await service.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "processor.gpu-clock", Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Integer(1200)
        }, "bios", CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason?.Code);
        Assert.False(service.DescriptorsChanged);
        Assert.Contains(service.Descriptors, d => d.CapabilityId == "processor.gpu-clock");
        Assert.Single(module.Calls, call => call.Function == "ioctl_send_smu_command" && call.Input[0] == 0x89);

        // A wake re-acquires the same instance: the control is still there and nothing is settled again.
        module.Calls.Clear();
        await Acquire(service);
        Assert.Contains(service.Descriptors, d => d.CapabilityId == "processor.gpu-clock");
        Assert.DoesNotContain(module.Calls, call => call.Function == "ioctl_send_smu_command");
        await service.DisposeAsync();

        using var next = new SettingsModule();
        var restarted = Processor(directory, next, codeName: 31, firmware: "bios");
        await Acquire(restarted);
        Assert.DoesNotContain(restarted.Descriptors, d => d.CapabilityId == "processor.gpu-clock");
        await restarted.DisposeAsync();
    }

    [Fact]
    public async Task ResetPassesOverADefinitelyRefusedSetting()
    {
        using var directory = new TemporaryDirectory();
        using var module = new SettingsModule();
        var service = Processor(directory, module, codeName: 31, firmware: "bios");
        await Acquire(service);
        module.Calls.Clear();
        module.Refused.Add(0x4C);

        var result = await service.ExecuteAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "processor.reset", Deadline = Deadline.Never
        }, "bios", CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Contains(module.Calls, call => call.Function == "ioctl_send_smu_command" && call.Input[0] == 0x4B);
        Assert.Contains(service.Descriptors, d => d.CapabilityId == "processor.cpu-curve-all");
        await service.DisposeAsync();
    }

    private static ProfiledProcessorService Processor(TemporaryDirectory directory, SettingsModule module,
        uint codeName, string firmware)
    {
        return new ProfiledProcessorService(
            new ProfiledFeatureContext(directory.Root, firmware, new TestPluginHostAdapter(1))
            {
                Journal = TestRecovery.Open(directory.Root)
            }, CpuPowerKind.Amd, minimumGpuClock: 100, maximumGpuClock: 2900,
            openAmd: () => new AmdSmuTransport(module, codeName, firmwareVersion: 1));
    }

    private static async Task Acquire(ProfiledProcessorService service)
    {
        await service.AcquireAsync(new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("bios")), CancellationToken.None);
    }

    private static long? Observed(ProfiledProcessorService service, string id)
    {
        return service.States.Single(state => state.CapabilityId == id).ObservedValue?.IntegerValue;
    }

    private sealed class SettingsModule : IPawnIoModule
    {
        private readonly Dictionary<long, long> _registers = [];
        internal List<(string Function, long[] Input)> Calls { get; } = [];
        internal bool WrongEcho { get; set; }

        /// <summary>SMU commands this firmware answers with <see cref="RefusalCode" />.</summary>
        internal HashSet<long> Refused { get; } = [];

        internal int RefusalCode { get; set; } = 87;

        public long[] Execute(string function, long[] input, int outputWords)
        {
            Calls.Add((function, [.. input]));
            if (function == "ioctl_send_smu_command")
            {
                if (Refused.Contains(input[0]))
                {
                    throw new Win32Exception(RefusalCode, $"PawnIO {function} failed.");
                }

                return [WrongEcho ? input[1] ^ 1 : input[1], 0, 0, 0, 0, 0];
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
