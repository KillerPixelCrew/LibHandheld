using System.Globalization;
using System.Runtime.InteropServices;

namespace LibHandheld.Internal.Windows;

/// <summary>HID interface arrivals/removals filtered in the callback to the exact supported vendor/products.</summary>
internal sealed class HidInterfaceNotifications : IDisposable
{
    private readonly Callback _callback;
    private readonly Action<string, bool> _changed;
    private readonly IReadOnlyCollection<ushort> _products;
    private readonly ushort _vendor;
    private nint _notification;

    internal HidInterfaceNotifications(ushort vendor, IReadOnlyCollection<ushort> products,
        Action<string, bool> changed)
    {
        _vendor = vendor;
        _products = products;
        _changed = changed;
        _callback = Notify;
        // Windows SDK 10.0.26100 cfgmgr32.h: 16-byte header + MAX_DEVICE_ID_LEN WCHAR union.
        Filter filter = new()
        {
            Size = 416, Flags = 0, Type = 0, Reserved = 0,
            ClassGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030")
        };
        var result = CM_Register_Notification(ref filter, 0, _callback, out _notification);
        if (result != 0)
        {
            throw new IOException($"HID arrival registration failed: CONFIGRET 0x{result:X}.");
        }
    }

    public void Dispose()
    {
        var notification = Interlocked.Exchange(ref _notification, 0);
        if (notification != 0)
        {
            _ = CM_Unregister_Notification(notification);
        }

        GC.KeepAlive(_callback);
    }

    private unsafe uint Notify(nint notification, nint context, uint action, nint data, uint size)
    {
        if (action > 1 || data == 0 || size < 26 || Marshal.ReadInt32(data) != 0)
        {
            return 0;
        }

        // CM_NOTIFY_EVENT_DATA: FilterType/Reserved, ClassGuid, then the variable WCHAR symbolic link.
        var path = new ReadOnlySpan<char>((void*)(data + 24), checked((int)((size - 24) / 2)));
        var terminator = path.IndexOf('\0');
        if (terminator >= 0)
        {
            path = path[..terminator];
        }

        if (Parse(path, "VID_") != _vendor || Parse(path, "PID_") is not { } product || !_products.Contains(product))
        {
            return 0;
        }

        try
        {
            _changed(path.ToString(), action == 0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        return 0;
    }

    private static ushort? Parse(ReadOnlySpan<char> path, ReadOnlySpan<char> marker)
    {
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + marker.Length + 4 <= path.Length
                          && ushort.TryParse(path.Slice(index + marker.Length, 4), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(ref Filter filter, nint context, Callback callback,
        out nint notification);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(nint notification);

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint Type;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(nint notification, nint context, uint action, nint data, uint size);
}
