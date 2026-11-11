using LibHandheld.Families.Acer;
using LibHandheld.Internal.Services;

namespace LibHandheld.Tests.Acer;

public sealed class AcerRingLightsTests
{
    private static readonly AcerRingTarget[] EightLeds = [new(0x41, 8, 5)];

    [Fact]
    public void TargetsComeFromTheListReply()
    {
        Assert.Equal(new byte[] { 0x41, 0x42 },
            AcerRingLights.ParseTargets(new byte[] { 0xA1, 2, 0x41, 0x42, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(0xA2, 1)]
    [InlineData(0xA1, 0)]
    [InlineData(0xA1, 10)]
    public void UnusableListRepliesAreRefused(int report, int count)
    {
        var reply = new byte[AcerRingLights.ReportLength];
        reply[0] = (byte)report;
        reply[1] = (byte)count;
        Assert.Throws<IOException>(() => AcerRingLights.ParseTargets(reply));
    }

    [Fact]
    public void CapabilityReadsSpeedAndLedCountFromTheMeasuredLayout()
    {
        var reply = new byte[] { 0xA3, 0x80, 0x01, 0x05, 0x01, 0x3B, 0, 0, 0, 0, 0 };
        Assert.Equal(new AcerRingTarget(0x80, 1, 5), AcerRingLights.ParseCapability(0x80, reply));
    }

    [Fact]
    public void CapabilityForAnotherTargetIsRefused()
    {
        var reply = new byte[] { 0xA3, 0x41, 0x01, 0x05, 0x08, 0x3B, 0, 0, 0, 0, 0 };
        Assert.Throws<IOException>(() => AcerRingLights.ParseCapability(0x80, reply));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void UnusableLedCountFallsBackToTheEightVisibleSegments(int count)
    {
        var reply = new byte[] { 0xA3, 0x41, 0x01, 0x05, (byte)count, 0x3B, 0, 0, 0, 0, 0 };
        Assert.Equal(8, AcerRingLights.ParseCapability(0x41, reply).LedCount);
    }

    [Fact]
    public void PacketUsesTheRearLogoFieldOrder()
    {
        Assert.Equal(AcerLogoLightingService.Packet("solid", 50, 3, 0x123456),
            AcerRingLights.Packet(0x80, 2, 50, 3, 0x123456, 1));
    }

    [Fact]
    public void SteadyColorsAreOneWritePerLedWithItsOwnMaskBit()
    {
        var lighting = Lighting(effect: 1);
        var frames = AcerRingLights.Frames(EightLeds, lighting);

        Assert.Equal(8, frames.Count);
        for (var led = 0; led < 8; led++)
        {
            var color = lighting.AsSpan(5 + led * 3, 3);
            Assert.Equal(new byte[] { 0xA4, 0x41, 2, 50, 0, 0, color[0], color[1], color[2], (byte)(1 << led), 0 }, frames[led]);
        }
    }

    [Fact]
    public void BreathingIsOneWritePerTargetWithNoMask()
    {
        var frames = AcerRingLights.Frames(EightLeds, Lighting(effect: 2));

        var frame = Assert.Single(frames);
        // Wire speed 3 is UI speed 17, which scales to 4 of the target's 5.
        Assert.Equal(new byte[] { 0xA4, 0x41, 4, 50, 4, 0, 10, 11, 12, 0, 0 }, frame);
    }

    [Fact]
    public void TargetsShareTheRetainedSlotsInOrder()
    {
        var frames = AcerRingLights.Frames([new AcerRingTarget(0x41, 4, 5), new AcerRingTarget(0x42, 4, 5)], Lighting(effect: 1));

        Assert.Equal(8, frames.Count);
        Assert.Equal(new byte[] { 0xA4, 0x42, 2, 50, 0, 0, 22, 23, 24, 1, 0 }, frames[4]);
    }

    [Fact]
    public void LedsPastTheRetainedSlotsRepeatTheFirstColor()
    {
        var frames = AcerRingLights.Frames([new AcerRingTarget(0x41, 10, 5)], Lighting(effect: 1));

        Assert.Equal(new byte[] { 0xA4, 0x41, 2, 50, 0, 0, 10, 11, 12, 0, 2 }, frames[9]);
    }

    [Fact]
    public void DarkFramesBlackOutEveryLed()
    {
        var frames = AcerRingLights.DarkFrames(EightLeds);

        Assert.Equal(8, frames.Count);
        Assert.All(frames, frame => Assert.Equal(new byte[] { 0xA4, 0x41, 2, 0, 0, 0, 0, 0, 0 }, frame[..9]));
        Assert.Equal(new byte[] { 0x80, 0 }, frames[7][9..]);
    }

    [Fact]
    public void BreathingSpeedScalesToTheTopSpeedTheAtlas7Reports()
    {
        // Measured reply: A3 45 01 0E 01 7F 00 01 00 00 00. Wire speed 3 is UI speed 17, and (17 x 14 + 10) / 20 is 12.
        var target = AcerRingLights.ParseCapability(0x45, new byte[] { 0xA3, 0x45, 0x01, 0x0E, 0x01, 0x7F, 0, 0x01, 0, 0, 0 });

        Assert.Equal(new AcerRingTarget(0x45, 1, 14), target);
        Assert.Equal(12, AcerRingLights.Frames([target], Lighting(effect: 2))[0][4]);
    }

    [Fact]
    public async Task WritingWithoutAnOpenCollectionIsAFailureNotASilentSuccess()
    {
        var rings = new AcerRingLights();

        await Assert.ThrowsAsync<IOException>(async () => await rings.ShowAsync(Lighting(effect: 1), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () => await rings.DarkenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARingWriteIsMarkedDispatchedBeforeItsFirstReport()
    {
        var port = new FakePort { FailingWrites = 1 };
        var rings = new AcerRingLights(() => port);
        Assert.True(rings.Open());
        var dispatched = false;

        await Assert.ThrowsAsync<IOException>(async () =>
            await rings.ShowAsync(Lighting(effect: 2), CancellationToken.None, () => dispatched = true));

        Assert.True(dispatched);
    }

    [Fact]
    public async Task AFailedFirstOutputAfterOpeningIsUncertainAndKeepsTheRingsForTheUser()
    {
        var port = new FakePort { FailingWrites = 1 };
        var rings = new AcerRingLights(() => port);
        var service = new AcerLightingService(null!, null!, rings);

        var outcome = await service.OpenRingsAsync(Lighting(effect: 1), CancellationToken.None);

        Assert.Equal(AcerLightingService.RingOutput.Uncertain, outcome);
        Assert.False(port.Disposed);
        Assert.Single(port.Writes, report => report[0] == 0xA4);

        // Every control stays usable for the user and carries the reason WSGM's automatic restore skips.
        Assert.Equal(DeviceServiceState.Owned, service.ResultFor(outcome).State);
        Assert.All(service.States, state =>
        {
            Assert.True(state.Available);
            Assert.Null(state.ObservedValue);
            Assert.Equal(CapabilityReasonCode.TransportFaulted, state.Reason?.Code);
        });

        // Nothing resends it; the user's next lighting command writes the rings again.
        var dispatched = false;
        await rings.ShowAsync(Lighting(effect: 1), CancellationToken.None, () => dispatched = true);
        Assert.True(dispatched);
        Assert.Equal(2, port.Writes.Count(report => report[0] == 0xA4));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void AnUnsupportedEffectBecomesSolidInItsFirstColor(byte effect)
    {
        var stored = Lighting(effect);

        var normalized = AcerLightingService.NormalizeForRings(stored);

        Assert.Equal(1, normalized[1]);
        for (var slot = 0; slot < 8; slot++)
        {
            Assert.Equal(new byte[] { 10, 11, 12 }, normalized[(5 + slot * 3)..(8 + slot * 3)]);
        }

        Assert.Equal(stored[(5 + 8 * 3)..(8 + 8 * 3)], normalized[(5 + 8 * 3)..(8 + 8 * 3)]);
        Assert.Equal(stored[3..5], normalized[3..5]);
        Assert.Equal(effect, stored[1]);
        Assert.Same(normalized, AcerLightingService.NormalizeForRings(normalized));
    }

    [Fact]
    public void OnlyTheAtlas7DropsPerZoneColorsBecauseItsOutputIsOneLed()
    {
        var atlas7 = new AcerLightingService(null!, null!, new AcerRingLights());
        var atlas8 = new AcerLightingService(null!, null!);

        Assert.Equal("main", Assert.Single(atlas7.Descriptors, item => item.CapabilityId == "lighting.zone-color").InstanceId);
        Assert.Equal(9, atlas8.Descriptors.Count(item => item.CapabilityId == "lighting.zone-color"));
    }

    private static byte[] Lighting(byte effect)
    {
        var bytes = Enumerable.Repeat((byte)0xA5, AcerControllerTransport.LightingLength).ToArray();
        bytes[0] = 0;
        bytes[1] = effect;
        bytes[2] = 9;
        bytes[3] = 3;
        bytes[4] = 50;
        for (var slot = 0; slot < 9; slot++)
        {
            bytes[5 + slot * 3] = (byte)(10 + slot * 3);
            bytes[6 + slot * 3] = (byte)(11 + slot * 3);
            bytes[7 + slot * 3] = (byte)(12 + slot * 3);
        }

        return bytes;
    }

    /// <summary>One target with one LED, as the Atlas 7 lists; the first writes of A4 can be made to fail.</summary>
    private sealed class FakePort : IAcerRingPort
    {
        internal List<byte[]> Writes { get; } = [];
        internal int FailingWrites { get; set; }
        internal bool Disposed { get; private set; }
        public string Name => "fixture FF5A:0001";

        public void Get(byte[] report)
        {
            if (report[0] == 0xA1)
            {
                report[1] = 1;
                report[2] = 0x45;
            }
            else
            {
                report[1] = 0x45;
                report[3] = 14;
                report[4] = 1;
            }
        }

        public void Set(byte[] report)
        {
            Writes.Add((byte[])report.Clone());
            if (report[0] == 0xA4 && FailingWrites > 0)
            {
                FailingWrites--;
                throw new IOException("The A4 write failed.");
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
