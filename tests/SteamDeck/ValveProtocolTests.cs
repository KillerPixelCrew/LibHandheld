using System.Buffers.Binary;
using LibHandheld.Families.SteamDeck;

namespace LibHandheld.Tests.SteamDeck;

public sealed class ValveProtocolTests
{
    [Theory]
    [InlineData("Valve", "Jupiter", "steamdeck-lcd")]
    [InlineData("Valve", "Galileo", "steamdeck-oled")]
    [InlineData("Valve", "Fremont", "steam-machine")]
    [InlineData("Valve", "Steam Deck", null)]
    [InlineData("Other", "Jupiter", null)]
    public void FactoryRequiresExactMachineIdentity(string manufacturer, string product, string? expected)
    {
        Assert.Equal(expected, SteamDeckModels.Match(new DeviceIdentitySnapshot
            { SystemManufacturer = manufacturer, SystemProduct = product }));
        Assert.False(SteamDeckModels.Definitions.Single(model => model.Id == "steam-machine").HasController);
    }

    [Fact]
    public void OnlyTheDecksDeclareANativeSteamController()
    {
        Assert.Equal(["steamdeck-lcd", "steamdeck-oled"], SteamDeckModels.Definitions
            .Where(model => model.NativeSteamController)
            .Select(model => model.Id));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void DecoderPreservesFullNeptunePhysicalState(int size)
    {
        var input = Packet();
        input[8] = 0xFC;
        input[9] = 0xFF;
        input[10] = 0x5F;
        input[11] = 4;
        input[13] = 0xC6;
        input[14] = 4;
        Short(input, 16, 12000);
        Short(input, 18, -8000);
        Short(input, 20, -9000);
        Short(input, 22, 10000);
        Short(input, 24, 1000);
        Short(input, 26, 2000);
        Short(input, 28, 16000);
        Short(input, 30, 100);
        Short(input, 32, 200);
        Short(input, 34, 300);
        Short(input, 44, 16384);
        Short(input, 46, 32767);
        Short(input, 48, 32767);
        Short(input, 50, -32768);
        Short(input, 52, -16000);
        Short(input, 54, 14000);
        Short(input, 56, 20000);
        Short(input, 58, 22000);
        var report = size == 64 ? input : new byte[] { 0 }.Concat(input).ToArray();
        Assert.True(NeptuneReport.TryDecode(report, DateTimeOffset.UnixEpoch, out var sample));
        Assert.Equal((CanonicalButtons)((1u << 26) - 1), sample.Buttons);
        Assert.Equal(1, sample.LeftStickX);
        Assert.Equal(-1, sample.LeftStickY);
        Assert.InRange(sample.LeftTrigger, 0.49f, 0.51f);
        Assert.Equal(1, sample.RightTrigger);
        Assert.InRange(sample.LeftPadX, 0.36f, 0.37f);
        Assert.True(sample.RightPadForce > sample.LeftPadForce);
        Assert.Equal(1, sample.LeftStickForce);
        Assert.Equal(1, sample.RightStickForce);
        Assert.True(sample.Motion!.Value.HasGyro);
        Assert.True(sample.Motion.Value.HasAccelerometer);
        Assert.True(sample.Motion.Value.GyroX > 0);
        Assert.True(sample.Motion.Value.GyroY > sample.Motion.Value.GyroX);
        Assert.True(sample.Motion.Value.GyroZ < 0);
        Assert.True(sample.Motion.Value.AccelZ < 0);
    }

    [Fact]
    public void DecoderRejectsOtherReportsAndRetractsUntouchedPadCoordinates()
    {
        var input = Packet();
        Short(input, 16, 32767);
        Short(input, 22, 32767);
        Assert.True(NeptuneReport.TryDecode(input, DateTimeOffset.UnixEpoch, out var sample));
        Assert.Equal(0, sample.LeftPadX);
        Assert.Equal(0, sample.RightPadY);
        input[2] = 1;
        Assert.False(NeptuneReport.TryDecode(input, DateTimeOffset.UnixEpoch, out _));
        Assert.False(NeptuneReport.TryDecode(new byte[63], DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void NativeHapticPulseRetainsReportIdStructAlignmentAndBothTimestamps()
    {
        var report = new byte[65];
        NeptuneReport.EncodeHapticPulse(report,
            new NativeHapticPulse(HapticActuatorSide.Left, HapticPulseStyle.Strong, -7), unchecked((int)0xABCDEF12));
        Assert.Equal(0, report[0]);
        Assert.Equal(0xEA, report[1]);
        Assert.Equal(13, report[2]);
        Assert.Equal(1, report[3]);
        Assert.Equal(2, report[4]);
        Assert.Equal(0, report[5]);
        Assert.Equal(0xF9, report[6]);
        Assert.Equal(4, report[7]);
        Assert.Equal(0, report[8]);
        Assert.Equal(unchecked((int)0xABCDEF12), BinaryPrimitives.ReadInt32LittleEndian(report.AsSpan(9)));
        Assert.Equal(unchecked((int)0xABCDEF12), BinaryPrimitives.ReadInt32LittleEndian(report.AsSpan(13)));
        Assert.All(report.Skip(17), value => Assert.Equal(0, value));
        NeptuneReport.EncodeHapticPulse(report,
            NeptuneReport.MotorPulse(HapticActuatorSide.Right, 0), timestamp: 5);
        Assert.Equal(0, report[3]);
        Assert.Equal(0, report[4]);
        Assert.Equal(0, report[6]);
    }

    [Theory]
    [InlineData(0, HapticPulseStyle.Disabled, 0)]
    [InlineData(1f / 255, HapticPulseStyle.Weak, -6)]
    [InlineData(128f / 255, HapticPulseStyle.Weak, 0)]
    [InlineData(1, HapticPulseStyle.Weak, 5)]
    [InlineData(2, HapticPulseStyle.Weak, 5)]
    [InlineData(float.NaN, HapticPulseStyle.Disabled, 0)]
    public void MotorEnvelopeUsesHcSignedIntensity(float intensity, HapticPulseStyle style, sbyte expected)
    {
        Assert.Equal(new NativeHapticPulse(HapticActuatorSide.Left, style, expected),
            NeptuneReport.MotorPulse(HapticActuatorSide.Left, intensity));
    }

    [Theory]
    [InlineData(ButtonHapticStrength.Low, false, HapticPulseStyle.Weak, -7)]
    [InlineData(ButtonHapticStrength.Low, true, HapticPulseStyle.Weak, -7)]
    [InlineData(ButtonHapticStrength.Medium, false, HapticPulseStyle.Strong, -1)]
    [InlineData(ButtonHapticStrength.Medium, true, HapticPulseStyle.Strong, -4)]
    [InlineData(ButtonHapticStrength.High, false, HapticPulseStyle.Strong, 5)]
    [InlineData(ButtonHapticStrength.High, true, HapticPulseStyle.Strong, 2)]
    public void ButtonFeedbackReversesActuatorAndSoftensRelease(ButtonHapticStrength strength, bool released,
        HapticPulseStyle style, sbyte intensity)
    {
        Assert.Equal(new NativeHapticPulse(HapticActuatorSide.Right, style, intensity),
            NativeHapticPulse.ForButton(CanonicalButtons.RearPaddle1, strength, released));
        Assert.Equal(new NativeHapticPulse(HapticActuatorSide.Left, style, intensity),
            NativeHapticPulse.ForButton(CanonicalButtons.A, strength, released));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeHapticPulse.ForButton(CanonicalButtons.A | CanonicalButtons.B, strength, released));
    }

    [Fact]
    public void MemoryAdapterLimitsAccessToExactValveFieldsAndSelectedGpuMailbox()
    {
        Assert.True(SteamDeckMemory.IsAllowed(0xFE700B92, 16, null));
        Assert.False(SteamDeckMemory.IsAllowed(0xFE700B92, 32, null));
        Assert.False(SteamDeckMemory.IsAllowed(0x80358A08, 32, null));
        Assert.True(SteamDeckMemory.IsAllowed(0x80358A08, 32, 0x80300000));
        Assert.False(SteamDeckMemory.IsAllowed(0x80558A08, 32, 0x80300000));
        Assert.False(SteamDeckMemory.IsAllowed(0xFE700000, 8, null));
        Assert.True(SteamDeckMemory.IsAllowed(0xFE700C01, 8, null));
        Assert.True(SteamDeckMemory.IsAllowed(0xFE700B9F, 32, null));
        Assert.True(SteamDeckMemory.IsAllowed(0xFE700B9F, 8, null));
    }

    [Fact]
    public void LiveClockGettersHaveDistinctHcCommandsAndCannotBecomeLimitSetters()
    {
        Assert.Equal(0x1Bu, SteamDeckNativeService.ClockMeasurementCommand("steamdeck.gpu-clock-current"));
        Assert.Equal(0x1Cu, SteamDeckNativeService.ClockMeasurementCommand("steamdeck.fabric-clock-current"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SteamDeckNativeService.ClockMeasurementCommand("steamdeck.gpu-clock-min"));
    }

    [Fact]
    public void FirmwareTableRetainsChargeAndBatteryEndianQuirks()
    {
        Assert.False(DeckFirmware.Match(0xB030, 6)!.Value.ChargeLimit);
        Assert.True(DeckFirmware.Match(0xB030, 10)!.Value.ChargeLimit);
        Assert.False(DeckFirmware.Match(0x1030, 5)!.Value.ChargeLimit);
        Assert.Null(DeckFirmware.Match(0x1050, 6));
        Assert.Null(DeckFirmware.Match(0xFFFF, 5));
        Assert.Null(DeckFirmware.Match(0x1050, 5, 1));
        Assert.Equal(0, DeckFirmware.Match(0x1050, 5)!.Value.BatteryTemperature(0x0AAC));
        Assert.Equal(0, DeckFirmware.Match(0xB030, 6)!.Value.BatteryTemperature(0xAC0A));
    }

    [Fact]
    public void VangoghClockCodesAndBoundsAreModelSpecific()
    {
        Assert.Equal((0x20u, 200u), SteamDeckNativeService.ClockCommand("steamdeck.gpu-clock-min", null, 200));
        Assert.Equal((0x41u, (3u << 20) | 3500),
            SteamDeckNativeService.ClockCommand("steamdeck.cpu-clock-max", "core3", 3500));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SteamDeckNativeService.ClockCommand("steamdeck.cpu-clock-max", "core4", 3500));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SteamDeckNativeService.ClockCommand("steamdeck.gpu-clock-min", null, 1901));
        Assert.Throws<ArgumentOutOfRangeException>(() => VangoghPower.EncodeWatts(22));
    }

    [Fact]
    public void FremontGradientRetainsAllSeventeenDistinctLedPositions()
    {
        Assert.Equal(0xFF0000, SteamMachineLighting.Blend(0xFF0000, 0x0000FF, 0));
        Assert.Equal(0x80007F, SteamMachineLighting.Blend(0xFF0000, 0x0000FF, 8));
        Assert.Equal(0x0000FF, SteamMachineLighting.Blend(0xFF0000, 0x0000FF, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamMachineLighting.Blend(0, 0, 17));
    }

    internal static byte[] Packet()
    {
        var packet = new byte[64];
        packet[0] = 1;
        packet[2] = 9;
        packet[3] = 64;
        return packet;
    }

    private static void Short(byte[] packet, int offset, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(offset), value);
    }
}
