using System.Management;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace LibHandheld.Internal.Windows;

/// <summary>Shared WMI discovery and invocation; the owning service caches and disposes the returned instance.</summary>
internal static class WmiProvider
{
    internal static ManagementObject? TryOpen(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
            new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) });
        using var collection = searcher.Get();
        foreach (ManagementObject candidate in collection)
        {
            using (candidate)
            {
                return (ManagementObject)candidate.Clone();
            }
        }

        return null;
    }

    internal static ManagementBaseObject? Invoke(ManagementObject instance, string method,
        ManagementBaseObject? arguments)
    {
        return instance.InvokeMethod(method, arguments,
            new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) });
    }

    internal static object? Invoke(ManagementObject instance, string method, object?[] arguments)
    {
        return instance.InvokeMethod(method, arguments);
    }
}
