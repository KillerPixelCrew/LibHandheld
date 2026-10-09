using System.Buffers.Binary;
using System.Numerics;
using LibHandheld.Families.Ayaneo;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Ayaneo;

public sealed class AyaneoTests
{
    [Theory]
    [InlineData("AB05-AMD", "air-plus-amd", 0x1804, 255, 3, 33)]
    [InlineData("AB05-Mendocino", "air-plus-mendocino", 0x1802, 255, 5, 15)]
    [InlineData("AB05-Intel", "air-plus-intel", 0x1802, 255, 5, 55)]
    [InlineData("AB05NXX", "air-plus-nxx", 0x1802, 255, 5, 15)]
    [InlineData("AB10", "air-2", 0x1809, 184, 5, 30)]
    [InlineData("KUN", "kun", 0x044B, 100, 3, 54)]
    public void ExactIdentityChoosesFanCodecAndPowerLimits(string product, string suffix, int duty,
        int scale, int minimum, int maximum)
    {
        var id = AyaneoModels.Match(Identity(product));
        Assert.Equal("ayaneo." + suffix, id);
        var model = AyaneoModels.All.Single(model => model.Profile.Definition.Id == id).Profile;
        Assert.Equal(duty, model.Fan!.DutyAddresses[0]);
        Assert.Equal(scale, model.Fan.EncodeDuty(100));
        Assert.Equal(minimum, model.MinimumWatts);
        Assert.Equal(maximum, model.MaximumWatts);
        Assert.Null(AyaneoModels.Match(Identity(product + "x")));
        Assert.Null(AyaneoModels.Match(Identity(product) with { SystemManufacturer = "AYN" }));
    }

    [Fact]
    public void CpuAmbiguityNeverChoosesTheLargerTdpRange()
    {
        Assert.Null(AyaneoModels.Match(Identity("AYANEO 3")));
        Assert.Equal("ayaneo.3-8840u", AyaneoModels.Match(Identity("AB07") with
        {
            ProcessorName = "AMD Ryzen 7 8840U w/ Radeon 780M Graphics"
        }));
        Assert.Equal("ayaneo.3-hx370", AyaneoModels.Match(Identity("AB07") with
        {
            ProcessorName = "AMD Ryzen AI 9 HX 370 w/ Radeon 890M"
        }));
        Assert.Equal("ayaneo.3-8840u", AyaneoModels.Match(Identity("AB07") with
        {
            ProcessorName = "AMD Ryzen 7 8840U with a revised brand suffix"
        }));
        Assert.Null(AyaneoModels.Match(Identity("AB07") with { ProcessorName = "Fake18840U" }));
    }

    [Fact]
    public void ChildMotionProfilesOverrideTheSharedProtocolAxes()
    {
        var slide = Model("slide").Profile.Motion!;
        var next = Model("next-2").Profile.Motion!;
        Assert.Equal(new Vector3(-1, 3, -2), slide.Acceleration.Apply(new Vector3(1, 2, 3)));
        Assert.Equal(new Vector3(-1, -3, 2), next.Gyro.Apply(new Vector3(1, 2, 3)));
        Assert.Equal(new Vector3(1, 3, 2), Model("2s").Profile.Motion!.Gyro.Apply(new Vector3(1, 2, 3)));
    }

    [Fact]
    public void SuperJoyFramesIncludeWindowsReportPrefixAndNativeChecksum()
    {
        var report = AyaneoSuperJoy.Lighting(0x102030, 0xA0B0C0, 50, true);
        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 1, 80, 88, 96 }, report[8..12]);
        Assert.Equal(new byte[] { 1, 8, 16, 24 }, report[12..16]);
        Assert.Equal((ushort)report[7..].Sum(value => value),
            BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(1)));
        Assert.Equal(0x20, report[24]);
        Assert.Equal(1, report[32]);
        var mapping = AyaneoSuperJoy.Mapping(12, 0x6E);
        Assert.Equal(new byte[] { 0, 0x6E, 0, 0x0B, 7, 12, 0, 0, 0, 0, 0, 0, 0x6E }, mapping[..13]);
        Assert.Equal("Cross / Touchpad", AyaneoSuperJoy.LeftModule(0x06));
        Assert.Equal("Touchpad / ABXY", AyaneoSuperJoy.RightModule(0x52));
    }

    [Theory]
    [InlineData("2", 0x1E, 0x55)]
    [InlineData("air-plus-intel", 0xD1D1, 1)]
    public async Task BypassWritesDoNotDependOnReadback(string id, int address, int value)
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        var service = Service(Model(id), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        ec.FailReadback = true;
        var result = await service.ExecuteAsync(Command(AyaneoNativeService.BypassId,
            CapabilityValue.Boolean(true)), "bios1", CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Contains(((ushort)address, (byte)value), ec.Writes);
        ec.FailReadback = false;
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Fact]
    public async Task CecConfirmationAndOriginalOwnerArePreserved()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        ec.Values[0xBF] = 0x22;
        var service = Service(Model("2"), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        ec.Writes.Clear();
        await service.ExecuteAsync(Command(AyaneoNativeService.LightPowerId, CapabilityValue.Boolean(false)), "bios1",
            CancellationToken.None);
        Assert.Contains(((ushort)0x6D, (byte)3), ec.Writes);
        Assert.Contains(((ushort)0xB2, (byte)0xC0), ec.Writes);
        Assert.Equal(ec.Writes.Count(write => write.Address == 0xBF && write.Value == 0x10),
            ec.Writes.Count(write => write.Address == 0xBF && write.Value == 0xFE));
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
        Assert.Equal(0x22, ec.Values[0xBF]);
    }

    [Fact]
    public async Task CeiiUsesIndexedAddressesWithoutCecConfirmation()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        var service = Service(Model("air-plus-amd"), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        ec.Writes.Clear();
        await service.ExecuteAsync(Command(AyaneoNativeService.LightPowerId, CapabilityValue.Boolean(false)), "bios1",
            CancellationToken.None);
        Assert.Contains(((ushort)0xD1B2, (byte)0xC0), ec.Writes);
        Assert.Contains(((ushort)0xD172, (byte)0xC0), ec.Writes);
        Assert.Contains(((ushort)0xD1C6, (byte)1), ec.Writes);
        Assert.Contains(((ushort)0xD186, (byte)1), ec.Writes);
        Assert.DoesNotContain(ec.Writes, write => write.Address == 0xBF);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Fact]
    public async Task FailedLightingWriteCannotBeReappliedByResume()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        var service = Service(Model("2"), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        await service.ExecuteAsync(Command(AyaneoNativeService.LightPowerId, CapabilityValue.Boolean(true)), "bios1",
            CancellationToken.None);
        ec.FailWriteAddress = 0xB2;
        var failed = await service.ExecuteAsync(
            Command(CapabilityIds.LightingBrightness, CapabilityValue.Integer(45)), "bios1", CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, failed.Outcome);
        ec.FailWriteAddress = null;
        await service.SuspendAsync(Cycle(), CancellationToken.None);
        ec.Writes.Clear();
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        Assert.DoesNotContain(ec.Writes, write => write.Address == 0xB2);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Fact]
    public void SuperJoyMappingUsesModifierTenAndKeyboardKeyTwelve()
    {
        var report = AyaneoSuperJoy.Mapping(12, 0x6E, modifier: 4);
        Assert.Equal(4, report[10]);
        Assert.Equal(0x6E, report[12]);
        Assert.Equal(0, report[9]);
        Assert.Equal(0, report[11]);
        Assert.Equal((ushort)0x72, BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(1)));
    }

    [Theory]
    [InlineData("flip-kb")]
    [InlineData("flip-1s-kb")]
    public void FlipKeyboardModelsRetainCecConfirmation(string id)
    {
        Assert.True(Model(id).RgbConfirmation);
    }

    [Fact]
    public async Task SecondaryBrightnessClearsTheDocumentedPrefixAndWritesTheScaledValue()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        var service = Service(Model("flip-ds"), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        ec.Writes.Clear();
        var result = await service.ExecuteAsync(Command(AyaneoNativeService.SecondaryId,
            CapabilityValue.Integer(50)), "bios1", CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(new (ushort, byte)[] { (0x4F, 0), (0x4E, 127) }, ec.Writes);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Fact]
    public async Task KunZoneThreeAndFourUseTheirSeparateBgrChannelOrders()
    {
        using var directory = new TemporaryDirectory();
        var ec = new FakeEc();
        var service = Service(Model("kun"), directory.Root, ec);
        await service.AcquireAsync(Cycle(), CancellationToken.None);
        ec.Writes.Clear();
        var command = Command(CapabilityIds.LightingColor, CapabilityValue.Color(0x305070)) with
        {
            InstanceId = "left"
        };
        await service.ExecuteAsync(command, "bios1", CancellationToken.None);
        List<(byte Register, byte Value)> left = [];
        byte group = 0;
        byte register = 0;
        foreach (var write in ec.Writes)
        {
            if (write.Address == 0x6D)
            {
                group = write.Value;
            }
            else if (write.Address == 0xB1)
            {
                register = write.Value;
            }
            else if (write.Address == 0xB2 && group == 1)
            {
                left.Add((register, write.Value));
            }
        }

        Assert.Contains(((byte)9, (byte)84), left);
        Assert.Contains(((byte)10, (byte)36), left);
        Assert.Contains(((byte)11, (byte)60), left);
        Assert.Contains(((byte)12, (byte)84), left);
        Assert.Contains(((byte)13, (byte)60), left);
        Assert.Contains(((byte)14, (byte)36), left);
        await service.ReleaseAsync(Cycle(), CancellationToken.None);
    }

    [Theory]
    [InlineData("submission")]
    [InlineData("query")]
    [InlineData("unconfirmed")]
    public async Task EjectFailuresNeverCutControllerPower(string failure)
    {
        var powerDown = false;
        var submissions = 0;

        ValueTask<byte[]> Submit(CancellationToken _)
        {
            submissions++;
            return failure == "submission"
                ? ValueTask.FromException<byte[]>(new IOException("Submission unknown"))
                : ValueTask.FromResult(new byte[65]);
        }

        ValueTask<byte[]> Query(CancellationToken _)
        {
            if (failure == "query")
            {
                return ValueTask.FromException<byte[]>(new IOException("Query failed"));
            }

            var response = new byte[65];
            response[20] = 0x77;
            return ValueTask.FromResult(response);
        }

        await Assert.ThrowsAsync<IOException>(() => AyaneoNativeService.CompleteEjectAsync(Submit, Query,
            () => powerDown = true, CancellationToken.None, (_, _) => Task.CompletedTask).AsTask());
        Assert.Equal(1, submissions);
        Assert.False(powerDown);
    }

    [Fact]
    public async Task ConfirmedEjectPowersDownExactlyOnceAfterItsMinimumLatchTime()
    {
        List<string> calls = [];
        var queries = 0;
        await AyaneoNativeService.CompleteEjectAsync(_ =>
            {
                calls.Add("submit");
                return ValueTask.FromResult(new byte[65]);
            }, _ =>
            {
                calls.Add("query");
                queries++;
                var response = new byte[65];
                response[20] = queries == 1 ? (byte)0x77 : (byte)0x11;
                return ValueTask.FromResult(response);
            }, () => calls.Add("power-down"), CancellationToken.None,
            (duration, _) =>
            {
                calls.Add(duration > TimeSpan.FromSeconds(2) ? "minimum-latch-delay" : "status-delay");
                return Task.CompletedTask;
            }, () => TimeSpan.Zero);
        Assert.Equal(
            new[] { "submit", "query", "status-delay", "query", "status-delay", "minimum-latch-delay", "power-down" },
            calls);
    }

    private static DeviceIdentitySnapshot Identity(string product)
    {
        return new DeviceIdentitySnapshot { SystemManufacturer = "AYANEO", SystemProduct = product };
    }

    private static AyaneoModel Model(string suffix)
    {
        return AyaneoModels.All.Single(model => model.Profile.Definition.Id == "ayaneo." + suffix);
    }

    private static AyaneoNativeService Service(AyaneoModel model, string directory, FakeEc ec)
    {
        return new AyaneoNativeService(model,
            new ProfiledFeatureContext(directory, "bios1", new TestPluginHostAdapter(1))
                { Journal = TestRecovery.Open(directory) }, _ => ec);
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
        internal Dictionary<ushort, byte> Values { get; } = new();
        internal List<(ushort Address, byte Value)> Writes { get; } = [];
        internal bool FailReadback { get; set; }
        internal ushort? FailWriteAddress { get; set; }

        public byte ReadByte(ushort address)
        {
            return FailReadback ? throw new IOException("Unreadable") : Values.GetValueOrDefault(address);
        }

        public ushort ReadUInt16(ushort address)
        {
            return (ushort)(ReadByte(address) | (ReadByte((ushort)(address + 1)) << 8));
        }

        public void WriteByte(ushort address, byte value)
        {
            if (FailWriteAddress == address)
            {
                throw new IOException("Write effect unknown");
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
        }
    }
}
