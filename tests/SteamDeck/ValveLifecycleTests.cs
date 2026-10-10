using LibHandheld.Families.SteamDeck;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.SteamDeck;

public sealed class ValveLifecycleTests
{
    [Fact]
    public async Task RuntimeControllerToggleRetiresReaderAndRestoresDesktopMappingsAndCapturedGyroMode()
    {
        using var directory = new TemporaryDirectory();
        var host = new TestPluginHostAdapter(1);
        var controllers = new List<NeptuneFake>();
        var context = new ProfiledFeatureContext(directory.Root, "steamdeck-lcd|bios", host)
            { Journal = TestRecovery.Open(directory.Root), ControllerManagementEnabled = true };
        var service = new SteamDeckNativeService(context,
            () =>
            {
                var pad = new NeptuneFake();
                controllers.Add(pad);
                return pad;
            },
            () => new MemoryFake(), () => new VangoghPower(new MemoryFake(), 0x80300000));
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("steamdeck-lcd|bios"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.True(service.Connected);
        Assert.True(controllers[0].ManagedMappings);
        Assert.Equal((ushort)7, controllers[0].Registers[7]);
        Assert.Equal((ushort)7, controllers[0].Registers[8]);
        Assert.Equal((ushort)1, controllers[0].Registers[9]);
        Assert.Equal((ushort)0x18, controllers[0].Registers[48]);
        controllers[0].SettingReads.Clear();
        await service.SetControllerManagementAsync(false, CancellationToken.None);
        Assert.Empty(controllers[0].SettingReads);
        Assert.False(service.Connected);
        Assert.True(controllers[0].Disposed);
        Assert.True(controllers[0].Stopped);
        Assert.False(controllers[0].ManagedMappings);
        Assert.True(controllers[0].MappingChanges[0]);
        Assert.False(controllers[0].MappingChanges[^1]);
        Assert.Single(controllers[0].MappingChanges, managed => !managed);
        Assert.Equal((ushort)1, controllers[0].Registers[9]);
        Assert.Equal((ushort)1, controllers[0].Registers[71]);
        Assert.Equal((ushort)0x1C, controllers[0].Registers[48]);
        Assert.Contains(controllers[0].Output, frame => frame.IsSilent);
        Assert.Empty(host.PhysicalDeviceSets.Last());
        await service.SetControllerManagementAsync(true, CancellationToken.None);
        Assert.True(service.Connected);
        Assert.Equal(2, controllers.Count);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.True(controllers[1].Disposed);
        Assert.All(host.ControllerSamples, sample => Assert.Equal(CanonicalButtons.None, sample.Buttons));
    }

    [Fact]
    public async Task DeckRefreshReadsSensorsOnlyAndRestoreDoesNotVerifyControlWrites()
    {
        using var directory = new TemporaryDirectory();
        var memory = new MemoryFake { Board = 10 };
        var context = new ProfiledFeatureContext(directory.Root, "steamdeck-lcd|bios", new TestPluginHostAdapter(1))
            { Journal = TestRecovery.Open(directory.Root), ControllerManagementEnabled = false };
        var service = new SteamDeckNativeService(context, () => new NeptuneFake(), () => memory,
            () => new VangoghPower(new MemoryFake(), 0x80300000));
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity("steamdeck-lcd|bios"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.DoesNotContain(0xFE700B92u, memory.Reads);
        Assert.DoesNotContain(0xFE700B9Fu, memory.Reads);
        Assert.Equal(CommandOutcome.Applied, (await service.ExecuteAsync(
            Command("fan.target-rpm", CapabilityValue.Integer(3500)), context.Firmware,
            CancellationToken.None)).Outcome);
        Assert.Equal(CommandOutcome.Applied, (await service.ExecuteAsync(
            Command("charge.limit", CapabilityValue.Integer(80)), context.Firmware, CancellationToken.None)).Outcome);
        memory.Reads.Clear();
        await service.RefreshTelemetryAsync(CancellationToken.None);
        Assert.Equal(new[] { 0xFE7003B0, 0xFE70046E }, memory.Reads);
        Assert.Contains(0xFE7003B0u, memory.Reads);
        Assert.Contains(0xFE70046Eu, memory.Reads);
        Assert.Equal(3500, service.States.Single(state => state.CapabilityId == "fan.target-rpm")
            .ObservedValue!.IntegerValue);
        Assert.Equal(80, service.States.Single(state => state.CapabilityId == "charge.limit")
            .ObservedValue!.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.DoesNotContain(0xFE700B92u, memory.Reads);
        Assert.DoesNotContain(0xFE700B9Fu, memory.Reads);
        Assert.Empty(context.Journal.Entries<SteamDeckOriginal>());
    }

    [Fact]
    public async Task FremontRefreshAndRestoreNeverReadBackAcknowledgedLighting()
    {
        using var directory = new TemporaryDirectory();
        var module = new LightingFake();
        var context = new ProfiledFeatureContext(directory.Root, "steam-machine|bios", new TestPluginHostAdapter(1))
            { Journal = TestRecovery.Open(directory.Root) };
        var service = new SteamMachineLighting(context, () => module);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never,
            new ProfiledCycleIdentity(context.Firmware));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Contains("ioctl_get_brightness_scale", module.Calls);
        module.Calls.Clear();
        Assert.Equal(CommandOutcome.Applied, (await service.ExecuteAsync(
                Command("lighting.brightness", CapabilityValue.Integer(80)), context.Firmware, CancellationToken.None))
            .Outcome);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(80, service.States.Single(state => state.CapabilityId == "lighting.brightness")
            .ObservedValue!.IntegerValue);
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.DoesNotContain(module.Calls, call => call.StartsWith("ioctl_get_", StringComparison.Ordinal));
    }

    private static CapabilityCommand Command(string id, CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = value, Deadline = Deadline.Never
        };
    }

    private sealed class LightingFake : IPawnIoModule
    {
        public List<string> Calls { get; } = [];

        public long[] Execute(string function, long[] input, int outputWords)
        {
            Calls.Add(function);
            return function switch
            {
                "ioctl_get_led_num" => [17], "ioctl_get_enabled" => [1],
                "ioctl_get_brightness_scale" => [100], "ioctl_get_effect" => [3],
                "ioctl_get_led_color" => [255, 0, 0], _ => new long[outputWords]
            };
        }

        public void Dispose()
        {
        }
    }

    private sealed class NeptuneFake : INeptuneDevice
    {
        private readonly ManualResetEventSlim _stop = new(false);

        public Dictionary<byte, ushort> Registers { get; } = new()
            { [7] = 0, [8] = 0, [9] = 1, [71] = 1, [48] = 0x1C };

        public List<bool> MappingChanges { get; } = [];
        public List<byte> SettingReads { get; } = [];
        public bool ManagedMappings { get; private set; }
        public List<HapticOutputFrame> Output { get; } = [];
        public bool Disposed { get; private set; }
        public bool Stopped { get; private set; }
        public string FirmwareIdentity => "firmware|serial|USB(1)";

        public IReadOnlyList<PhysicalDeviceIdentity> Identities { get; } =
        [
            new()
            {
                InstancePath = "HID\\VID_28DE&PID_1205&MI_02", VendorId = "28DE", ProductId = "1205",
                RequiresHiding = true
            }
        ];

        public int Read(byte[] buffer)
        {
            _stop.Wait();
            throw new OperationCanceledException();
        }

        public void StopReading()
        {
            Stopped = true;
            _stop.Set();
        }

        public ushort? ReadSetting(byte id)
        {
            SettingReads.Add(id);
            return Registers.GetValueOrDefault(id);
        }

        public void WriteSetting(byte id, ushort value)
        {
            Registers[id] = value;
        }

        public void SetManagedMappings(bool managed)
        {
            MappingChanges.Add(managed);
            ManagedMappings = managed;
            if (managed)
            {
                Registers[7] = 7;
                Registers[8] = 7;
            }
        }

        public void Rumble(HapticOutputFrame frame)
        {
            Output.Add(frame);
        }

        public void Dispose()
        {
            Disposed = true;
            _stop.Dispose();
        }
    }

    private sealed class MemoryFake : ISteamDeckMemory
    {
        public uint Board { get; init; } = 6;
        public List<uint> Reads { get; } = [];

        public uint Read(uint address, int width)
        {
            Reads.Add(address);
            return address switch
            {
                0xFE700C4C => 0xB030, 0xFE7003BD => Board, 0xFE700C01 => 0, 0xFE7003B0 => 2000,
                0xFE700B92 => 2500, 0xFE70046E => 0xAC0A,
                _ => 1
            };
        }

        public void Write(uint address, uint value, int width)
        {
        }

        public void WriteFanMode(byte mode)
        {
        }

        public void AuthorizeGpuRange(uint mmioBase)
        {
        }

        public void Dispose()
        {
        }
    }
}
