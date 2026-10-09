using System.Buffers.Binary;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Devices;

public sealed class UsbPortIdentityTests
{
    private const string Root = "PCIROOT(0)#PCI(1400)#USBROOT(0)";

    [Fact]
    public void AFixedLeafInAnExternalHubIsStillExternal()
    {
        UsbHubIdentity[] hubs = [new("root", Root, "root-key"), new("child", Root + "#USB(4)", "hub-key")];
        Assert.False(UsbPortIdentity.ProvesInternal(Root + "#USB(4)#USB(1)", "pad-key", hubs,
            (hub, port) => hub.InterfacePath == "root"
                ? new UsbPortEvidence(true, "hub-key")
                : new UsbPortEvidence(false, "pad-key")));
        Assert.True(UsbPortIdentity.ProvesInternal(Root + "#USB(4)#USB(1)", "pad-key", hubs,
            (hub, port) => hub.InterfacePath == "root"
                ? new UsbPortEvidence(false, "hub-key")
                : new UsbPortEvidence(false, "pad-key")));
    }

    [Fact]
    public void UnknownPortOrWrongActualDevnodeNeverQualifies()
    {
        UsbHubIdentity[] hubs = [new("root", Root, "root-key")];
        Assert.False(UsbPortIdentity.ProvesInternal(Root + "#USB(1)", "pad-key", hubs, (_, _) => null));
        Assert.False(UsbPortIdentity.ProvesInternal(Root + "#USB(1)", "pad-key", hubs,
            (_, _) => new UsbPortEvidence(false, "another-device")));
        Assert.False(UsbPortIdentity.ProvesInternal(Root + "#USB(1)", "pad-key", [.. hubs, hubs[0]],
            (_, _) => new UsbPortEvidence(false, "pad-key")));
    }

    [Fact]
    public void PackedConnectorPrefixRequiresCorrectPortLengthAndReservedBits()
    {
        var report = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(report, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(4), 18);
        Assert.Equal(false, UsbPortIdentity.UserConnectable(report, 3));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 1);
        Assert.Equal(true, UsbPortIdentity.UserConnectable(report, 3));
        Assert.Null(UsbPortIdentity.UserConnectable(report, 2));
        Assert.Null(UsbPortIdentity.UserConnectable(report.AsSpan(0, 17), 3));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 16);
        Assert.Null(UsbPortIdentity.UserConnectable(report, 3));
        Assert.Equal(0x220458U, UsbPortIdentity.ConnectorIoctl);
        Assert.Equal(0x220420U, UsbPortIdentity.DriverKeyIoctl);
    }

    [Theory]
    [InlineData("PCIROOT(0)#USBROOT(0)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(0)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(1)#USBMI(0)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(A)")]
    [InlineData("PCIROOT(0)#USBROOT(0)#USB(256)")]
    public void UnprovedLocationShapesAreRejected(string path)
    {
        Assert.False(UsbPortIdentity.TryPath(path, out _));
    }
}
