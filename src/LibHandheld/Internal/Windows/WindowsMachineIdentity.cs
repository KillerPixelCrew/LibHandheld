using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LibHandheld.Internal.Identity;
using Microsoft.Win32;
using DeviceIdentitySnapshot = LibHandheld.Contracts.DeviceIdentitySnapshot;
using UsbEndpointObservation = LibHandheld.Contracts.UsbEndpointObservation;

namespace LibHandheld.Internal.Windows;

// Read-only registry, CPUID and HID interface metadata. No reports, EC methods or privileged drivers are used.
internal static class WindowsMachineIdentity
{
    internal static bool OnAcPower()
    {
        return !GetSystemPowerStatus(out var status) || status.ACLineStatus != 0;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    internal static DeviceIdentitySnapshot Read()
    {
        return ReadSmbios() with { UsbEndpoints = UsbEndpoints() };
    }

    internal static DeviceIdentitySnapshot ReadSmbios()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Handheld machine detection requires Windows.");
        }

        using var bios = OpenRegistry(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var processor = OpenRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = ReadValue(bios, "SystemManufacturer"),
            SystemProduct = ReadValue(bios, "SystemProductName"),
            SystemSku = ReadValue(bios, "SystemSKU"), SystemFamily = ReadValue(bios, "SystemFamily"),
            BaseboardManufacturer = ReadValue(bios, "BaseBoardManufacturer"),
            BaseboardProduct = ReadValue(bios, "BaseBoardProduct"),
            BaseboardVersion = ReadValue(bios, "BaseBoardVersion"), BiosVersion = ReadValue(bios, "BIOSVersion"),
            ProcessorName = ReadValue(processor, "ProcessorNameString"), CpuIdentity = ProcessorIdentity(),
            UsbEndpoints = []
        };
    }

    private static RegistryKey? OpenRegistry(string path)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(path, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? ReadValue(RegistryKey? key, string name)
    {
        try
        {
            return IdentityText.Normalize(key?.GetValue(name) as string);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static IReadOnlyList<UsbEndpointObservation> UsbEndpoints()
    {
        try
        {
            return HidDevices.EnumerateAll().Select(collection => new UsbEndpointObservation
            {
                VendorId = collection.VendorId.ToString("X4", CultureInfo.InvariantCulture),
                ProductId = collection.ProductId.ToString("X4", CultureInfo.InvariantCulture),
                DeviceRelease = collection.ReleaseNumber.ToString("X4", CultureInfo.InvariantCulture),
                InterfaceNumber = Hex(collection.InstancePath, "MI_", 2) is { } interfaceId
                    ? int.Parse(interfaceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : null,
                LocationPath = collection.PhysicalLocation,
                ReportLengths = new[] { (int)collection.InputLength, collection.OutputLength, collection.FeatureLength }
                    .Where(length => length > 0).Distinct().ToArray()
            }).Distinct().ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return [];
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
}
