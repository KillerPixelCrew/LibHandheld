using LibHandheld.Families.Acer;

namespace LibHandheld.Tests.Acer;

public sealed class AcerLightingTests
{
    [Fact]
    public void BrightnessChangesOnlyTheSelectedAnimationByte()
    {
        var retained = Lighting();
        var changed = AcerLightingService.Edit(retained, Command("lighting.brightness", CapabilityValue.Integer(45)));
        var expected = (byte[])retained.Clone();
        expected[4] = 45;
        Assert.Equal(expected, changed);
        Assert.Equal(50, retained[4]);
    }

    [Fact]
    public void SharedColorChangesEightVisibleSegmentsAndRetainsTheUnassignedSlot()
    {
        var retained = Lighting();
        retained[1] = 4;
        var changed = AcerLightingService.Edit(retained, Command("lighting.color", CapabilityValue.Color(0x123456)));
        Assert.Equal(1, changed[1]);
        for (var index = 0; index < 8; index++)
        {
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, changed.AsSpan(5 + index * 3, 3).ToArray());
        }

        Assert.Equal(retained.AsSpan(29).ToArray(), changed.AsSpan(29).ToArray());
        Assert.Equal(retained[2], changed[2]);
        Assert.Equal(retained[3], changed[3]);
        Assert.Equal(retained[4], changed[4]);
    }

    [Theory]
    [InlineData("left.top-left", 6)]
    [InlineData("left.top-right", 5)]
    [InlineData("left.bottom-left", 7)]
    [InlineData("left.bottom-right", 4)]
    [InlineData("right.top-left", 1)]
    [InlineData("right.top-right", 0)]
    [InlineData("right.bottom-left", 2)]
    [InlineData("right.bottom-right", 3)]
    public void SpatialColorChangesOnlyItsMeasuredSlot(string zone, int index)
    {
        var retained = Lighting();
        var command = Command("lighting.color", CapabilityValue.Color(0x123456)) with { InstanceId = zone };
        var changed = AcerLightingService.Edit(retained, command);
        var expected = (byte[])retained.Clone();
        new byte[] { 0x12, 0x34, 0x56 }.CopyTo(expected, 5 + index * 3);
        Assert.Equal(expected, changed);
    }

    [Fact]
    public void UnassignedSlotCannotBecomeAUserZone()
    {
        Assert.Throws<ArgumentException>(() => AcerLightingService.Edit(Lighting(),
            Command("lighting.color", CapabilityValue.Color(0x123456)) with { InstanceId = "slot-9" }));
    }

    [Fact]
    public void BreathingRetainsTheUnassignedSlotInBothFrames()
    {
        var retained = Lighting();
        var changed = AcerLightingService.Edit(retained, Command("lighting.effect", CapabilityValue.Choice("breathing")));
        var expected = (byte[])retained.Clone();
        expected[1] = 2;
        expected.AsSpan(32, 24).Clear();
        Assert.Equal(expected, changed);
    }

    [Fact]
    public void ProfileWriterCannotAddressButtonsOrSendRomCommands()
    {
        Assert.Equal(Convert.FromHexString("0F00003C2101024E01"),
            AcerControllerTransport.Packet(command: 0x21, offset: 590, count: 1).AsSpan(0, 9).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerControllerTransport.Packet(command: 0x22, offset: 590, count: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerControllerTransport.Packet(command: 0x21, offset: 0, count: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerControllerTransport.Packet(command: 0x21, offset: 1469, count: 1));
    }

    [Fact]
    public void UnknownProfileShapeCannotBecomeAWrite()
    {
        var retained = Lighting();
        retained[0] = 4;
        Assert.Throws<IOException>(() => AcerLightingService.Edit(retained,
            Command("lighting.brightness", CapabilityValue.Integer(45))));
    }

    private static byte[] Lighting()
    {
        var bytes = Enumerable.Repeat((byte)0xA5, AcerControllerTransport.LightingLength).ToArray();
        bytes[0] = 0;
        bytes[1] = 1;
        bytes[2] = 9;
        bytes[3] = 3;
        bytes[4] = 50;
        return bytes;
    }

    private static CapabilityCommand Command(string id, CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = id, RequestedValue = value, Deadline = Deadline.Never
        };
    }
}
