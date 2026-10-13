using System.Buffers.Binary;
using System.Numerics;
using LibHandheld.Internal.Input;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("rc72la")]
    [InlineData("rc73xa")]
    public void PublishedOemControllerRolesMatchTheExistingVendorMapping(string modelId)
    {
        var model = AllyModels.ById(modelId)!;
        var controls = AllyModels.OemControls(model);
        foreach (var code in new byte[] { 0xA6, 0x38, 0x93 })
        {
            var action = AllyModels.VendorAction(model, code)!.Value;
            Assert.Equal(action.Button, controls.Single(control => control.ControlId == action.ControlId).ControllerButton);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x5A, 0xA6, 0, 0 }, true, 0xA6)]
    [InlineData(new byte[] { 0x5A, 0x38 }, true, 0x38)]
    [InlineData(new byte[] { 0x5D, 0xA6 }, false, 0)]
    [InlineData(new byte[] { 0x5A, 0x00 }, false, 0)]
    [InlineData(new byte[] { 0x5A }, false, 0)]
    public void VendorReportsDecodeOnlyReport5A(byte[] report, bool expected, byte code)
    {
        Assert.Equal(expected, AllyProtocol.TryReadVendorEvent(report, out var decoded));
        Assert.Equal(code, decoded);
    }

    [Fact]
    public void ClassicLayoutPreservesHcButtonCodes()
    {
        var ally = AllyModels.ById("rc72la")!;

        Assert.Equal(
            new AllyVendorAction(OemControlIds.CommandCenter, OemPressKind.Short, CanonicalButtons.QuickAccess),
            AllyModels.VendorAction(ally, 0xA6));
        Assert.Equal(new AllyVendorAction(OemControlIds.ArmouryCrate, OemPressKind.Short, CanonicalButtons.Guide),
            AllyModels.VendorAction(ally, 0x38));
        Assert.Equal(new AllyVendorAction(OemControlIds.Library, OemPressKind.Short, CanonicalButtons.None),
            AllyModels.VendorAction(ally, 0x93));
        Assert.Equal(new AllyVendorAction(OemControlIds.M2, OemPressKind.Short, CanonicalButtons.RearPaddle2),
            AllyModels.VendorAction(ally, 0xA7));
        Assert.Equal(new AllyVendorAction(OemControlIds.M2, OemPressKind.Short, CanonicalButtons.RearPaddle2,
            OemControlEdge.Released), AllyModels.VendorAction(ally, 0xA8));
        Assert.Null(AllyModels.VendorAction(ally, 0xA5));
    }

    [Fact]
    public void XboxLayoutNamesItsOwnButtons()
    {
        var xbox = AllyModels.ById("rc73xa")!;

        Assert.Equal(OemControlIds.ArmouryCrate, AllyModels.VendorAction(xbox, 0xA6)!.Value.ControlId);
        Assert.Equal(OemControlIds.Library, AllyModels.VendorAction(xbox, 0x93)!.Value.ControlId);
        Assert.Equal([AllyModels.VkF21, AllyModels.VkF22], xbox.FrontKeyboardControls.Select(item => item.VirtualKey));
        Assert.Empty(AllyModels.ById("rc71l")!.FrontKeyboardControls);
    }

    [Theory]
    [InlineData("rc73ya")]
    [InlineData("rc73xa")]
    public void XboxFrontButtonsAreGuideQuickAccessAndCompanion(string definitionId)
    {
        var xbox = AllyModels.ById(definitionId)!;

        // The Xbox button is the pad's guide bit and always reaches Steam as the Guide.
        Assert.Equal(CanonicalButtons.Guide, AllyControllerCodec.Buttons(AllyControllerCodec.Guide));
        // Armoury Crate carries no Steam button: WSGM opens its overlay from it.
        Assert.Equal(CanonicalButtons.None, AllyModels.VendorAction(xbox, 0xA6)!.Value.Button);
        Assert.Equal(CanonicalButtons.QuickAccess, AllyModels.VendorAction(xbox, 0x38)!.Value.Button);
        Assert.Equal(CanonicalButtons.QuickAccess, AllyModels.VendorAction(xbox, 0x93)!.Value.Button);
        Assert.Equal(
            [
                (AllyModels.VkF21, OemControlIds.ArmouryCrate, CanonicalButtons.None),
                (AllyModels.VkF22, OemControlIds.Library, CanonicalButtons.QuickAccess)
            ],
            xbox.FrontKeyboardControls.Select(item => (item.VirtualKey, item.ControlId, item.Button)));

        var controls = AllyModels.OemControls(xbox);
        Assert.True(controls.Single(control => control.ControlId == OemControlIds.ArmouryCrate).CompanionApplication);
        Assert.Equal(OemControlDefaultActionHint.CompanionApplication,
            controls.Single(control => control.ControlId == OemControlIds.ArmouryCrate).DefaultActionHint);
        Assert.All(controls.Where(control => control.ControlId != OemControlIds.ArmouryCrate),
            control => Assert.False(control.CompanionApplication));
        Assert.All(controls.Where(control => control.ControlId != OemControlIds.ArmouryCrate),
            control => Assert.Equal(OemControlDefaultActionHint.None, control.DefaultActionHint));
    }

    [Fact]
    public void ClassicFrontButtonsKeepGuideAndQuickAccess()
    {
        var ally = AllyModels.ById("rc71l")!;

        // With no Xbox button, Armoury Crate is the only Guide source, so it is not the companion button.
        Assert.Equal(CanonicalButtons.QuickAccess, AllyModels.VendorAction(ally, 0xA6)!.Value.Button);
        Assert.Equal(CanonicalButtons.Guide, AllyModels.VendorAction(ally, 0x38)!.Value.Button);
        Assert.All(AllyModels.OemControls(ally), control => Assert.False(control.CompanionApplication));
    }

    [Fact]
    public void ControllerTablesAreSixtyFourByteFeatureReports()
    {
        Assert.All(AllyProtocol.GameModeConfiguration, report =>
        {
            Assert.Equal(64, report.Length);
            Assert.Equal(0x5A, report[0]);
            Assert.Equal(0xD1, report[1]);
        });
        Assert.Equal(AllyProtocol.GameModeConfiguration.Count, AllyProtocol.DefaultConfiguration.Count);
        // Only the M1/M2 table differs between taking and releasing the controller.
        var differing = Enumerable.Range(0, AllyProtocol.GameModeConfiguration.Count)
            .Where(index => !AllyProtocol.GameModeConfiguration[index]
                .SequenceEqual(AllyProtocol.DefaultConfiguration[index]))
            .ToArray();
        Assert.Equal([8], differing);
        Assert.Equal([0x5A, 0xD1, 0x01, 0x01, 0x01], AllyProtocol.GameModeConfiguration[0][..5]);
    }

    [Fact]
    public void RearTablesCarryHhdKeyCodes()
    {
        var keyboard = AllyProtocol.RearKeyboardMapping;
        Assert.Equal([0x5A, 0xD1, 0x02, 0x08, 0x2C], keyboard[..5]);
        Assert.Equal(0x28, keyboard[7]);
        Assert.Equal(0x30, keyboard[29]);

        var factory = AllyProtocol.RearDefaultMapping;
        Assert.Equal(0x8E, factory[7]);
        Assert.Equal(0x8E, factory[18]);
        Assert.Equal(0x8F, factory[29]);
        // HC's M1M2Default ends here; its bytes are sent as HC sends them.
        Assert.All(factory[30..].ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void FrontTablesMatchHcByteForByteInHcOrder()
    {
        // HC 1.3.1.6 ROGAlly.cs:93-154, in ConfigureController's order (ROGAlly.cs:652-659).
        byte[][] hc =
        [
            [90, 209, 1, 1, 1],
            [
                90, 209, 2, 1, 44, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 25, 0, 0, 0, 0, 0, 0, 0, 1, 10, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 3, 140, 136, 118
            ],
            [
                90, 209, 2, 2, 44, 1, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 2, 130, 35, 0, 0, 0, 1, 12, 0,
                0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 2, 130, 13
            ],
            [90, 209, 2, 3, 44, 1, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 8],
            [90, 209, 2, 4, 44, 1, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 6],
            [
                90, 209, 2, 5, 44, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 22, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 2, 130, 49
            ],
            [
                90, 209, 2, 6, 44, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 2, 130, 77, 0, 0, 0, 1, 4, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 30
            ],
            [90, 209, 2, 7, 44, 1, 17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 18]
        ];
        var sent = AllyProtocol.GameModeConfiguration.Take(hc.Length).ToArray();

        for (var index = 0; index < hc.Length; index++)
        {
            Assert.True(sent[index].AsSpan(0, hc[index].Length).SequenceEqual(hc[index]), $"table {index}");
            Assert.All(sent[index][hc[index].Length..].ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public void CommitFollowsHcValues()
    {
        var commit = AllyProtocol.GameModeConfiguration.Skip(10).ToArray();
        Assert.Equal([0x5A, 0xD1, 0x0F, 0x20], commit[0][..4]);
        Assert.Equal([0x5A, 0xD1, 0x06, 0x02, 0x64, 0x64], commit[1][..6]);
        Assert.Equal([0x5A, 0xD1, 0x04, 0x04, 0x00, 0x64, 0x00, 0x64], commit[2][..8]);
        Assert.Equal([0x5A, 0xD1, 0x05, 0x04, 0x00, 0x64, 0x00, 0x64], commit[3][..8]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 0)]
    [InlineData(17, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 3)]
    public void BrightnessScalesLikeHc(int percent, byte level)
    {
        Assert.Equal([0x5D, 0xBA, 0xC5, 0xC4, level], AllyProtocol.Brightness(percent));
    }

    [Fact]
    public void ColorMessageMatchesHcAuraMessage()
    {
        var report = AllyProtocol.Color(AuraEffect.Breathing, AuraZone.All, 0x112233, 0x445566, AllyProtocol.SpeedFast);

        Assert.Equal(17, report.Length);
        Assert.Equal(
            [0x5D, 0xB3, 0x00, 0x01, 0x11, 0x22, 0x33, 0xE1, 0x00, 0x01, 0x44, 0x55, 0x66, 0, 0, 0, 0],
            report);
        Assert.Equal(0, AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, 0, 0, 0)[9]);
        Assert.Equal(4, AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, 0, 0, 0)[2]);
    }

    [Theory]
    [InlineData(0, 0xEB)]
    [InlineData(33, 0xEB)]
    [InlineData(34, 0xF5)]
    [InlineData(66, 0xF5)]
    [InlineData(67, 0xE1)]
    public void SpeedUsesHcBands(int percent, byte expected)
    {
        Assert.Equal(expected, AllyProtocol.Speed(percent));
    }

    [Fact]
    public void TwoSolidRingColoursFollowHcApplyColorFast()
    {
        var reports = LightingService.Encode(new AllyLightingState(100, AuraEffect.Solid, 90, 0xFF0000, 0x0000FF));

        Assert.Equal(5, reports.Count);
        Assert.True(reports[0].Feature);
        Assert.Equal(0xBA, reports[0].Bytes[1]);
        Assert.Equal([1, 2, 3, 4], reports.Skip(1).Select(report => (int)report.Bytes[2]));
        Assert.Equal(0xFF, reports[1].Bytes[4]);
        Assert.Equal(0xFF, reports[4].Bytes[6]);
        // HC's fast path always sends the slow speed and no apply or set.
        Assert.All(reports.Skip(1), report =>
        {
            Assert.False(report.Feature);
            Assert.Equal(AllyProtocol.SpeedSlow, report.Bytes[7]);
            Assert.Equal(0xB3, report.Bytes[1]);
        });
    }

    [Fact]
    public void OneSolidColourFollowsHcApplyColor()
    {
        var reports = LightingService.Encode(new AllyLightingState(100, AuraEffect.Solid, 90, 0x00FF00, 0x00FF00));

        Assert.Equal(4, reports.Count);
        Assert.Equal(0, reports[1].Bytes[2]);
        Assert.Equal(AllyProtocol.SpeedFast, reports[1].Bytes[7]);
        Assert.Equal(0xB4, reports[2].Bytes[1]);
        Assert.Equal(0xB5, reports[3].Bytes[1]);
    }

    [Fact]
    public void AcpiRequestsFollowHcCallMethod()
    {
        var status = AsusAcpiProtocol.EncodeStatus(AsusAcpiId.SustainedPower, 0);
        Assert.Equal(16, status.Length);
        Assert.Equal(0x53545344u, BinaryPrimitives.ReadUInt32LittleEndian(status));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)));
        Assert.Equal(0x001200A3u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)));

        var set = AsusAcpiProtocol.EncodeSet(AsusAcpiId.ChargeLimit, 80);
        Assert.Equal(0x53564544u, BinaryPrimitives.ReadUInt32LittleEndian(set));
        Assert.Equal(80u, BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(12)));

        var curve = AsusAcpiProtocol.Encode(AsusAcpiProtocol.DeviceSet, AsusAcpiId.CpuFanCurve, new byte[16]);
        Assert.Equal(28, curve.Length);
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(curve.AsSpan(4)));
    }

    [Theory]
    [InlineData(0x0001000Fu, true, 15)]
    [InlineData(0x00010000u, true, 0)]
    [InlineData(0x0000000Fu, false, 0)]
    [InlineData(0xFFFFFFFEu, false, 0)]
    [InlineData(0x0101000Fu, false, 0)]
    public void ScalarsRequireThePresenceBit(uint raw, bool expected, int value)
    {
        Assert.Equal(expected, AsusAcpiProtocol.TryDecodeScalar(raw, out var decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void CurveSelectorFollowsHc()
    {
        Assert.Equal(0u, AsusAcpiProtocol.CurveSelector(0));
        Assert.Equal(2u, AsusAcpiProtocol.CurveSelector(1));
        Assert.Equal(1u, AsusAcpiProtocol.CurveSelector(2));
    }

    [Fact]
    public void OnlyReviewedIdsAreWritable()
    {
        Assert.False(AsusAcpiProtocol.IsWritable(AsusAcpiId.CpuFanSpeed));
        Assert.False(AsusAcpiProtocol.IsWritable((AsusAcpiId)0x00090020));
        Assert.True(AsusAcpiProtocol.IsWritable(AsusAcpiId.FastPower));
        Assert.True(AsusAcpiProtocol.IsValidCurve(AllyFanCapability.DefaultCpuCurve));
        Assert.False(AsusAcpiProtocol.IsValidCurve(new byte[16]));
    }

    [Fact]
    public void ClassicMotionMapsLikeHcRogAllyJson()
    {
        var model = AllyModels.ById("rc71l")!;
        var raw = new Vector3(1, 2, 3);

        Assert.Equal(new Vector3(-1, -3, 2), model.Gyro.Apply(raw));
        Assert.Equal(new Vector3(-1, -3, 2), model.Accelerometer.Apply(raw));
    }

    [Fact]
    public void XboxAllyFollowsHcsEnvelope()
    {
        var xboxAlly = AllyModels.ById("rc73ya")!;

        Assert.Equal(35, xboxAlly.MaximumWatts);
        Assert.Equal([13, 17, 25], xboxAlly.Presets.Select(preset => preset.Watts));
        Assert.All(xboxAlly.Presets,
            preset => Assert.InRange(preset.Watts, xboxAlly.MinimumWatts, xboxAlly.MaximumWatts));
    }

    [Fact]
    public void FrontOemControlsDeclareNoLongPress()
    {
        Assert.All(AllyModels.All.SelectMany(AllyModels.OemControls),
            control => Assert.False(control.SupportsLongPress));
    }

    [Fact]
    public void XboxGyroSignsDifferFromItsAccelerometer()
    {
        var model = AllyModels.ById("rc73ya")!;
        var raw = new Vector3(1, 2, 3);

        Assert.Equal(new Vector3(1, 3, -2), model.Gyro.Apply(raw));
        Assert.Equal(new Vector3(-1, -3, 2), model.Accelerometer.Apply(raw));
    }

    [Fact]
    public void XInputButtonsDecodeToCanonical()
    {
        var buttons = (ushort)(AllyControllerCodec.A | AllyControllerCodec.Back | AllyControllerCodec.Guide
                               | AllyControllerCodec.DPadLeft);

        Assert.Equal(CanonicalButtons.A | CanonicalButtons.View | CanonicalButtons.Guide | CanonicalButtons.DPadLeft,
            AllyControllerCodec.Buttons(buttons));
        Assert.Equal(-1f, AllyControllerCodec.Axis(short.MinValue));
        Assert.Equal(1f, AllyControllerCodec.Axis(short.MaxValue));
    }

    [Fact]
    public void LatchedOemButtonsExpire()
    {
        var state = new AllyOemButtonState();
        var now = DateTimeOffset.UtcNow;
        state.Latch(CanonicalButtons.Guide, now);
        state.Hold(AllyOemSource.Keyboard, CanonicalButtons.RearPaddle1, true);

        Assert.Equal(CanonicalButtons.Guide | CanonicalButtons.RearPaddle1, state.Current(now));
        Assert.Equal(CanonicalButtons.RearPaddle1, state.Current(now + OemButtonLatch.HoldDuration));
        state.Hold(AllyOemSource.Keyboard, CanonicalButtons.RearPaddle1, false);
        Assert.Equal(CanonicalButtons.None, state.Current(now + OemButtonLatch.HoldDuration));
    }

    [Fact]
    public void AButtonHeldByBothSourcesStaysDownUntilBothRelease()
    {
        var state = new AllyOemButtonState();
        var now = DateTimeOffset.UtcNow;
        state.Hold(AllyOemSource.Vendor, CanonicalButtons.RearPaddle2, true);
        state.Hold(AllyOemSource.Keyboard, CanonicalButtons.RearPaddle2, true);
        state.Hold(AllyOemSource.Vendor, CanonicalButtons.RearPaddle2, false);

        Assert.Equal(CanonicalButtons.RearPaddle2, state.Current(now));
        state.Release(CanonicalButtons.RearPaddle2);
        Assert.Equal(CanonicalButtons.None, state.Current(now));
    }

    [Fact]
    public void TheSecondTransportOfOnePressIsNotAdmitted()
    {
        var state = new AllyOemButtonState();
        var now = DateTimeOffset.UtcNow;

        // A release-less vendor press, then the same button's key inside the latch window.
        Assert.True(state.Admit(OemControlIds.ArmouryCrate, AllyOemSource.Vendor, OemControlEdge.Pressed, false, now));
        Assert.False(state.Admit(OemControlIds.ArmouryCrate, AllyOemSource.Keyboard, OemControlEdge.Pressed, true,
            now.AddMilliseconds(20)));
        Assert.False(state.Admit(OemControlIds.ArmouryCrate, AllyOemSource.Keyboard, OemControlEdge.Released, true,
            now.AddMilliseconds(80)));

        // A key held first swallows the vendor echo, and its own release still passes.
        var later = now.AddSeconds(1);
        Assert.True(state.Admit(OemControlIds.M2, AllyOemSource.Keyboard, OemControlEdge.Pressed, true, later));
        Assert.False(state.Admit(OemControlIds.M2, AllyOemSource.Vendor, OemControlEdge.Pressed, true,
            later.AddMilliseconds(5)));
        Assert.False(state.Admit(OemControlIds.M2, AllyOemSource.Vendor, OemControlEdge.Released, true,
            later.AddMilliseconds(90)));
        Assert.True(state.Admit(OemControlIds.M2, AllyOemSource.Keyboard, OemControlEdge.Released, true,
            later.AddMilliseconds(100)));

        // Separate presses from one source are never merged.
        Assert.True(state.Admit(OemControlIds.Library, AllyOemSource.Vendor, OemControlEdge.Pressed, false, later));
        Assert.True(state.Admit(OemControlIds.Library, AllyOemSource.Vendor, OemControlEdge.Pressed, false,
            later.AddMilliseconds(50)));
    }
}
