using LibHandheld.Families.OneXPlayer;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.OneXPlayer;

public sealed class OneXPlayerTests
{
    [Fact]
    public void EveryConcreteModelHasAnUnambiguousSelector()
    {
        Assert.Equal(25, OneXPlayerModels.All.Count);
        Assert.Equal(25, OneXPlayerModels.Definitions.Select(item => item.Id).Distinct().Count());
        foreach (var model in OneXPlayerModels.All)
        foreach (var product in model.Products)
        {
            var identity = new DeviceIdentitySnapshot
            {
                BaseboardManufacturer = model.Manufacturers[0], BaseboardProduct = product,
                SystemManufacturer = model.Manufacturers[0], SystemProduct = product,
                BaseboardVersion = model.Versions.FirstOrDefault(), ProcessorName = model.ProcessorSkus.FirstOrDefault()
            };
            Assert.Same(model, OneXPlayerModels.Match(identity));
        }
    }

    [Theory]
    [InlineData("V01", "oxp-mini-amd")]
    [InlineData("1002-C", "oxp-mini-intel")]
    [InlineData("V03", "oxp-mini-pro")]
    public void MiniRevisionUsesBaseboardVersion(string version, string expected)
    {
        Assert.Equal(expected, OneXPlayerModels.Match(new DeviceIdentitySnapshot
                { BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "ONE XPLAYER", BaseboardVersion = version })
            ?.Definition.Id);
        Assert.Equal("oxp-mini-amd", OneXPlayerModels.Match(new DeviceIdentitySnapshot
            {
                BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "ONE XPLAYER", BaseboardVersion = "Unknown"
            })
            ?.Definition.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("V02")]
    public void MiniUnknownRevisionsUseHcsAmdDefault(string? version)
    {
        Assert.Equal("oxp-mini-amd", OneXPlayerModels.Match(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "ONEXPLAYER Mini Pro",
            BaseboardVersion = version
        })?.Definition.Id);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(32, 1, 1)]
    [InlineData(33, 3, 1)]
    [InlineData(34, 3, 3)]
    [InlineData(66, 3, 3)]
    [InlineData(67, 4, 4)]
    public void BrightnessUsesEachFirmwareGenerationBoundary(int percentage, byte serialLevel, byte hidLevel)
    {
        var serial = OneXCommandCodec.Brightness(OneXLightingKind.Serial, percentage, 0);
        Assert.Equal(serialLevel, serial[8]);
        Assert.Equal((byte)1, serial[6]);
        var hid = OneXCommandCodec.Brightness(OneXLightingKind.HidV1, percentage, 0);
        Assert.Equal(hidLevel, hid[8]);
        Assert.Equal((byte)(percentage == 0 ? 0 : 1), hid[6]);
        Assert.Equal((byte)1, OneXCommandCodec.Brightness(OneXLightingKind.HidV2, percentage, 0)[4]);
        var rear = OneXCommandCodec.Brightness(OneXLightingKind.Serial, percentage, 3);
        Assert.Equal((byte)(percentage == 0 ? 0 : 1), rear[6]);
        Assert.Equal((byte)0, rear[7]);
        Assert.Equal((byte)0, rear[8]);
    }

    [Fact]
    public void X1AokClassicPresetKeepsItsNativeMode()
    {
        Assert.Equal((byte)0x14, OneXCommandCodec.Lighting(OneXLightingKind.Serial, "aok-classic", 0, 0)[3]);
        Assert.Equal(200, OneXNativeService.LightingDelay(OneXLightingKind.HidV1X2));
        Assert.Equal(50, OneXNativeService.LightingDelay(OneXLightingKind.HidV1));
    }

    [Theory]
    [InlineData(false, false, false, 0x20, 103, 102, 3)]
    [InlineData(true, false, false, 2, 104, 105, 4)]
    [InlineData(false, true, false, 2, 103, 102, 4)]
    [InlineData(false, false, true, 0x20, 103, 102, 3)]
    public void VendorRemappingUsesNativeBankKeysAndIntercept(bool mini, bool apex, bool three,
        byte bank, byte firstKey, byte secondKey, int count)
    {
        var profile = new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184,
            OneXLightingKind.HidV1, VendorButtons: true, X2: !apex, Mini: mini, Apex: apex, Three: three);
        var frames = OneXCommandCodec.ControllerConfiguration(profile);
        Assert.Equal(count, frames.Count);
        Assert.Equal(new byte[] { 0xB4, 0x3F, 1, 2, 0x38, bank, 1, 1 }, frames[0][..8]);
        Assert.Equal(firstKey, frames[1][53]);
        Assert.Equal(secondKey, frames[1][59]);
        if (apex)
        {
            Assert.Equal(new byte[] { 0xB2, 0x3F, 1, 1, 0x1F, 0x40, 3, 2, 3, 0, 0, 0, 1 }, frames[2][..13]);
        }
        else
        {
            Assert.Equal(new byte[] { 0xB4, 0x3F, 1, 2, 0x38, bank, 3, 1, 0x24, 2, 2, 5 }, frames[2][..12]);
        }

        if (mini || apex)
        {
            Assert.Equal(new byte[] { 0xB2, 0x3F, 1, 0, 1, 2 }, frames[^1][..6]);
        }
    }

    [Fact]
    public void StatusResendRulesSeparateX1MiniFromRemappedControllers()
    {
        var profile = new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184,
            OneXLightingKind.HidV1, VendorButtons: true);
        var status = OneXCommandCodec.V1(0xB8, [0, 0, 0x20]);
        Assert.True(OneXOemSource.RequestsConfiguration(profile, status));
        Assert.False(OneXOemSource.RequestsConfiguration(profile with { SimpleVibration = true }, status));
        status = OneXCommandCodec.V1(0xB8, [0xFE]);
        Assert.False(OneXOemSource.RequestsConfiguration(profile, status));
        Assert.True(OneXOemSource.RequestsConfiguration(profile with { SimpleVibration = true }, status));
        Assert.Equal(new byte[] { 0xB3, 0x3F, 1, 1, 5, 5 },
            OneXCommandCodec.ControllerConfiguration(profile with { SimpleVibration = true })[0][..6]);
    }

    [Fact]
    public void BusySerialPortLeavesHidFallbackAvailable()
    {
        Assert.Null(OneXOemSource.TryOpenSerial(() => throw new UnauthorizedAccessException("Busy port")));
    }

    [Fact]
    public void TakeoverOnlyChangesItsBitInTheLiveRegister()
    {
        var ec = new FakeEc();
        ec.Values[0x04EB] = 0xA5;
        OneXNativeService.WriteTakeoverBit(ec, true);
        Assert.Equal((byte)0xE5, ec.Values[0x04EB]);
        ec.Values[0x04EB] = 0xDA;
        OneXNativeService.WriteTakeoverBit(ec, false);
        Assert.Equal((byte)0x9A, ec.Values[0x04EB]);
        var unreadable = new FakeEc { ReadUnavailable = true };
        Assert.Throws<IOException>(() => OneXNativeService.WriteTakeoverBit(unreadable, true));
        Assert.Empty(unreadable.Writes);
    }

    [Fact]
    public async Task TakeoverRestorePreservesBitsChangedByFirmwareWhileOwned()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        ec.Values[0x04EB] = 0xA5;
        var service = new OneXNativeService(
            new ProfiledFeatureContext(directory.Root, "bios1", new TestPluginHostAdapter(1))
                { Journal = TestRecovery.Open(directory.Root), ControllerManagementEnabled = true },
            new OneXProtocolProfile(OneXEcKind.Wmi, 0x44A, 0x44B, 0, 184, OneXLightingKind.None), _ => ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        Assert.Equal((byte)0xE5, ec.Values[0x04EB]);
        ec.Values[0x04EB] = 0xDA;
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
        Assert.Equal((byte)0x9A, ec.Values[0x04EB]);
    }

    [Theory]
    [InlineData("8840U", "oxp-g1-8840u", 35)]
    [InlineData("HX 370", "oxp-g1-hx370", 65)]
    public void G1CpuVariantsKeepTheirOwnPowerEnvelope(string cpu, string expected, int maximum)
    {
        var model = OneXPlayerModels.Match(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "ONEXPLAYER G1 A",
            ProcessorName = "AMD Ryzen " + cpu
        });
        Assert.Equal(expected, model?.Definition.Id);
        Assert.Equal(maximum, model?.MaximumWatts);
    }

    [Fact]
    public void ExactWindowsBoardWinsOverAnAliasOnTheSystemField()
    {
        var matched = OneXPlayerModels.Match(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "ONEXPLAYER X2Mini PRO",
            SystemManufacturer = "ONE-NETBOOK", SystemProduct = "ONEXPLAYER F1 OLED"
        });
        Assert.Equal("oxp-x2-mini-pro", matched?.Definition.Id);
        var alias = OneXPlayerModels.Match(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ONE-NETBOOK", BaseboardProduct = "Unlisted board revision",
            SystemManufacturer = "ONE-NETBOOK", SystemProduct = "ONEXPLAYER F1 OLED"
        });
        Assert.Equal("oxp-f1", alias?.Definition.Id);
    }

    [Fact]
    public void X2RegisterAddressUsesBankThenOffset()
    {
        Assert.Equal(0xEB04u, OneXWmiEc.Address(0x04EB));
        Assert.Equal(0x4A04u, OneXWmiEc.Address(0x044A));
        Assert.Equal(0x4B04u, OneXWmiEc.Address(0x044B));
    }

    [Fact]
    public void VendorGenerationsKeepDistinctWireFraming()
    {
        var v1 = OneXCommandCodec.Brightness(OneXLightingKind.HidV1, 65, 5);
        Assert.Equal(new byte[] { 0xB8, 0x3F, 1, 0xFD, 5, 2, 1, 5, 3 }, v1[..9]);
        Assert.Equal(new byte[] { 0x3F, 0xB8 }, v1[^2..]);
        var v2 = OneXCommandCodec.Brightness(OneXLightingKind.HidV2, 100, 0);
        Assert.Equal(new byte[] { 0, 7, 0xFF, 0xFD, 1, 5, 4 }, v2[..7]);
        Assert.Equal(65, v2.Length);
        var serial = OneXCommandCodec.Lighting(OneXLightingKind.Serial, "solid", 0x123456, 3);
        Assert.Equal(new byte[] { 0xFD, 0x3F, 3, 0xFE, 0, 0, 0x12, 0x34, 0x56 }, serial[..9]);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x3F, 0xFD }, serial[^4..]);
    }

    [Fact]
    public void NativeButtonCodeMeansDifferentPhysicalControlsOnEachGeneration()
    {
        var x1 = new OneXProtocolProfile(OneXEcKind.Indexed, 0x44A, 0x44B, 0, 184, OneXLightingKind.Serial);
        Assert.Equal(4, OneXOemSource.DecodeButton(x1, 0x22));
        Assert.Equal(3, OneXOemSource.DecodeButton(x1, 0x23));
        var x2 = x1 with { X2 = true };
        Assert.Equal(3, OneXOemSource.DecodeButton(x2, 0x22));
        Assert.Equal(4, OneXOemSource.DecodeButton(x2, 0x23));
        Assert.Equal(1, OneXOemSource.DecodeButton(x2, 0x24));
        Assert.Equal(2, OneXOemSource.DecodeButton(x2 with { Three = true }, 0x24));
        Assert.Equal(1, OneXOemSource.DecodeButton(x2 with { Mini = true }, 0x21));
        Assert.Equal(-1, OneXOemSource.DecodeButton(x1 with { HasRearButtons = false }, 0x22));
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(33, 91)]
    [InlineData(50, 133)]
    [InlineData(100, 255)]
    public void OneXFlyRetainsTheElevenStepFanFloor(int percentage, byte encoded)
    {
        Assert.Equal(encoded, OneXNativeService.EncodeDuty(new OneXProtocolProfile(OneXEcKind.Acpi,
            0x4A, 0x4B, 11, 255, OneXLightingKind.None), percentage));
    }

    [Fact]
    public async Task FanUsesSelectedBackendAndRestoresCapturedRegisters()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        ec.Values[0x44A] = 0;
        ec.Values[0x44B] = 91;
        var service = Service(directory.Root, ec, OneXEcKind.Wmi);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        var result = await service.ExecuteAsync(Command("fan.duty", CapabilityValue.Integer(50)), "bios1",
            CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(new (ushort, byte)[] { (0x44B, 92), (0x44A, 1) }, ec.Writes);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
        Assert.Equal((byte)91, ec.Values[0x44B]);
        Assert.Equal((byte)0, ec.Values[0x44A]);
        Assert.True(ec.Disposed);
    }

    [Fact]
    public async Task SelectingCustomFanModePreservesTheExistingDuty()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        ec.Values[0x44B] = 91;
        var service = Service(directory.Root, ec, OneXEcKind.Indexed);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        await service.ExecuteAsync(Command(CapabilityIds.FanMode, CapabilityValue.Choice("custom")), "bios1",
            CancellationToken.None);
        Assert.Equal(new (ushort, byte)[] { (0x44A, 1) }, ec.Writes);
        Assert.Equal((byte)91, ec.Values[0x44B]);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Fact]
    public async Task MissingReadbackDoesNotBlockFanWriteButCannotClaimOriginalRestored()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc { ReadUnavailable = true };
        var service = Service(directory.Root, ec, OneXEcKind.Indexed);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        var result = await service.ExecuteAsync(Command("fan.duty", CapabilityValue.Integer(50)), "bios1",
            CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        var released = await service.ReleaseAsync(Cycle(), CancellationToken.None);
        Assert.Equal(DeviceServiceState.ReleasedUnverified, released.State);
        Assert.DoesNotContain(((ushort)0x44A, (byte)0), ec.Writes);
    }

    [Fact]
    public async Task AnUncertainCommandIsNotRepeatedByObservationButAnExplicitRetryIsAllowed()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc { FailWrite = true };
        var service = Service(directory.Root, ec, OneXEcKind.Indexed);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, (await service.ExecuteAsync(
            Command("fan.duty", CapabilityValue.Integer(50)), "bios1", CancellationToken.None)).Outcome);
        ec.FailWrite = false;
        ec.Writes.Clear();
        await service.RefreshAsync(CancellationToken.None);
        Assert.Empty(ec.Writes);
        Assert.Equal(CommandOutcome.Applied, (await service.ExecuteAsync(
            Command("fan.duty", CapabilityValue.Integer(50)), "bios1", CancellationToken.None)).Outcome);
        Assert.Equal(new (ushort, byte)[] { (0x44B, 92), (0x44A, 1) }, ec.Writes);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    private static OneXNativeService Service(string path, FakeEc ec, OneXEcKind kind)
    {
        return new OneXNativeService(
            new ProfiledFeatureContext(path, "bios1", new TestPluginHostAdapter(1))
                { Journal = TestRecovery.Open(path) },
            new OneXProtocolProfile(kind, 0x44A, 0x44B, 0, 184, OneXLightingKind.None), _ => ec, () => ec);
    }

    private static DeviceCycleContext<ProfiledCycleIdentity> Cycle()
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity("bios1"));
    }

    private static CapabilityCommand Command(string id, CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = value, Deadline = Deadline.Never
        };
    }

    private sealed class FakeEc : IEmbeddedController
    {
        internal Dictionary<ushort, byte> Values { get; } = [];
        internal List<(ushort Address, byte Value)> Writes { get; } = [];
        internal bool ReadUnavailable { get; init; }
        internal bool FailWrite { get; set; }
        internal bool Disposed { get; private set; }

        public byte ReadByte(ushort address)
        {
            return ReadUnavailable ? throw new IOException("Unreadable") : Values.GetValueOrDefault(address);
        }

        public ushort ReadUInt16(ushort address)
        {
            return (ushort)(ReadByte(address) | (ReadByte((ushort)(address + 1)) << 8));
        }

        public void WriteByte(ushort address, byte value)
        {
            if (FailWrite)
            {
                throw new IOException("Submitted write result unknown");
            }

            Values[address] = value;
            Writes.Add((address, value));
        }

        public void WriteUInt16(ushort address, ushort value)
        {
            WriteByte(address, (byte)value);
            WriteByte((ushort)(address + 1), (byte)(value >> 8));
        }

        public T Transaction<T>(Func<T> operation)
        {
            return operation();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
