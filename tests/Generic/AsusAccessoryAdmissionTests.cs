using LibHandheld.Families.Generic;

namespace LibHandheld.Tests.Generic;

public sealed class AsusAccessoryAdmissionTests
{
    [Fact]
    public void CapturedApprovedUsbIdentityAdmitsTheOptionalProvider()
    {
        Assert.True(GenericAsusService.MatchesAccessoryIdentity(new DeviceIdentitySnapshot
        {
            UsbEndpoints = [new UsbEndpointObservation { VendorId = "0B05", ProductId = "18C6" }]
        }));
    }

    [Fact]
    public void ManufacturerOrAnUnapprovedProductDoesNotAdmitAccessoryControl()
    {
        Assert.False(GenericAsusService.MatchesAccessoryIdentity(new DeviceIdentitySnapshot
        {
            SystemManufacturer = "ASUSTeK COMPUTER INC.",
            UsbEndpoints = [new UsbEndpointObservation { VendorId = "0B05", ProductId = "FFFF" }]
        }));
    }
}
