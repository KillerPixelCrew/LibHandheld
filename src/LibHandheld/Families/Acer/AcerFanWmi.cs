using System.Globalization;
using System.Management;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>The AcerGamingFunction fan getters and setters, seamed so fixtures can stand in for WMI.</summary>
internal interface IAcerFanWmi : IDisposable
{
    /// <summary>Invokes a getter with uint32 gmInput and returns the complete uint64 gmOutput.</summary>
    ulong Read(string method, uint input);

    /// <summary>Invokes a packed uint64 setter and returns its uint32 gmOutput status.</summary>
    uint Write(string method, ulong input);
}

/// <summary>The live root\WMI AcerGamingFunction instance; callers serialize access.</summary>
internal sealed class AcerFanWmi(ManagementObject instance) : IAcerFanWmi
{
    internal static AcerFanWmi? TryOpen()
    {
        var instance = WmiProvider.TryOpen("root\\WMI", "SELECT * FROM AcerGamingFunction");
        return instance is null ? null : new AcerFanWmi(instance);
    }

    public ulong Read(string method, uint input)
    {
        using var arguments = instance.GetMethodParameters(method);
        arguments["gmInput"] = input;
        using var reply = WmiProvider.Invoke(instance, method, arguments);
        return reply?["gmOutput"] is { } value
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
            : throw new IOException($"Acer {method} returned no value.");
    }

    public uint Write(string method, ulong input)
    {
        using var arguments = instance.GetMethodParameters(method);
        arguments["gmInput"] = input;
        using var reply = WmiProvider.Invoke(instance, method, arguments);
        return reply?["gmOutput"] is { } value
            ? Convert.ToUInt32(value, CultureInfo.InvariantCulture)
            : throw new IOException($"Acer {method} returned no status; effect unknown.");
    }

    public void Dispose()
    {
        instance.Dispose();
    }
}
