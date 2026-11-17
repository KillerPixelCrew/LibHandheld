using System.Buffers.Binary;
using LibHandheld.Families.Acer;
using LibHandheld.Internal;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Acer;

public sealed class AcerInputTests
{
    [Fact]
    public void DInputCompanionIsOnlyTheGamepadInterfaceVendorCollection()
    {
        var vendor = Collection("mi_00", 0xFFF0, 0x40, 64) with
        {
            ProductId = 0xF90A, ReleaseNumber = 0x0100, OutputLength = 64,
            InstancePath = @"HID\VID_0502&PID_F90A&MI_00&COL02\fixture"
        };
        var consumer = vendor with
        {
            UsagePage = 0xC, Usage = 1, InputLength = 5, OutputLength = 0,
            InstancePath = @"HID\VID_0502&PID_F90A&MI_01&COL03\fixture"
        };
        Assert.True(AcerInputProtocol.IsDInputCompanion(vendor));
        Assert.False(AcerInputProtocol.IsDInputCompanion(consumer));
        Assert.False(AcerInputProtocol.IsDInputCompanion(vendor with { UsagePage = 1, Usage = 6 }));
        Assert.False(AcerInputProtocol.IsDInputCompanion(vendor with { ProductId = 0xF901 }));
        Assert.False(AcerInputProtocol.IsDInputCompanion(vendor with { ReleaseNumber = 0x0101 }));
        Assert.False(AcerInputProtocol.IsDInputCompanion(vendor with { InstancePath = vendor.InstancePath.Replace("COL02", "COL020") }));
        Assert.False(AcerInputProtocol.IsDInputCompanion(vendor with { PhysicalLocation = "" }));
    }

    private static DeviceIdentitySnapshot Identity => new()
    {
        SystemManufacturer = "Acer", SystemProduct = "Predator PA08-I51",
        BaseboardManufacturer = "PTL", BaseboardProduct = "F900_PTG"
    };

    [Fact]
    public void ExactSystemAndBoardSelectTheVerifiedIntelModel()
    {
        var definition = DeviceFamilies.Detect(Identity);
        Assert.NotNull(definition);
        Assert.Equal(AcerModels.Atlas8.Definition.Id, definition.Id);
        Assert.True(definition.HardwareVerified);
        Assert.True(definition.HasController);
        Assert.True(definition.HardwareAccess.HasFlag(DeviceHardwareAccess.PawnIo));
        Assert.Contains(CapabilityRole.ControllerSource, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.MotionSource, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.PowerSustainedLimit, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.FanMode, definition.DeclaredRoles);
    }

    [Fact]
    public void MarketingNameOrSharedUsbIdentityDoesNotAdmitAnotherModel()
    {
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemProduct = "Predator Atlas 8" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemProduct = "Predator PA08-I52" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemManufacturer = "Other" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { BaseboardProduct = "F900" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { BaseboardManufacturer = "Acer" }));
    }

    [Fact]
    public void OemCollectionsRequireCapturedInterfaceUsageReleaseAndLength()
    {
        var vendor = Collection("mi_01", 0xFFF1, 0xF5, 16);
        var qam = Collection("mi_02", 0x88, 1, 3);
        Assert.True(AcerInputProtocol.IsVendorCollection(vendor));
        Assert.True(AcerInputProtocol.IsQamCollection(qam));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { UsagePage = 0xFFA0 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { InputLength = 64 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 5 }));
        Assert.True(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 0x0100, InputLength = 15 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 0x0100, InputLength = 16 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { PhysicalLocation = "" }));
        Assert.False(AcerInputProtocol.IsQamCollection(qam with { UsagePage = 1, Usage = 6 }));
        Assert.False(AcerInputProtocol.IsQamCollection(qam with { DevicePath = "hid#mi_01" }));
        Assert.False(AcerInputProtocol.IsQamCollection(qam with { ProductId = 0xF900 }));
    }

    [Fact]
    public void Firmware105KeepsTheMeasuredXInputCollectionShapes()
    {
        // Shapes logged on the PA08-I51 after Predator Sense flashed controller firmware 1.05.
        var vendor = Collection("mi_01", 0xFFF1, 0xF5, 15) with { ReleaseNumber = 0x0105 };
        var qam = Collection("mi_02", 0x88, 1, 3) with { ReleaseNumber = 0x0105 };
        var command = Collection("mi_01", 0xFFA0, 1, 64) with { ReleaseNumber = 0x0105, OutputLength = 64 };
        Assert.True(AcerInputProtocol.IsVendorCollection(vendor));
        Assert.True(AcerInputProtocol.IsQamCollection(qam));
        Assert.True(AcerControllerTransport.IsCommandCollection(command));
        Assert.True(AcerControllerTransport.IsCommandCollection(command with { ReleaseNumber = 0x0100 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { InputLength = 16 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 0x0106 }));
        Assert.False(AcerInputProtocol.IsQamCollection(qam with { ReleaseNumber = 0x0106 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(command with { ReleaseNumber = 0x0106 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(command with { OutputLength = 0 }));
    }

    [Fact]
    public void DInputLanesAdmitBothMeasuredFirmwareReleases()
    {
        var command = Collection("mi_00", 0xFFF0, 0x40, 64) with
        {
            ProductId = 0xF90A, ReleaseNumber = 0x0105, OutputLength = 64,
            InstancePath = @"HID\VID_0502&PID_F90A&MI_00&COL02\fixture"
        };
        Assert.True(AcerControllerTransport.IsCommandCollection(command));
        Assert.True(AcerControllerTransport.IsCommandCollection(command with { ReleaseNumber = 0x0100 }));
        Assert.True(AcerInputProtocol.IsDInputCompanion(command));
        Assert.False(AcerControllerTransport.IsCommandCollection(command with { ReleaseNumber = 0x0106 }));
        Assert.False(AcerInputProtocol.IsDInputCompanion(command with { ReleaseNumber = 0x0106 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(command with { UsagePage = 0xFFA0, Usage = 1 }));
    }

    [Fact]
    public void DInputUsagesFromTheGamepadReaderProduceSingleEdges()
    {
        var edges = new List<(string Id, bool Down)>();
        using var state = new AcerButtonState((id, _, down) => edges.Add((id, down)));
        state.HandleDInputButtons([13, 17]);
        state.HandleDInputButtons([13, 17]);
        state.HandleDInputButtons([17]);
        state.ReleaseVendor();
        state.ReleaseQam();
        Assert.Equal(new[] { ("guide", true), ("m2", true), ("guide", false), ("m2", false) }, edges);
    }

    [Fact]
    public void UsagesBeforeADInputStartReachNothing()
    {
        var source = new AcerButtonSource();
        source.OnUsages([13, 14, 16, 17]);
        source.OnHidDisconnected();
        Assert.False(source.IsConnected);
    }

    [Fact]
    public void RearRolloverAndUnrelatedReportsPreserveHeldEdges()
    {
        var edges = new List<(string Id, bool Down)>();
        using var state = new AcerButtonState((id, _, down) => edges.Add((id, down)));
        var report = new byte[16];
        report[0] = 0x9A;
        report[1] = 2;
        state.HandleVendorReport(report);
        state.HandleVendorReport(report);
        report[1] = 6;
        state.HandleVendorReport(report);
        report[0] = 0x9B;
        report[1] = 0;
        state.HandleVendorReport(report);
        state.HandleVendorReport([0x9A, 0]);
        Assert.Equal(new[] { ("m1", true), ("m2", true) }, edges);
        report[0] = 0x9A;
        report[1] = 4;
        state.HandleVendorReport(report);
        state.ReleaseVendor();
        Assert.Equal(new[] { ("m1", true), ("m2", true), ("m1", false), ("m2", false) }, edges);
    }

    [Fact]
    public void GuideAndQamRejectShortOrUnknownReports()
    {
        var guide = new byte[16];
        guide[0] = 0x9E;
        guide[3] = 5;
        Assert.True(AcerInputProtocol.IsGuidePress(guide));
        Assert.False(AcerInputProtocol.IsGuidePress(guide.AsSpan(0, 5)));
        guide[3] = 6;
        Assert.False(AcerInputProtocol.IsGuidePress(guide));
        Assert.True(AcerInputProtocol.TryDecodeQam([0x37, 0x81, 0xFF], out var pressed));
        Assert.True(pressed);
        Assert.True(AcerInputProtocol.TryDecodeQam([0x37, 0, 0], out pressed));
        Assert.False(pressed);
        Assert.False(AcerInputProtocol.TryDecodeQam([0x37, 0x82, 0xFF], out _));
        Assert.False(AcerInputProtocol.TryDecodeQam([0x37, 0x81], out _));
    }

    [Fact]
    public async Task GuideNotificationReleasesWithoutAnotherInputReport()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var state = new AcerButtonState((id, _, down) =>
        {
            if (id == "guide" && !down)
            {
                released.TrySetResult();
            }
        });
        var report = new byte[16];
        report[0] = 0x9E;
        report[3] = 5;
        state.HandleVendorReport(report);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CancelledAcquisitionDoesNotEnumerateOrOpenHardware()
    {
        await using var source = new AcerButtonSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.StartAsync((_, _, _) =>
            {
            }, new CancellationToken(canceled: true)));
        Assert.False(source.IsConnected);
    }

    [Fact]
    public void DisposalReleasesHeldControlsAndRefusesLateReports()
    {
        var edges = new List<(string Id, bool Down)>();
        var state = new AcerButtonState((id, _, down) => edges.Add((id, down)));
        var report = new byte[16];
        report[0] = 0x9A;
        report[1] = 6;
        state.HandleVendorReport(report);
        report[0] = 0x9E;
        report[1] = 0;
        report[3] = 5;
        state.HandleVendorReport(report);
        state.HandleQamReport([0x37, 0x81, 0xFF]);
        state.Dispose();
        state.ReleaseVendor();
        state.ReleaseQam();
        state.HandleVendorReport(report);
        state.HandleQamReport([0x37, 0x81, 0xFF]);
        state.Dispose();
        Assert.Equal(8, edges.Count);
        foreach (var id in new[] { "guide", "qam", "m1", "m2" })
        {
            Assert.Equal(new[] { (id, true), (id, false) }, edges.Where(edge => edge.Id == id));
        }
    }

    [Fact]
    public void AHeldRearKeyRebuildsThePadThatCombinationModeWithholdsFromXInput()
    {
        // While M1 or M2 is held the firmware stops the XInput report and sends the other keys in 9A and the
        // sticks and triggers in 9B instead (2026-11-17 captures); before this the pad froze.
        using var state = new AcerButtonState((_, _, _) => { });
        var frozen = new StandardXInput.Gamepad { Buttons = 0x0400, LeftY = 32000, RightTrigger = 7 };
        Assert.False(state.TrySubstitute(5, frozen, out _));

        state.HandleVendorReport(Combination(0x9A, rear: 4));
        Assert.True(state.TrySubstitute(5, frozen, out var pad));
        Assert.Equal(frozen, pad);

        state.HandleVendorReport(Combination(0x9A, rear: 4, first: 0x01, second: 0x02));
        Assert.True(state.TrySubstitute(5, frozen, out pad));
        Assert.Equal(0x0400 | 0x1000 | 0x2000, pad.Buttons);
        state.HandleVendorReport(Combination(0x9A, rear: 4, first: 0x93));
        Assert.True(state.TrySubstitute(5, frozen, out pad));
        Assert.Equal(0x0400 | 0x0008, pad.Buttons);
        Assert.Equal(32000, pad.LeftY);

        // Left stick fully right and up, right stick fully left and down, LT released, RT fully squeezed.
        state.HandleVendorReport(Analog(rear: 4, 65535, 0, 0, 65535, 0, 1023));
        Assert.True(state.TrySubstitute(5, frozen, out pad));
        Assert.Equal((32767, 32767, -32768, -32768, 0, 255),
            ((int)pad.LeftX, (int)pad.LeftY, (int)pad.RightX, (int)pad.RightY, (int)pad.LeftTrigger,
                (int)pad.RightTrigger));
        state.HandleVendorReport(Analog(rear: 4, 0x8000, 0x7FFF, 0x8000, 0x7FFF, 512, 0));
        Assert.True(state.TrySubstitute(5, frozen, out pad));
        Assert.Equal((0, 0, 0, 0, 128, 0),
            ((int)pad.LeftX, (int)pad.LeftY, (int)pad.RightX, (int)pad.RightY, (int)pad.LeftTrigger,
                (int)pad.RightTrigger));
    }

    [Fact]
    public void TheRebuiltPadOutlivesTheReleaseOnlyUntilXInputReportsAgain()
    {
        using var state = new AcerButtonState((_, _, _) => { });
        var frozen = new StandardXInput.Gamepad();
        state.HandleVendorReport(Combination(0x9A, rear: 2, first: 0x01));
        state.HandleVendorReport(Analog(rear: 2, 0x8000, 0, 0x8000, 0x7FFF, 0, 0));
        Assert.True(state.TrySubstitute(7, frozen, out var pad));
        Assert.Equal(32767, pad.LeftY);

        // The 9B sent at release reads neutral even with the stick still up, and the same packet is the frozen pad.
        state.HandleVendorReport(Combination(0x9A, rear: 0));
        state.HandleVendorReport(Analog(rear: 0, 0x8000, 0x7FFF, 0x8000, 0x7FFF, 0, 0));
        Assert.True(state.TrySubstitute(7, frozen, out pad));
        Assert.Equal((0, 32767), ((int)pad.Buttons, (int)pad.LeftY));
        Assert.False(state.TrySubstitute(8, frozen, out _));
        Assert.False(state.TrySubstitute(7, frozen, out _));

        // The next hold starts again from the pad XInput holds then.
        var later = new StandardXInput.Gamepad { LeftX = -1000 };
        state.HandleVendorReport(Combination(0x9A, rear: 4));
        Assert.True(state.TrySubstitute(9, later, out pad));
        Assert.Equal(later, pad);
    }

    [Fact]
    public void FrontKeysDuringARearHoldArePressedAndReleasedAndUnknownCodesAddNothing()
    {
        var edges = new List<(string Id, bool Down)>();
        using var state = new AcerButtonState((id, _, down) => edges.Add((id, down)));
        state.HandleVendorReport(Combination(0x9A, rear: 4));
        state.HandleVendorReport(Combination(0x9A, rear: 4, first: 0xA1));
        state.HandleVendorReport(Combination(0x9A, rear: 4));
        state.HandleVendorReport(Combination(0x9A, rear: 4, first: 0x55, second: 0xA0));
        Assert.True(state.TrySubstitute(1, new StandardXInput.Gamepad(), out var pad));
        Assert.Equal(0, pad.Buttons);
        state.HandleVendorReport(Combination(0x9A, rear: 0));
        Assert.Equal(new[]
        {
            ("m2", true), ("guide", true), ("guide", false), ("qam", true), ("m2", false), ("qam", false)
        }, edges);

        // A reader fault or stop releases a front key the hold was carrying along with the rear key.
        edges.Clear();
        state.HandleVendorReport(Combination(0x9A, rear: 2, first: 0xA1));
        state.ReleaseVendor();
        Assert.False(state.TrySubstitute(1, new StandardXInput.Gamepad(), out _));
        Assert.Equal(new[] { ("m1", true), ("guide", true), ("guide", false), ("m1", false) }, edges);
    }

    private static byte[] Combination(byte id, byte rear, ushort first = 0, ushort second = 0)
    {
        var report = new byte[15];
        report[0] = id;
        report[1] = rear;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(3), first);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(5), second);
        return report;
    }

    private static byte[] Analog(byte rear, ushort leftX, ushort leftY, ushort rightX, ushort rightY,
        ushort leftTrigger, ushort rightTrigger)
    {
        var report = new byte[15];
        report[0] = 0x9B;
        report[1] = rear;
        ushort[] words = [leftX, leftY, rightX, rightY, leftTrigger, rightTrigger];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(3 + index * 2), words[index]);
        }

        return report;
    }

    private static HidCollection Collection(string usbInterface, ushort page, ushort usage, ushort length)
    {
        return new HidCollection
        {
            DevicePath = "hid#" + usbInterface, InstancePath = "test", VendorId = 0x0502, ProductId = 0xF901,
            ReleaseNumber = 4, UsagePage = page, Usage = usage, InputLength = length, OutputLength = 0,
            FeatureLength = 0, PhysicalLocation = "PCIROOT(0)#USBROOT(0)#USB(3)"
        };
    }
}
