using LibHandheld.Families.SteamDeck;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Testing;
using LibHandheld.Testing;

namespace LibHandheld.Tests.SteamDeck;

public sealed class ValveLifecycleTests
{
    [Fact]
    public async Task RuntimeControllerToggleRetiresReaderAndRestoresOnlyCapturedRegisters()
    {
        using var directory = new TemporaryDirectory();
        var host = new TestPluginHostAdapter(1);
        var controllers = new List<NeptuneFake>();
        var context = new ProfiledFeatureContext(directory.Root, "steamdeck-lcd|bios", host)
            { ControllerManagementEnabled = true };
        var service = new SteamDeckNativeService(context,
            () =>
            {
                var pad = new NeptuneFake();
                controllers.Add(pad);
                return pad;
            },
            () => new MemoryFake(), () => new VangoghPower(new MemoryFake(), 0x80300000));
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(1, Deadline.Never,
            new ProfiledCycleIdentity("steamdeck-lcd|bios"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.True(service.Connected);
        Assert.Equal((ushort)0, controllers[0].Registers[9]);
        Assert.Equal((ushort)0x18, controllers[0].Registers[48]);
        await service.SetControllerManagementAsync(false, CancellationToken.None);
        Assert.False(service.Connected);
        Assert.True(controllers[0].Disposed);
        Assert.True(controllers[0].Stopped);
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

    private sealed class NeptuneFake : INeptuneDevice
    {
        private readonly ManualResetEventSlim _stop = new(false);
        public Dictionary<byte, ushort> Registers { get; } = new() { [9] = 1, [71] = 1, [48] = 0x1C };
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
            return Registers.GetValueOrDefault(id);
        }

        public void WriteSetting(byte id, ushort value)
        {
            Registers[id] = value;
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
        public uint Read(uint address, int width)
        {
            return address switch
            {
                0xFE700C4C => 0xB030, 0xFE7003BD => 6, 0xFE7003B0 => 2000, 0xFE700B92 => 2500, 0xFE70046E => 0xAC0A,
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
