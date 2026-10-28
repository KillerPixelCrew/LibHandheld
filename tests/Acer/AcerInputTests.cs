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
