using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

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
    private readonly Dictionary<string, ManagementObject> _providers = new(StringComparer.Ordinal);
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
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }

        _providers.Clear();
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

    private int? ReadInteger(string provider, string method, string outputName,
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

    private ManagementBaseObject? Invoke(string provider, string method,
        params (string Name, object Value)[] values)
    {
        if (!_providers.TryGetValue(provider, out var instance))
        {
            instance = WmiProvider.TryOpen(@"root\WMI", $"SELECT * FROM {provider}");
            if (instance is not null)
            {
                _providers.Add(provider, instance);
            }
        }

        if (instance is null)
        {
            throw new IOException($"Lenovo WMI provider {provider} is unavailable.");
        }

        using var arguments = instance.GetMethodParameters(method);
        foreach (var value in values)
        {
            arguments[value.Name] = value.Value;
        }

        try
        {
            return WmiProvider.Invoke(instance, method, arguments);
        }
        catch
        {
            _providers.Remove(provider);
            instance.Dispose();
            throw;
        }
    }
}

internal static class LegionIdentity
{
    public static string Binding(DeviceIdentitySnapshot identity)
    {
        return $"{identity.SystemProduct}|{identity.BaseboardProduct}|{identity.BiosVersion}";
    }
}
