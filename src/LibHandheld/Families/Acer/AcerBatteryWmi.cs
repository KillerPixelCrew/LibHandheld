using System.Globalization;
using System.Management;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>The battery-1 optimized-charging query and setter.</summary>
internal interface IAcerBatteryWmi : IDisposable
{
    /// <summary>Reads function 1 for battery 1; support is bit 0 and only a 0/1 status is a known value.</summary>
    (bool Supported, bool? Enabled) Read();
    /// <summary>Writes only function-mask bit 0; zero uint16 uReturn acknowledges the write.</summary>
    void Write(bool enabled, CancellationToken token, Action? dispatched = null);
}

/// <summary>The native BatteryControl provider; reserved fields and function masks follow Acer's calls.</summary>
internal sealed class AcerBatteryWmi(ManagementObject instance) : IAcerBatteryWmi
{
    internal static IAcerBatteryWmi? TryOpen()
    {
        var instance = WmiProvider.TryOpen("root\\WMI", "SELECT * FROM BatteryControl");
        return instance is null ? null : new AcerBatteryWmi(instance);
    }

    public (bool Supported, bool? Enabled) Read()
    {
        using var input = instance.GetMethodParameters("GetBatteryHealthControlStatus");
        input["uBatteryNo"] = (byte)1;
        input["uFunctionQuery"] = (byte)1;
        input["uReserved"] = new byte[2];
        using var output = WmiProvider.Invoke(instance, "GetBatteryHealthControlStatus", input);
        if (output?["uReturn"] is not byte[] status || status.Length != 2 || status[0] != 0 || status[1] != 0
            || output["uFunctionStatus"] is not byte[] functions || functions.Length != 5
            || output["uFunctionList"] is not byte functionList)
        {
            throw new IOException("Acer battery health response is unavailable or rejected.");
        }
        var supported = (functionList & 1) != 0;
        return (supported, supported && functions[0] <= 1 ? functions[0] == 1 : null);
    }

    public void Write(bool enabled, CancellationToken token, Action? dispatched = null)
    {
        using var input = instance.GetMethodParameters("SetBatteryHealthControl");
        input["uBatteryNo"] = (byte)1;
        input["uFunctionMask"] = (byte)1;
        input["uFunctionStatus"] = enabled ? (byte)1 : (byte)0;
        input["uReservedIn"] = new byte[5];
        token.ThrowIfCancellationRequested();
        dispatched?.Invoke();
        using var output = WmiProvider.Invoke(instance, "SetBatteryHealthControl", input);
        if (output?["uReturn"] is not { } result || Convert.ToUInt16(result, CultureInfo.InvariantCulture) != 0)
        {
            throw new IOException("Acer battery health write was not acknowledged; effect unknown.");
        }
    }

    public void Dispose()
    {
        instance.Dispose();
    }
}
