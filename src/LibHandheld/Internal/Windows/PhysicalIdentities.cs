using System.Globalization;
using LibHandheld.Contracts;

namespace LibHandheld.Internal.Windows;

/// <summary>Builds physical identity from the attributed collection or node, never from a guessed controller slot.</summary>
internal static class PhysicalIdentities
{
    internal static PhysicalDeviceIdentity FromCollection(HidCollection collection)
    {
        return Create(collection.InstancePath,
            collection.PhysicalLocation, collection.VendorId, collection.ProductId);
    }

    internal static PhysicalDeviceIdentity FromNode(DeviceNode node)
    {
        return Create(node.InstancePath,
            node.PhysicalLocation, UsbIds.Parse(node.InstancePath, "VID_"), UsbIds.Parse(node.InstancePath, "PID_"));
    }

    private static PhysicalDeviceIdentity Create(string instance, string location, ushort? vendor, ushort? product)
    {
        return new PhysicalDeviceIdentity
        {
            InstancePath = instance, LocationPath = location,
            VendorId = vendor?.ToString("X4", CultureInfo.InvariantCulture),
            ProductId = product?.ToString("X4", CultureInfo.InvariantCulture),
            RequiresHiding = vendor is not null && product is not null && !string.IsNullOrWhiteSpace(location)
        };
    }
}
