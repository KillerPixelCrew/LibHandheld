using LibHandheld.Families.Acer;
using LibHandheld.Internal;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Acer;

/// These pin what the PA07-I51 definition admits from its read-only evidence. They do not establish
/// hardware acceptance; the definition stays unverified until an attended pass.
public sealed class AcerAtlas7Tests
{
    private static DeviceIdentitySnapshot Identity => new()
    {
        SystemManufacturer = "Acer", SystemProduct = "Predator PA07-I51",
        BaseboardManufacturer = "PTL", BaseboardProduct = "G310_PTG"
    };

    private static DeviceIdentitySnapshot Atlas8Identity => new()
    {
        SystemManufacturer = "Acer", SystemProduct = "Predator PA08-I51",
        BaseboardManufacturer = "PTL", BaseboardProduct = "F900_PTG"
    };

    [Fact]
    public void ExactSystemAndBoardSelectTheUnverifiedAtlas7()
    {
        var definition = DeviceFamilies.Detect(Identity);
        Assert.NotNull(definition);
        Assert.Equal(AcerModels.Atlas7.Definition.Id, definition.Id);
        Assert.False(definition.HardwareVerified);
        Assert.True(definition.HasController);
        Assert.True(definition.HardwareAccess.HasFlag(DeviceHardwareAccess.PawnIo));
        Assert.Equal(AcerModels.Atlas8.Definition.Id, DeviceFamilies.Detect(Atlas8Identity)?.Id);
    }

    [Fact]
    public void AnAtlas8BoardOrSystemNeverCrossesIntoTheOtherDefinition()
    {
        Assert.Null(DeviceFamilies.Detect(Identity with { BaseboardProduct = "F900_PTG" }));
        Assert.Null(DeviceFamilies.Detect(Atlas8Identity with { BaseboardProduct = "G310_PTG" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemProduct = "Predator Atlas 7" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemProduct = "Predator PA07-I52" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { SystemManufacturer = "Other" }));
        Assert.Null(DeviceFamilies.Detect(Identity with { BaseboardManufacturer = "Acer" }));
    }

    [Fact]
    public void Atlas7DeclaresOnlyControlsBackedByItsOwnFirmware()
    {
        var definition = DeviceFamilies.Detect(Identity);
        Assert.NotNull(definition);
        Assert.Contains(CapabilityRole.ControllerSource, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.MotionSource, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.PowerSustainedLimit, definition.DeclaredRoles);
        Assert.Contains(CapabilityRole.ScenarioMode, definition.DeclaredRoles);
        foreach (var excluded in new[]
                 {
                     CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanMeasuredRpm,
                     CapabilityRole.ChargeProtectionMode, CapabilityRole.ChargeLimit,
                     CapabilityRole.LightingBrightness, CapabilityRole.LightingEffect,
                     CapabilityRole.LightingEffectSpeed, CapabilityRole.LightingZoneColor
                 })
        {
            // DeclaredRoles also carries the roles any model gains from an approved USB accessory.
            Assert.DoesNotContain(excluded, AcerModels.Atlas7.NativeRoles);
        }

        Assert.Null(definition.FanCurvePresets);
        Assert.Null(definition.ExtendedVrrPanel);
    }

    [Fact]
    public void PredatorSenseTakeoverIsBoundToTheAtlas7AndLeavesFocusZoomAndTheKeyboard()
    {
        var takeover = HandheldDevice.ManagerApps.Single(app => app.DefinitionId == AcerModels.Atlas7.Definition.Id);
        Assert.Equal("acer-predator-handheld-atlas-7", takeover.Id);
        Assert.Contains("AcerHandheld.Service", takeover.Services);
        Assert.Contains("PredatorHandheldLauncher", takeover.Tasks);
        Assert.Equal(["Handheld.Widget"], takeover.Processes);
        Assert.Equal(["ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt"], takeover.PackageFamilies);
        Assert.DoesNotContain("AFZsvc", takeover.Services);
        Assert.DoesNotContain("FocusZoomUserService", takeover.Services);
        Assert.Null(takeover.DedicatedKeyboardDeviceId);
    }

    [Fact]
    public void CommandLaneAdmitsOnlyTheMeasuredAtlas7Shape()
    {
        var lane = Collection("mi_01", 0xFFA0, 1, 64) with { OutputLength = 64 };
        Assert.True(AcerControllerTransport.IsCommandCollection(lane));
        Assert.False(AcerControllerTransport.IsCommandCollection(lane with { ReleaseNumber = 0x0105 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(lane with { UsagePage = 0xFFF0, Usage = 0x40 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(lane with { OutputLength = 32 }));
        Assert.False(AcerControllerTransport.IsCommandCollection(lane with { ProductId = 0xF311 }));
    }

    [Fact]
    public void OemCollectionsAdmitTheMeasuredAtlas7ReportsOnly()
    {
        var vendor = Collection("mi_01", 0xFFF1, 0xF5, 15);
        var qam = Collection("mi_02", 0x88, 1, 3);
        Assert.True(AcerInputProtocol.IsVendorCollection(vendor));
        Assert.True(AcerInputProtocol.IsQamCollection(qam));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 0x0105 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { ReleaseNumber = 4, InputLength = 16 }));
        Assert.False(AcerInputProtocol.IsQamCollection(qam with { ReleaseNumber = 0x0105 }));
        Assert.False(AcerInputProtocol.IsVendorCollection(vendor with { DevicePath = "hid#mi_02" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Atlas7RefusesEveryUnmeasuredControllerTopologyBeforeDispatch(byte gamepad)
    {
        Assert.Throws<NotSupportedException>(() =>
            AcerControllerTransport.ProductAfter(new AcerControllerMode(gamepad, RearKeys: 1), AcerControllerTransport.Atlas7ProductId));
    }

    [Fact]
    public void ModeTopologyStaysOnItsOwnProduct()
    {
        var xinput = new AcerControllerMode(Gamepad: 1, RearKeys: 1);
        var dinput = new AcerControllerMode(Gamepad: 2, RearKeys: 1);
        Assert.Equal(AcerControllerTransport.Atlas7ProductId,
            AcerControllerTransport.ProductAfter(xinput, AcerControllerTransport.Atlas7ProductId));
        Assert.Equal(0xF901, AcerControllerTransport.ProductAfter(xinput, current: 0xF901));
        Assert.Equal(0xF90A, AcerControllerTransport.ProductAfter(dinput, current: 0xF901));
        Assert.Equal(0xF901, AcerControllerTransport.ProductAfter(xinput, current: 0xF90A));
    }

    private static HidCollection Collection(string usbInterface, ushort page, ushort usage, ushort length)
    {
        return new HidCollection
        {
            DevicePath = "hid#" + usbInterface, InstancePath = "test", VendorId = 0x0502,
            ProductId = AcerControllerTransport.Atlas7ProductId, ReleaseNumber = 0x0100, UsagePage = page,
            Usage = usage, InputLength = length, OutputLength = 0, FeatureLength = 0,
            PhysicalLocation = "PCIROOT(0)#USBROOT(0)#USB(3)"
        };
    }
}
