using System.Text;
using LibHandheld.Families.Asus;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Generic;

/// <summary>The dock's two ACPI IDs and 300-byte HID collection; no driver tuning belongs here.</summary>
internal sealed class AsusXgTransport : IDisposable
{
    private readonly IAsusAcpi _acpi;
    private readonly Lock _gate = new();
    private readonly HidCollection[] _lights;
    private readonly Action<byte[]>? _write;
    private bool _customFan;
    private bool _disposed;

    internal AsusXgTransport(IAsusAcpi acpi, HidCollection[] lights, Action<byte[]>? write = null)
    {
        _acpi = acpi;
        _lights = lights;
        _write = write;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acpi.Dispose();
        }
    }

    internal static AsusXgTransport? Open(IReadOnlyList<HidCollection>? collections = null)
    {
        var acpi = new WindowsAsusAcpi();
        if (!acpi.TryOpen())
        {
            acpi.Dispose();
            return null;
        }

        var lights = (collections ?? AsusAuraTransport.DiscoverCollections()).Where(collection =>
            collection.ProductId is 0x1970 or 0x1A9A or AsusAuraProtocol.Xg2025
            && collection.FeatureLength == 300
            && (collection.ProductId != AsusAuraProtocol.Xg2025 ||
                AsusAuraProtocol.Application(collection.UsagePage, collection.Usage))
            && !string.IsNullOrEmpty(collection.PhysicalLocation)).ToArray();
        // Multiple collections of one dock are expected; multiple independent docks are ambiguous.
        if (lights.Select(collection => collection.PhysicalLocation).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
        {
            lights = [];
        }

        return new AsusXgTransport(acpi, lights);
    }

    internal void Enable(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled)
            {
                ResetFanCore();
            }

            if (_acpi.Write(AsusAcpiId.XgEnabled, enabled ? 1U : 0) != 0)
            {
                throw new IOException("ASUS firmware rejected the XG Mobile change.");
            }
        }
    }

    internal void Light(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Write(Encoding.ASCII.GetBytes("^ASUS Tech.Inc."));
            foreach (var collection in _lights.Where(collection => collection.ProductId == AsusAuraProtocol.Xg2025))
            {
                using var handle = HidDevices.Open(collection, false);
                HidDevices.SetFeature(handle, collection, AsusAuraProtocol.Packet(AsusAuraProtocol.Xg2025, 0xE4, 2));
                HidDevices.SetFeature(handle, collection, AsusAuraProtocol.Packet(AsusAuraProtocol.Xg2025, 0xBD, 0, 1));
            }

            Write([0x5E, 0xC5, enabled ? (byte)0x50 : (byte)0]);
        }
    }

    internal void Fan(byte[] curve)
    {
        if (!AsusAcpiProtocol.IsValidCurve(curve))
        {
            throw new ArgumentException("Invalid XG Mobile fan curve.", nameof(curve));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var payload = new byte[19];
            payload[0] = 0x5E;
            payload[1] = 0xD1;
            payload[2] = 1;
            curve.CopyTo(payload, 3);
            Write(Encoding.ASCII.GetBytes("^ASUS Tech.Inc."));
            _customFan = true;
            Write(payload);
        }
    }

    internal void ReleaseFan()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_customFan)
            {
                ResetFanCore();
            }
        }
    }

    private void ResetFanCore()
    {
        // Retire the attempt before dispatch. A failed reset is uncertain and must not be
        // automatically repeated by release after a failed dock-disable command.
        _customFan = false;
        Write([0x5E, 0xD1, 0x02]);
    }

    private void Write(byte[] payload)
    {
        if (_write is not null)
        {
            _write(payload);
            return;
        }

        if (_lights.Length == 0)
        {
            throw new IOException("The XG Mobile 300-byte HID collection is absent or ambiguous.");
        }

        foreach (var collection in _lights)
        {
            using var handle = HidDevices.Open(collection, false);
            HidDevices.SetFeature(handle, collection, payload);
        }
    }
}
