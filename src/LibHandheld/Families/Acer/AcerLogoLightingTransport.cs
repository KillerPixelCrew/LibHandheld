using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Acer;

/// <summary>Feature reports on the exact ENEK5130 rear-logo collection.</summary>
internal interface IAcerLogoLightingTransport : IDisposable
{
    void Read(byte[] report);
    void Write(byte[] report);
}

/// <summary>Owns the one native HID handle used by rear-logo lighting.</summary>
internal sealed class AcerLogoLightingTransport(SafeFileHandle handle) : IAcerLogoLightingTransport
{
    internal static IAcerLogoLightingTransport? TryOpen()
    {
        // This is I2C HID (ENEK5130), so USB VID_/PID_ path filtering cannot discover it.
        var collection = HidDevices.Unique(HidDevices.EnumerateAll(),
            item => item.VendorId == 0x0CF2 && item.ProductId == 0x5130
                    && item.UsagePage == 0xFF5A && item.Usage == 1 && item.ReleaseNumber == 1
                    && item.FeatureLength == 11);
        return collection is null ? null : new AcerLogoLightingTransport(HidDevices.Open(collection, overlapped: false));
    }

    public void Read(byte[] report)
    {
        if (!NativeHid.HidD_GetFeature(handle, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The logo capability query failed.");
        }
    }

    public void Write(byte[] report)
    {
        if (!NativeHid.HidD_SetFeature(handle, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Rear-logo write failed; effect unknown.");
        }
    }

    public void Dispose()
    {
        handle.Dispose();
    }
}
