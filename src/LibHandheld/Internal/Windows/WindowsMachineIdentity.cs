using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LibHandheld.Internal.Identity;
using DeviceIdentitySnapshot = LibHandheld.Contracts.DeviceIdentitySnapshot;
using UsbEndpointObservation = LibHandheld.Contracts.UsbEndpointObservation;

namespace LibHandheld.Internal.Windows;

// Identity collection is read-only. No EC, vendor DLL, HID or privileged driver is opened here.
internal static class WindowsMachineIdentity
{
    internal static DeviceIdentitySnapshot Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Handheld machine detection requires Windows.");
        }

        var system = Query("SELECT Manufacturer, Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem");
        var board = Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
        var bios = Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
        var processor = Query("SELECT Name FROM Win32_Processor");
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Get(system, "Manufacturer"), SystemProduct = Get(system, "Model"),
            SystemSku = Get(system, "SystemSKUNumber"), SystemFamily = Get(system, "SystemFamily"),
            BaseboardManufacturer = Get(board, "Manufacturer"), BaseboardProduct = Get(board, "Product"),
            BaseboardVersion = Get(board, "Version"), BiosVersion = Get(bios, "SMBIOSBIOSVersion"),
            ProcessorName = Get(processor, "Name"), CpuIdentity = ProcessorIdentity(), UsbEndpoints = UsbEndpoints()
        };
    }

    private static Dictionary<string, string?> Query(string query)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\CIMV2", query);
            using var candidates = searcher.Get();
            foreach (var item in candidates.Cast<ManagementObject>())
            {
                using (item)
                {
                    foreach (var property in item.Properties)
                    {
                        values[property.Name] = IdentityText.Normalize(
                            Convert.ToString(property.Value, CultureInfo.InvariantCulture));
                    }
                }

                break;
            }
        }
        catch (Exception exception) when (exception is ManagementException or COMException or
                                              UnauthorizedAccessException)
        {
            // An unavailable observation must not become a guessed model identity.
        }

        return values;
    }

    private static IReadOnlyList<UsbEndpointObservation> UsbEndpoints()
    {
        var endpoints = new List<UsbEndpointObservation>();
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\CIMV2",
                @"SELECT DeviceID, HardwareID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_%'");
            using var candidates = searcher.Get();
            foreach (var item in candidates.Cast<ManagementObject>())
            {
                using (item)
                {
                    var path = Convert.ToString(item["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;
                    var vendor = Hex(path, "VID_", 4);
                    var product = Hex(path, "PID_", 4);
                    if (vendor is null || product is null)
                    {
                        continue;
                    }

                    var hardwareIds = item["HardwareID"] as string[];
                    var release = hardwareIds?.Select(id => Hex(id, "REV_", 4))
                        .FirstOrDefault(value => value is not null);
                    var interfaceText = Hex(path, "MI_", 2);
                    endpoints.Add(new UsbEndpointObservation
                    {
                        VendorId = vendor, ProductId = product, DeviceRelease = release,
                        InterfaceNumber = interfaceText is null
                            ? null
                            : int.Parse(interfaceText, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    });
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or COMException or
                                              UnauthorizedAccessException)
        {
            // Empty observations fail endpoint-dependent detection instead of authorizing an incorrect device.
        }

        return Array.AsReadOnly(endpoints.ToArray());
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
    {
        return values.GetValueOrDefault(key);
    }

    private static string? ProcessorIdentity()
    {
        if (!X86Base.IsSupported)
        {
            return null;
        }

        var signature = (uint)X86Base.CpuId(1, 0).Eax;
        var baseFamily = (signature >> 8) & 0xF;
        var family = baseFamily + (baseFamily == 0xF ? (signature >> 20) & 0xFF : 0);
        var model = (signature >> 4) & 0xF;
        if (baseFamily is 6 or 0xF)
        {
            model |= (signature >> 12) & 0xF0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{family}-{model}-{signature & 0xF}");
    }

    private static string? Hex(string value, string marker, int length)
    {
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + marker.Length + length > value.Length)
        {
            return null;
        }

        var hex = value.Substring(start + marker.Length, length);
        return hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }
}
