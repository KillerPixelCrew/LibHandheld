using System.Numerics;
using System.Text;
using LibHandheld.Families.Zotac;

namespace LibHandheld.Tests.Zotac;

public sealed class ZotacModelsTests
{
    [Fact]
    public void BoardIdentityPrecedesTheHhdMarketingAlias()
    {
        var profile = ZotacModels.Match(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "PC PARTNER LIMITED", BaseboardProduct = "G0A1W",
            SystemProduct = "Any marketing name"
        });
        Assert.Equal("zotac-zone", profile?.Definition.Id);
        Assert.Equal(new Vector3(1, 3, -2), profile!.Motion!.Gyro.Apply(new Vector3(1, 2, 3)));
        Assert.Equal(new Vector3(1, 3, 2), profile.Motion.Acceleration.Apply(new Vector3(1, 2, 3)));
        Assert.Null(ZotacModels.Match(new DeviceIdentitySnapshot
            { BaseboardManufacturer = "ZOTAC", BaseboardProduct = "Other board" }));
    }

    [Fact]
    public void VendorCommandsUseThePublishedPayloadShapeAndKermitChecksum()
    {
        Assert.Equal((ushort)0x2189, ZotacProtocol.Crc(Encoding.ASCII.GetBytes("123456789")));
        var rear = ZotacProtocol.Rear(1);
        Assert.Equal(65, rear.Length);
        Assert.Equal((byte)0xE1, rear[1]);
        Assert.Equal((byte)60, rear[4]);
        Assert.Equal((byte)0xA1, rear[5]);
        Assert.Equal((byte)1, rear[6]);
        Assert.Equal((byte)9, rear[11]);
        Assert.Equal((byte)68, rear[13]);
        var color = ZotacProtocol.Color(1, 0x123456);
        Assert.Equal((byte)0xAD, color[5]);
        Assert.Equal((byte)1, color[7]);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, color[10..13]);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, color[37..40]);
    }

    [Fact]
    public void VramEncodingRefusesOverflowBeforeDispatch()
    {
        Assert.Equal((byte)176, ZotacNativeService.EncodeVram(32, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZotacNativeService.EncodeVram(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZotacNativeService.EncodeVram(64, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZotacNativeService.EncodeVram(16, 13));
    }
}
