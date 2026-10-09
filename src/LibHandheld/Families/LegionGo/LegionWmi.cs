using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Identity;
using Microsoft.Win32;

namespace LibHandheld.Families.LegionGo;

internal interface ILegionWmi : IDisposable
{
    bool FeaturesAvailable { get; }
    bool ModesAvailable { get; }
    bool FansAvailable { get; }
    int? ReadFeature(int id);
    void WriteFeature(int id, int value);
    int? ReadMode();
    void WriteMode(int value);
    ushort[]? ReadFanTable();
    void WriteFanTable(ushort[] values);
}

internal sealed class LegionWmi : ILegionWmi
{
    private bool? _fans;
    private bool? _features;
    private bool? _modes;
    public bool FeaturesAvailable => _features ??= HasMethod("LENOVO_OTHER_METHOD", "SetFeatureValue");
    public bool ModesAvailable => _modes ??= HasMethod("LENOVO_GAMEZONE_DATA", "SetSmartFanMode");
    public bool FansAvailable => _fans ??= HasMethod("LENOVO_FAN_METHOD", "Fan_Set_Table");

    public int? ReadFeature(int id)
    {
        return ReadInteger("LENOVO_OTHER_METHOD", "GetFeatureValue", "Value", ("IDs", id));
    }

    public void WriteFeature(int id, int value)
    {
        Invoke("LENOVO_OTHER_METHOD", "SetFeatureValue", ("IDs", id), ("value", value))?.Dispose();
    }

    public int? ReadMode()
    {
        return ReadInteger("LENOVO_GAMEZONE_DATA", "GetSmartFanMode", "Data");
    }

    public void WriteMode(int value)
    {
        Invoke("LENOVO_GAMEZONE_DATA", "SetSmartFanMode", ("Data", value))?.Dispose();
    }

    public ushort[]? ReadFanTable()
    {
        try
        {
            using var output = Invoke("LENOVO_FAN_METHOD", "Fan_Get_Table", ("FanID", 1), ("SensorID", 0));
            return output?["FanTable"] is uint[] values && values.Length == 10
                ? values.Select(v => checked((ushort)v)).ToArray()
                : null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or OverflowException or IOException)
        {
            return null;
        }
    }

    public void WriteFanTable(ushort[] values)
    {
        Invoke("LENOVO_FAN_METHOD", "Fan_Set_Table", ("FanTable", LegionProtocol.FanTable(values)))?.Dispose();
    }

    public void Dispose()
    {
    }

    private static bool HasMethod(string provider, string method)
    {
        try
        {
            using var definition = new ManagementClass(@"root\WMI", provider, null);
            definition.Get();
            return definition.Methods.Cast<MethodData>().Any(m => m.Name == method);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or IOException)
        {
            return false;
        }
    }

    private static int? ReadInteger(string provider, string method, string outputName,
        params (string Name, object Value)[] values)
    {
        try
        {
            using var output = Invoke(provider, method, values);
            return output?[outputName] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException
                                       or IOException)
        {
            return null;
        }
    }

    private static ManagementBaseObject? Invoke(string provider, string method,
        params (string Name, object Value)[] values)
    {
        using var query = new ManagementObjectSearcher(@"root\WMI", $"SELECT * FROM {provider}");
        using var collection = query.Get();
        foreach (ManagementObject instance in collection)
        {
            using (instance)
            using (var arguments = instance.GetMethodParameters(method))
            {
                foreach (var value in values)
                {
                    arguments[value.Name] = value.Value;
                }

                return instance.InvokeMethod(method, arguments,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) });
            }
        }

        throw new IOException($"Lenovo WMI provider {provider} is unavailable.");
    }
}

internal static class LegionIdentity
{
    public static DeviceIdentitySnapshot Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Value(key, "SystemManufacturer"),
            SystemProduct = Value(key, "SystemProductName"),
            SystemSku = Value(key, "SystemSKU"),
            BaseboardProduct = Value(key, "BaseBoardProduct"),
            BiosVersion = Value(key, "BIOSVersion")
        };
    }

    public static string Binding(DeviceIdentitySnapshot identity)
    {
        return $"{identity.SystemProduct}|{identity.BaseboardProduct}|{identity.BiosVersion}";
    }

    private static string? Value(RegistryKey? key, string name)
    {
        return key?.GetValue(name) is string value ? IdentityText.Normalize(value) : null;
    }
}
