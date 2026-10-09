using System.Security.Cryptography;
using System.Text;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Generic;

internal sealed record AsusAuraModule(
    ushort ProductId,
    string ZoneId,
    string Label,
    string HardwareIdentity,
    HidCollection Collection);

internal interface IAsusAuraTransport : IDisposable
{
    void Send(byte[] packet);
}

/// <summary>A retained approved HID collection; there is no model-name-based lighting write.</summary>
internal sealed class AsusAuraTransport : IAsusAuraTransport
{
    private readonly HidCollection _collection;
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    internal AsusAuraTransport(AsusAuraModule module)
    {
        if (!Matches(module.Collection))
        {
            throw new ArgumentException("The collection is not an approved Aura module.", nameof(module));
        }

        _collection = module.Collection;
        _handle = HidDevices.Open(_collection, false);
    }

    internal static IReadOnlyCollection<ushort> Products { get; } =
        [AsusAuraProtocol.Lightbar, AsusAuraProtocol.Keyboard, 0x1970, 0x1A9A, AsusAuraProtocol.Xg2025];

    public void Send(byte[] packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expected = AsusAuraProtocol.ReportLength(_collection.ProductId);
        if (packet.Length != expected || (packet[0] != AsusAuraProtocol.ReportId(_collection.ProductId)
                                          && !(_collection.ProductId != AsusAuraProtocol.Xg2025 && packet[0] == 0x5A &&
                                               packet[1] == 0xBA)))
        {
            throw new ArgumentException("The packet report ID or size does not match the Aura module.", nameof(packet));
        }

        HidDevices.SetFeature(_handle, _collection, packet);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    internal static bool Matches(HidCollection collection)
    {
        return collection.VendorId == AsusAuraProtocol.Vendor
               && AsusAuraProtocol.Supported(collection.ProductId) &&
               AsusAuraProtocol.Application(collection.UsagePage, collection.Usage)
               && (collection.ProductId == AsusAuraProtocol.Xg2025
                   ? collection.FeatureLength == 300
                   : collection.FeatureLength is 64 or 65);
    }

    internal static IReadOnlyList<AsusAuraModule> Discover()
    {
        return Modules(DiscoverCollections());
    }

    internal static IReadOnlyList<HidCollection> DiscoverCollections()
    {
        return HidDevices.Enumerate(AsusAuraProtocol.Vendor,
            Products);
    }

    internal static IReadOnlyList<AsusAuraModule> Modules(IReadOnlyList<HidCollection> collections)
    {
        var candidates = collections.Where(collection => Matches(collection)
                                                         && !string.IsNullOrWhiteSpace(collection.PhysicalLocation));
        return Array.AsReadOnly(candidates.GroupBy(collection => (collection.ProductId, collection.PhysicalLocation))
            .Select(group => group.OrderBy(collection => ApplicationOrder(collection))
                .ThenBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase).First())
            .Select(Module).OrderBy(module => module.ZoneId, StringComparer.Ordinal).ToArray());
    }

    internal static AsusAuraModule Module(HidCollection collection)
    {
        var physical = collection.PhysicalLocation.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(physical)))[..12].ToLowerInvariant();
        var label = collection.ProductId switch
        {
            AsusAuraProtocol.Lightbar => "Lightbar", AsusAuraProtocol.Keyboard => "Keyboard",
            _ => "XG Mobile (2025)"
        };
        return new AsusAuraModule(collection.ProductId, $"aura-{collection.ProductId:x4}-{key}", label,
            $"{collection.ProductId:x4}|{physical}|{collection.ReleaseNumber:x4}", collection);
    }

    private static int ApplicationOrder(HidCollection collection)
    {
        return collection.Usage switch
        {
            0x0076 => 0, 0x0079 => 1, 0x0080 => 2, _ => 3
        };
    }
}
