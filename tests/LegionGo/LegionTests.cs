using System.Buffers.Binary;
using LibHandheld.Families.LegionGo;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Transports;
using LibHandheld.Testing;

namespace LibHandheld.Tests.LegionGo;

public sealed class LegionTests
{
    [Theory]
    [InlineData("83E1", false, 5, 30)]
    [InlineData("83N0", true, 15, 35)]
    [InlineData("83N1", true, 15, 35)]
    [InlineData("83L3", false, 5, 30)]
    [InlineData("83N6", false, 5, 30)]
    [InlineData("83Q2", false, 5, 30)]
    [InlineData("83Q3", false, 5, 30)]
    public void ExactModelControlsSharedProtocolAndBounds(string product, bool ec, int minimum, int maximum)
    {
        var model = LegionGoModels.Match(Identity(product));
        Assert.NotNull(model);
        Assert.Equal(ec, model.EcFan);
        Assert.Equal(minimum, model.MinimumWatts);
        Assert.Equal(maximum, model.MaximumWatts);
        Assert.Null(LegionGoModels.Match(Identity(product) with { SystemManufacturer = "Other" }));
        Assert.Null(LegionGoModels.Match(Identity(product + "x")));
    }

    [Fact]
    public void SlimInputUsesTheUnnumberedPayloadAfterWindowsReportPrefix()
    {
        var payload = new byte[32];
        payload[0] = 3;
        payload[2] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(20), 1000);
        var model = LegionGoModels.All.Single(m => m.SystemModel == "83L3");
        Assert.True(LegionInputCodec.TryDecode(model, payload, DateTimeOffset.UtcNow, false, false, out var decoded));
        Assert.Equal(3, decoded.OemButtons & 3);
        Assert.True((decoded.Sample.Buttons & CanonicalButtons.RearPaddle1) != 0);
        Assert.Equal(-1000 * (2000f / short.MaxValue), decoded.Sample.Motion!.Value.GyroX);
    }

    [Fact]
    public void SlimUsesWindowsPercentageBrightnessAndInvertedSpeed()
    {
        var model = LegionGoModels.All.Single(m => m.SystemModel == "83L3");
        var report = LegionProtocol.Rgb(model, 3, "solid", 0x102030, 100, 25);
        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0, 0x10, 5, 0, 0x10, 0x20, 0x30, 100, 75 }, report[..9]);
    }

    [Fact]
    public void TabletBrightnessIsSixBitsAndFanTableIsLittleEndian()
    {
        var rgb = LegionProtocol.Rgb(LegionGoModels.All[0], 4, "wheel", 0x102030, 100, 0);
        Assert.Equal(new byte[] { 5, 12, 0x72, 1, 4, 4, 0x10, 0x20, 0x30, 63, 63, 3, 1 }, rgb);
        var fan = LegionProtocol.FanTable([44, 48, 55, 60, 71, 79, 87, 87, 100, 100]);
        Assert.Equal(64, fan.Length);
        Assert.Equal(1, fan[0]);
        Assert.Equal(44, BinaryPrimitives.ReadUInt16LittleEndian(fan.AsSpan(6)));
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(fan.AsSpan(24)));
        Assert.Equal(1500, LegionProtocol.FanPercentToRpm(0));
        Assert.Equal(5200, LegionProtocol.FanPercentToRpm(100));
    }

    [Fact]
    public void TabletMotionUsesChosenControllerAndOemWheelDirectionsAreExact()
    {
        var data = new byte[64];
        data[0] = 0x74;
        data[12] = 2;
        data[13] = 3;
        data[18] = 0xC0;
        data[20] = 0x84;
        data[25] = 129;
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(35), 1000);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(50), 2000);
        Assert.True(LegionInputCodec.TryDecode(LegionGoModels.All[0], data, DateTimeOffset.UtcNow, false, false,
            out var left));
        Assert.True(LegionInputCodec.TryDecode(LegionGoModels.All[0], data, DateTimeOffset.UtcNow, true, false,
            out var right));
        Assert.Equal(-1000 * (4f / short.MaxValue), left.Sample.Motion!.Value.AccelX);
        Assert.Equal(-2000 * (4f / short.MaxValue), right.Sample.Motion!.Value.AccelX);
        Assert.NotEqual(0, left.OemButtons & 2048);
        Assert.Equal(0, left.OemButtons & 4096);
        data[25] = 128;
        Assert.True(LegionInputCodec.TryDecode(LegionGoModels.All[0], data, DateTimeOffset.UtcNow, false, false,
            out var idle));
        Assert.Equal(0, idle.OemButtons & (2048 | 4096));
    }

    [Fact]
    public void LongTouchPublishesASeparateHeldGestureAndReleasesOnLift()
    {
        var touch = new LegionTouchState();
        var time = DateTimeOffset.UtcNow;
        var down = new CanonicalControllerSample
            { Timestamp = time, Buttons = CanonicalButtons.RightPadTouch, RightPadX = 0.2f, RightPadY = 0.2f };
        _ = touch.Apply(down);
        Assert.False(touch.SecondaryHeld);
        var held = touch.Apply(down with { Timestamp = time.AddMilliseconds(501), RightPadForce = 1 });
        Assert.True(touch.SecondaryHeld);
        Assert.Equal(CanonicalButtons.None, held.Buttons & CanonicalButtons.RightPadTouch);
        Assert.Equal(0, held.RightPadForce);
        _ = touch.Apply(CanonicalControllerSample.Neutral(time.AddMilliseconds(502)));
        Assert.False(touch.SecondaryHeld);
        var idle = touch.Apply(CanonicalControllerSample.Neutral(time.AddMilliseconds(1100)));
        Assert.Equal(CanonicalButtons.None, idle.Buttons & CanonicalButtons.RightPadClick);
    }

    [Theory]
    [InlineData(0x80, CanonicalButtons.RearPaddle1, 4)]
    [InlineData(0x40, CanonicalButtons.RearPaddle3, 8)]
    [InlineData(0x04, CanonicalButtons.RearPaddle2, 16)]
    [InlineData(0x20, CanonicalButtons.RearPaddle4, 32)]
    public void TabletPaddlesFollowL4R4L5R5WhileOemIdsRetainTheirOrder(byte mask,
        CanonicalButtons expected, ushort oem)
    {
        var report = new byte[64];
        report[0] = 0x74;
        report[20] = mask;
        foreach (var model in LegionGoModels.All.Where(item => !item.Slim))
        {
            Assert.True(LegionInputCodec.TryDecode(model, report, DateTimeOffset.UnixEpoch,
                rightGyro: false, nativeTouchpad: false, out var decoded));
            Assert.Equal(expected, decoded.Sample.Buttons);
            Assert.Equal(oem, decoded.OemButtons);
        }
    }

    [Fact]
    public void SingleTapUsesConfiguredDelayAndPulsesForOneSample()
    {
        var touch = new LegionTouchState(doubleTapMilliseconds: 200, longTapMilliseconds: 350);
        var down = TabletTouch(DateTimeOffset.UnixEpoch);
        _ = touch.Apply(down);
        _ = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(10)));
        var boundary = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(210)));
        Assert.Equal(CanonicalButtons.None, boundary.Buttons & CanonicalButtons.RightPadClick);
        var pulse = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(211)));
        Assert.Equal(CanonicalButtons.RightPadClick, pulse.Buttons & CanonicalButtons.RightPadClick);
        var after = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(212)));
        Assert.Equal(CanonicalButtons.None, after.Buttons & CanonicalButtons.RightPadClick);
    }

    [Fact]
    public void DoubleTapIncludesConfiguredBoundaryAndHoldsTouchAndClickDuringDragging()
    {
        var touch = new LegionTouchState(doubleTapMilliseconds: 200, longTapMilliseconds: 350);
        var down = TabletTouch(DateTimeOffset.UnixEpoch);
        _ = touch.Apply(down);
        _ = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(10)));
        var second = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(210) });
        Assert.Equal(CanonicalButtons.RightPadTouch | CanonicalButtons.RightPadClick, second.Buttons);
        var dragged = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(900), RightPadX = 0.9f });
        Assert.Equal(CanonicalButtons.RightPadTouch | CanonicalButtons.RightPadClick, dragged.Buttons);
        Assert.False(touch.SecondaryHeld);
        var up = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(901)));
        Assert.Equal(CanonicalButtons.None, up.Buttons);
        var idle = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(1200)));
        Assert.Equal(CanonicalButtons.None, idle.Buttons);
    }

    [Fact]
    public void ConfiguredLongPressSuppressesContactAtTheExactThresholdAndResetReleasesIt()
    {
        var touch = new LegionTouchState(doubleTapMilliseconds: 200, longTapMilliseconds: 350);
        var down = TabletTouch(DateTimeOffset.UnixEpoch);
        _ = touch.Apply(down);
        var before = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(349) });
        Assert.Equal(CanonicalButtons.RightPadTouch, before.Buttons);
        Assert.False(touch.SecondaryHeld);
        var held = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(350) });
        Assert.Equal(CanonicalButtons.None, held.Buttons);
        Assert.Equal(0, held.RightPadForce);
        Assert.True(touch.SecondaryHeld);
        touch.Reset();
        Assert.False(touch.SecondaryHeld);
        Assert.Equal(CanonicalButtons.None,
            touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(1000))).Buttons);
    }

    [Fact]
    public void MovementPreventsSingleAndLongClicksButStillAllowsTheHcDoubleTapWindow()
    {
        var touch = new LegionTouchState(doubleTapMilliseconds: 200, longTapMilliseconds: 350);
        var down = TabletTouch(DateTimeOffset.UnixEpoch);
        var moved = down with { Timestamp = down.Timestamp.AddMilliseconds(350), RightPadX = 0.5f };
        _ = touch.Apply(down);
        _ = touch.Apply(moved);
        Assert.False(touch.SecondaryHeld);
        _ = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(351)));
        var idle = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(552)));
        Assert.Equal(CanonicalButtons.None, idle.Buttons);
        touch.Reset();
        _ = touch.Apply(down);
        _ = touch.Apply(moved);
        _ = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(351)));
        var second = touch.Apply(moved with { Timestamp = down.Timestamp.AddMilliseconds(551) });
        Assert.Equal(CanonicalButtons.RightPadTouch | CanonicalButtons.RightPadClick, second.Buttons);
    }

    [Fact]
    public void ZeroWindowsTimingUsesHcSingleTapAndLongPressFallbacks()
    {
        var touch = new LegionTouchState(doubleTapMilliseconds: 0, longTapMilliseconds: 0);
        var down = TabletTouch(DateTimeOffset.UnixEpoch);
        _ = touch.Apply(down);
        _ = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(10)));
        var pulse = touch.Apply(CanonicalControllerSample.Neutral(down.Timestamp.AddMilliseconds(311)));
        Assert.Equal(CanonicalButtons.RightPadClick, pulse.Buttons);
        touch.Reset();
        _ = touch.Apply(down);
        _ = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(499) });
        Assert.False(touch.SecondaryHeld);
        _ = touch.Apply(down with { Timestamp = down.Timestamp.AddMilliseconds(500) });
        Assert.True(touch.SecondaryHeld);
    }

    private static CanonicalControllerSample TabletTouch(DateTimeOffset timestamp)
    {
        return new CanonicalControllerSample
        {
            Timestamp = timestamp, Buttons = CanonicalButtons.RightPadTouch,
            RightPadX = 0.2f, RightPadY = 0.2f, RightPadForce = 1
        };
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public async Task PowerPairWritesFourLimitsAndRestoresTheCapturedModeWithoutOverridingOemLimits(int originalMode)
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FakeWmi { Mode = originalMode };
        var hid = new FakeHid();
        var host = new TestPluginHostAdapter(1);
        await using var engine = Engine("83E1", wmi, hid);
        await engine.StartAsync(Start(host, directory, "83E1", false), CancellationToken.None);
        var surface = host.DescriptorSets[^1];
        Assert.True(DevicePowerPair.TryValidate(surface.Descriptors, out _));
        Assert.True(DevicePowerPreset.TryValidate(surface.Descriptors, out _));
        _ = await engine.ExecuteCommandAsync(Command(surface, CapabilityIds.Scenario, CapabilityValue.Choice("quiet")),
            CancellationToken.None);
        var result = await engine.ExecuteCommandAsync(
            Command(surface, CapabilityIds.PowerSustained, CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 25
            }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(20, wmi.Values[LegionProtocol.LongPower]);
        Assert.Equal(20, wmi.Values[LegionProtocol.CrossPower]);
        Assert.Equal(25, wmi.Values[LegionProtocol.ShortPower]);
        Assert.Equal(25, wmi.Values[LegionProtocol.PeakPower]);
        var writesBeforeStop = wmi.Writes.Count;
        wmi.ControlReads.Clear();
        _ = await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.Empty(wmi.ControlReads);
        Assert.Equal(originalMode, wmi.Mode);
        var restoration = wmi.Writes.Skip(writesBeforeStop).ToArray();
        if (originalMode == 255)
        {
            Assert.Equal(new (int? Feature, int Value)[]
            {
                (LegionProtocol.LongPower, 15), (LegionProtocol.CrossPower, 15),
                (LegionProtocol.ShortPower, 15), (LegionProtocol.PeakPower, 15), (null, 255)
            }, restoration);
            Assert.All(
                wmi.Values.Where(p => p.Key is LegionProtocol.LongPower or LegionProtocol.CrossPower
                    or LegionProtocol.ShortPower or LegionProtocol.PeakPower), p => Assert.Equal(15, p.Value));
        }
        else
        {
            // Lenovo's OEM mode owns its limits. Replaying captured raw limits would switch it back to Custom.
            Assert.Equal(new (int? Feature, int Value)[] { (null, originalMode) }, restoration);
        }
    }

    [Fact]
    public async Task MissingReadbackDoesNotBlockWritesOrInventVerification()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FakeWmi { HideReadbackAfterWrite = true };
        var host = new TestPluginHostAdapter(1);
        await using var engine = Engine("83E1", wmi, new FakeHid());
        await engine.StartAsync(Start(host, directory, "83E1", false), CancellationToken.None);
        Assert.Empty(wmi.ControlReads);
        var result = await engine.ExecuteCommandAsync(Command(host.DescriptorSets[^1], CapabilityIds.PowerSustained,
                CapabilityValue.Integer(20)) with
            {
                PairedPowerLimitWatts = 25
            }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(20, wmi.Values[LegionProtocol.LongPower]);
        Assert.Equal(5, wmi.ControlReads.Count);
        wmi.ControlReads.Clear();
        _ = await engine.ExecuteCommandAsync(Command(host.DescriptorSets[^1], CapabilityIds.ChargeLimit,
            CapabilityValue.Boolean(true)), CancellationToken.None);
        Assert.Equal(20, host.CapabilityStates.Last(state => state.CapabilityId == CapabilityIds.PowerSustained)
            .ObservedValue!.IntegerValue);
        _ = await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.Empty(wmi.ControlReads);
    }

    [Fact]
    public async Task ControllerToggleIsIndependentOfLightingAndPowerAndNeedsNoRestart()
    {
        using var directory = new TemporaryDirectory();
        var hid = new FakeHid();
        var host = new TestPluginHostAdapter(1);
        await using var engine = Engine("83E1", new FakeWmi(), hid);
        await engine.StartAsync(Start(host, directory, "83E1", true), CancellationToken.None);
        Assert.True(hid.Reading);
        await engine.SetControllerManagementAsync(new EngineControllerManagementContext(false, Deadline.Never),
            CancellationToken.None);
        Assert.False(hid.Reading);
        Assert.True(hid.Connected);
        Assert.True(hid.Listening);
        Assert.False(host.OemControlSets[^1][0].RequiresControllerAcquisition);
        Assert.True(host.OemControlSets[^1][2].RequiresControllerAcquisition);
        var result = await engine.ExecuteCommandAsync(
            Command(host.DescriptorSets[^1], CapabilityIds.LightingBrightness, CapabilityValue.Integer(50)),
            CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        await engine.SetControllerManagementAsync(new EngineControllerManagementContext(true, Deadline.Never),
            CancellationToken.None);
        Assert.True(hid.Reading);
        Assert.Single(host.DescriptorSets);
        _ = await engine.StopAsync(new EngineStopContext(Deadline.Never),
            CancellationToken.None);
        Assert.False(hid.Connected);
    }

    private static DeviceIdentitySnapshot Identity(string product)
    {
        return new DeviceIdentitySnapshot
            { SystemManufacturer = "LENOVO", SystemProduct = product, BiosVersion = "test-bios" };
    }

    private static ServiceEngineFixture Engine(string product, FakeWmi wmi, FakeHid hid)
    {
        return new ServiceEngineFixture(context => new LegionGoDeviceEngine(context,
            new LegionHardware(() => Identity(product), () => wmi, () => hid,
                () => new FakeEc())));
    }

    private static EngineStartContext Start(TestPluginHostAdapter host, TemporaryDirectory directory, string product,
        bool controller)
    {
        return new EngineStartContext
        {
            Host = host, Identity = Identity(product), StateDirectory = directory.Root,
            Journal = TestRecovery.Open(directory.Root),
            DeviceDefinitionId = LegionGoModels.Match(Identity(product))!.DefinitionId,
            ControllerManagementEnabled = controller
        };
    }

    private static CapabilityCommand Command(CapabilityDescriptorSet surface, string id, CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = value,
            Deadline = Deadline.Never
        };
    }

    private sealed class FakeWmi : ILegionWmi
    {
        private ushort[] _fanTable = [44, 48, 55, 60, 71, 79, 87, 87, 100, 100];
        private bool _wrote;

        public Dictionary<int, int> Values { get; } = new()
        {
            [LegionProtocol.LongPower] = 15, [LegionProtocol.CrossPower] = 15,
            [LegionProtocol.ShortPower] = 15, [LegionProtocol.PeakPower] = 15,
            [LegionProtocol.FullFan] = 0, [LegionProtocol.Temperature] = 55,
            [LegionProtocol.FanRpm] = 2000, [LegionProtocol.ChargeProtection] = 0
        };

        public int Mode { get; set; } = 2;
        public List<(int? Feature, int Value)> Writes { get; } = [];
        public List<int?> ControlReads { get; } = [];
        public bool HideReadbackAfterWrite { get; init; }
        public bool FeaturesAvailable => true;
        public bool ModesAvailable => true;
        public bool FansAvailable => true;

        public int? ReadFeature(int id)
        {
            if (id is not (LegionProtocol.Temperature or LegionProtocol.FanRpm))
            {
                ControlReads.Add(id);
            }

            return _wrote && HideReadbackAfterWrite ? null : Values.GetValueOrDefault(id);
        }

        public void WriteFeature(int id, int value)
        {
            Writes.Add((id, value));
            Values[id] = value;
            _wrote = true;
        }

        public int? ReadMode()
        {
            ControlReads.Add(null);
            return _wrote && HideReadbackAfterWrite ? null : Mode;
        }

        public void WriteMode(int value)
        {
            Writes.Add((null, value));
            Mode = value;
            _wrote = true;
        }

        public ushort[]? ReadFanTable()
        {
            ControlReads.Add(LegionProtocol.FullFan);
            return _fanTable.ToArray();
        }

        public void WriteFanTable(ushort[] values)
        {
            _fanTable = values.ToArray();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeHid : ILegionHid
    {
        public List<byte[]> Reports { get; } = [];

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public bool Connected { get; private set; }
        public bool Reading { get; private set; }
        public bool Listening { get; private set; }
        public bool RumbleAvailable => Reading;
        public string? Binding => Connected ? "physical-port|test-firmware" : null;
        public bool RightGyro { get; set; }
        public bool NativeTouchpad { get; set; }

        public ValueTask<bool> OpenAsync(LegionGoModel model, CancellationToken cancellationToken)
        {
            Connected = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken)
        {
            Reports.Add(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(IDeviceSink sink, CancellationToken cancellationToken)
        {
            Reading = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ListenAsync(IDeviceSink sink, CancellationToken cancellationToken)
        {
            Listening = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Reading = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            Reading = false;
            Listening = false;
            Connected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask RumbleAsync(HapticOutputFrame output, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Reading = false;
            Listening = false;
            Connected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEc : IEmbeddedController
    {
        private readonly Dictionary<ushort, byte> _bytes = [];

        public byte ReadByte(ushort address)
        {
            return _bytes.GetValueOrDefault(address);
        }

        public ushort ReadUInt16(ushort address)
        {
            return (ushort)(ReadByte(address) | (ReadByte((ushort)(address + 1)) << 8));
        }

        public void WriteByte(ushort address, byte value)
        {
            _bytes[address] = value;
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
