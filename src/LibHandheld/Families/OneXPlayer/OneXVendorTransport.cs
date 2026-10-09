using System.Globalization;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>The collection selector is part of each protocol generation, rather than a VID-only guess.</summary>
internal sealed class OneXVendorTransport : IDisposable
{
    private readonly HidCollection? _collection;
    private readonly Lock _gate = new();
    private readonly SafeFileHandle? _handle;
    private readonly SerialPort? _serial;
    private bool _disposed;

    private OneXVendorTransport(HidCollection collection)
    {
        _collection = collection;
        _handle = HidDevices.Open(collection, false);
    }

    private OneXVendorTransport(SerialPort serial)
    {
        _serial = serial;
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
            _handle?.Dispose();
            _serial?.Dispose();
        }
    }

    internal static OneXVendorTransport? Open(OneXLightingKind kind)
    {
        if (kind == OneXLightingKind.None)
        {
            return null;
        }

        if (kind == OneXLightingKind.Serial)
        {
            var port = OneXSerial.Open(false);
            return port is null ? null : new OneXVendorTransport(port);
        }

        var old = kind is OneXLightingKind.HidV2 or OneXLightingKind.HidV1G1;
        var collection = HidDevices.Unique(HidDevices.Enumerate(old ? (ushort)0x1A2C : (ushort)0x1A86,
            old ? [0xB001] : [0xFE00, 0x1305]), item =>
            item.UsagePage == (old ? 0xFF01 : 0xFF00) && item.Usage == 1 && item.OutputLength is 64 or 65);
        return collection is null ? null : new OneXVendorTransport(collection);
    }

    internal void Write(ReadOnlySpan<byte> frame, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_serial is not null)
            {
                var data = frame.ToArray();
                _serial.Write(data, 0, data.Length);
            }
            else
            {
                var output = new byte[_collection!.OutputLength];
                if (frame.Length == output.Length)
                {
                    frame.CopyTo(output);
                }
                else if (frame.Length + 1 == output.Length)
                {
                    frame.CopyTo(output.AsSpan(1));
                }
                else
                {
                    throw new InvalidDataException("The vendor command does not fit this collection.");
                }

                HidDevices.WriteOutput(_handle!, _collection, output);
            }
        }
    }
}

internal static class OneXSerial
{
    internal static SerialPort? Open(bool allowLegacyPort)
    {
        using var search = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, PNPDeviceID, DeviceID FROM Win32_PnPEntity WHERE ClassGuid='{4d36e978-e325-11ce-bfc1-08002be10318}'");
        using var ports = search.Get();
        string? selected = null;
        foreach (ManagementObject item in ports)
        {
            using (item)
            {
                var id = Convert.ToString(item["PNPDeviceID"], CultureInfo.InvariantCulture) ?? "";
                var name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? "";
                var match = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                if (id.Contains("VID_1A86&PID_7523", StringComparison.OrdinalIgnoreCase))
                {
                    selected = match.Groups[1].Value;
                    break;
                }

                if (!allowLegacyPort || !id.Contains("PNP0501", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var resources = item.GetRelated("Win32_PortResource");
                foreach (ManagementObject resource in resources)
                {
                    using (resource)
                    {
                        if (Convert.ToUInt64(resource["StartingAddress"], CultureInfo.InvariantCulture) == 0x3E8)
                        {
                            selected = match.Groups[1].Value;
                        }
                    }
                }
            }
        }

        if (selected is null)
        {
            return null;
        }

        var serial = new SerialPort(selected, 115200, Parity.Even, 8, StopBits.Two)
            { ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 250 };
        try
        {
            serial.Open();
            return serial;
        }
        catch
        {
            serial.Dispose();
            throw;
        }
    }
}
