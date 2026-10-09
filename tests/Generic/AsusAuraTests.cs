using LibHandheld.Families.Generic;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Testing;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Generic;

public sealed class AsusAuraTests
{
    [Fact]
    public void ExactProductApplicationAndFeatureShapeSelectTheAuraProtocol()
    {
        Assert.True(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Lightbar, 64)));
        Assert.True(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Keyboard, 65)));
        Assert.True(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Xg2025, 300)));
        Assert.False(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Xg2025, 64)));
        Assert.False(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Keyboard, 300)));
        Assert.False(AsusAuraTransport.Matches(Collection(0x1ABE, 64)));
        Assert.False(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Lightbar, 64) with
        {
            UsagePage = 1, Usage = 1
        }));
        Assert.False(AsusAuraTransport.Matches(Collection(AsusAuraProtocol.Lightbar, 64) with { VendorId = 0x1234 }));
    }

    [Fact]
    public void PacketFixturesKeepXgThreeHundredByteReportsSeparateFromKeyboardReports()
    {
        var normal = AsusAuraProtocol.Color(AsusAuraProtocol.Keyboard, "duality", 0x123456, 0xABCDEF, 50, false);
        Assert.Equal(64, normal.Length);
        Assert.Equal(new byte[] { 0x5D, 0xB3, 0, 1, 0x12, 0x34, 0x56, 0xEB, 0, 0, 0xAB, 0xCD, 0xEF }, normal[..13]);
        var xg = AsusAuraProtocol.Color(AsusAuraProtocol.Xg2025, "rainbow", 0, 0, 100, false);
        Assert.Equal(300, xg.Length);
        Assert.Equal((byte)0x5E, xg[0]);
        Assert.Equal((byte)0xF5, xg[7]);
        Assert.Equal(new byte[] { 0xB4, 0xB5 },
            AsusAuraProtocol.Apply(AsusAuraProtocol.Xg2025).Select(packet => packet[1]));
        Assert.Equal(new byte[] { 0xB5, 0xB4 },
            AsusAuraProtocol.Apply(AsusAuraProtocol.Keyboard).Select(packet => packet[1]));
        Assert.Equal(new byte[] { 0x5D, 0xBD, 1, 15, 7, 51, 51, 0xFF },
            AsusAuraProtocol.Power(AsusAuraProtocol.Keyboard, true, true, false, false)[..8]);
        Assert.Equal(new byte[] { 0x5E, 0xC5, 0 },
            AsusAuraProtocol.Power(AsusAuraProtocol.Xg2025, true, false, true, true)[..3]);
        Assert.Equal(new byte[] { 0x5A, 0xBA, 0xC5, 0xC4, 2 },
            AsusAuraProtocol.Brightness(AsusAuraProtocol.Keyboard, 66)[..5]);
        Assert.Equal(new byte[] { 0x5E, 0xBA, 0xC5, 0xC4, 3 },
            AsusAuraProtocol.Brightness(AsusAuraProtocol.Xg2025, 100)[..5]);
    }

    [Fact]
    public void ModesAreLimitedToTheCapabilitiesOfEachModule()
    {
        Assert.Contains("cycle", AsusAuraProtocol.Effects(AsusAuraProtocol.Lightbar));
        Assert.DoesNotContain("cycle", AsusAuraProtocol.Effects(AsusAuraProtocol.Keyboard));
        Assert.DoesNotContain("strobe", AsusAuraProtocol.Effects(AsusAuraProtocol.Xg2025));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AsusAuraProtocol.Color(AsusAuraProtocol.Xg2025, "strobe", 0, 0, 50, false));
    }

    [Fact]
    public void DiscoveryDoesNotInitializeOrPretendToObserveLighting()
    {
        var module = Module(AsusAuraProtocol.Keyboard);
        var transport = new FakeTransport();
        var controller = new AsusAuraController(() => [module], _ => transport);
        controller.Refresh(CancellationToken.None);
        Assert.Empty(transport.Packets);
        Assert.NotEmpty(controller.Descriptors);
        Assert.All(controller.States, state =>
        {
            Assert.Null(state.ObservedValue);
            Assert.Equal(HardwareStateQuality.Unknown, state.Quality);
        });
        var generation = controller.Generation;
        controller.Refresh(CancellationToken.None);
        Assert.Equal(generation, controller.Generation);
        controller.Close();
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task AccessoryModeComposesAuraWithoutAnyLaptopFirmwareDescriptors()
    {
        var module = Module(AsusAuraProtocol.Xg2025);
        var transport = new FakeTransport();
        var aura = new AsusAuraController(() => [module], _ => transport);
        var service =
            new GenericAsusService(new ProfiledFeatureContext("unused", "firmware", new TestPluginHostAdapter(1)), aura,
                false);
        var cycle = new DeviceCycleContext<ProfiledCycleIdentity>(1, Deadline.Never,
            new ProfiledCycleIdentity("firmware"));
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.DoesNotContain(service.Descriptors,
            descriptor =>
                descriptor.Role is CapabilityRole.ChargeLimit or CapabilityRole.ScenarioMode
                    or CapabilityRole.FanCurve);
        Assert.Contains(service.Descriptors,
            descriptor => descriptor.Role == CapabilityRole.LightingEffect && descriptor.InstanceId == module.ZoneId);
        Assert.Empty(transport.Packets);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public void ExplicitXgColorInitializesAndCommitsOnlyTheXgModule()
    {
        var module = Module(AsusAuraProtocol.Xg2025);
        var transport = new FakeTransport();
        var controller = new AsusAuraController(() => [module], _ => transport);
        controller.Refresh(CancellationToken.None);
        var result =
            controller.Execute(Command(CapabilityIds.LightingColor, module.ZoneId, CapabilityValue.Color(0x123456)),
                CancellationToken.None);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(new byte[] { 0x41, 0xE4, 0xC5, 0xBD, 0xB3, 0xB4, 0xB5 },
            transport.Packets.Select(packet => packet[1]));
        Assert.All(transport.Packets, packet =>
        {
            Assert.Equal(300, packet.Length);
            Assert.Equal((byte)0x5E, packet[0]);
        });
        Assert.Equal(0x123456,
            controller.States
                .Single(state => state.CapabilityId == CapabilityIds.LightingColor && state.InstanceId == module.ZoneId)
                .ObservedValue?.ColorValue);
        controller.Close();
    }

    [Fact]
    public void HotplugRetractsDescriptorsAndReopensWithoutReapplyingPreferences()
    {
        var module = Module(AsusAuraProtocol.Lightbar);
        IReadOnlyList<AsusAuraModule> found = [module];
        var opened = new List<FakeTransport>();
        var controller = new AsusAuraController(() => found, _ =>
        {
            var transport = new FakeTransport();
            opened.Add(transport);
            return transport;
        });
        controller.Refresh(CancellationToken.None);
        var generation = controller.Generation;
        found = [];
        controller.Refresh(CancellationToken.None);
        Assert.Empty(controller.Descriptors);
        Assert.True(opened[0].Disposed);
        Assert.True(controller.Generation > generation);
        found = [module];
        controller.Refresh(CancellationToken.None);
        Assert.Equal(2, opened.Count);
        Assert.Empty(opened[1].Packets);
        Assert.NotEmpty(controller.Descriptors);
        controller.Close();
    }

    [Fact]
    public void UncertainColorIsNotRetriedByDiscoverySuspendOrReplug()
    {
        var module = Module(AsusAuraProtocol.Keyboard);
        var opened = 0;
        var transport = new FakeTransport { FailColor = true };
        var controller = new AsusAuraController(() => [module], _ =>
        {
            opened++;
            return transport;
        });
        controller.Refresh(CancellationToken.None);
        var command = Command(CapabilityIds.LightingColor, module.ZoneId, CapabilityValue.Color(0x123456));
        Assert.Equal(CommandOutcome.Indeterminate, controller.Execute(command, CancellationToken.None).Outcome);
        var count = transport.Packets.Count;
        controller.Refresh(CancellationToken.None);
        controller.Close();
        controller.Refresh(CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, controller.Execute(command, CancellationToken.None).Outcome);
        Assert.Equal(count, transport.Packets.Count);
        Assert.Equal(1, opened);
        Assert.All(controller.States, state => Assert.False(state.Available));
        controller.Close();
    }

    private static CapabilityCommand Command(string id, string instance, CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, InstanceId = instance, RequestedValue = value,
            ExpectedCycleGeneration = 1, ExpectedDescriptorGeneration = 1, Deadline = Deadline.Never
        };
    }

    private static AsusAuraModule Module(ushort product)
    {
        return AsusAuraTransport.Module(Collection(product,
            product == AsusAuraProtocol.Xg2025 ? (ushort)300 : (ushort)64));
    }

    private static HidCollection Collection(ushort product, ushort featureLength)
    {
        return new HidCollection
        {
            DevicePath = "fake-aura-" + product, InstancePath = "fake-instance-" + product,
            VendorId = AsusAuraProtocol.Vendor,
            ProductId = product, ReleaseNumber = 1, UsagePage = 0xFF31, Usage = 0x0076, InputLength = 0,
            OutputLength = 0,
            FeatureLength = featureLength, PhysicalLocation = "PCIROOT(0)#USBROOT(0)#USB(3)"
        };
    }

    private sealed class FakeTransport : IAsusAuraTransport
    {
        internal List<byte[]> Packets { get; } = [];
        internal bool Disposed { get; private set; }
        internal bool FailColor { get; init; }

        public void Send(byte[] packet)
        {
            Packets.Add((byte[])packet.Clone());
            if (FailColor && packet[1] == 0xB3)
            {
                throw new IOException("Submitted Aura write is uncertain");
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
