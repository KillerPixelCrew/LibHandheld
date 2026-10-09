using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Devices;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

internal readonly record struct StandardTouchSample(bool Touch, bool Click, float X, float Y);

/// <summary>Reads the selected model's real digitizer collection through its own HID descriptor.</summary>
internal sealed class StandardControllerTouchpad(StandardTouchProfile profile) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private nint _descriptor;
    private StandardTouchSample _latest;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;

    internal StandardTouchSample Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    internal bool IsConnected => _reader is { IsCompleted: false };

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal bool Start()
    {
        var collections = profile.UsbIds.SelectMany(id => HidDevices.Enumerate(id.Vendor, [id.Product]))
            .Where(collection => collection.UsagePage == 0x0D && collection.Usage == 5 &&
                                 collection.InputLength is > 0 and <= 4096).ToArray();
        if (collections.Length != 1)
        {
            return false;
        }

        var collection = collections[0];
        var details = HidDevices.Inspect(collection);
        var xs = details.Capabilities.Where(cap => cap.ReportType == HidReportType.Input && !cap.IsButton
            && cap.UsagePage == 1 && cap.UsageMin <= 0x30 && cap.UsageMax >= 0x30).ToArray();
        var ys = details.Capabilities.Where(cap => cap.ReportType == HidReportType.Input && !cap.IsButton
            && cap.UsagePage == 1 && cap.UsageMin <= 0x31 && cap.UsageMax >= 0x31).ToArray();
        var x = xs.FirstOrDefault();
        var y = ys.FirstOrDefault(cap =>
            x is not null && cap.LinkCollection == x.LinkCollection && cap.ReportId == x.ReportId);
        if (x is null || y is null || x.LogicalMax <= x.LogicalMin || y.LogicalMax <= y.LogicalMin)
        {
            return false;
        }

        var stream = HidDevices.OpenStream(collection);
        if (!HidD_GetPreparsedData(stream.SafeFileHandle, out var descriptor))
        {
            stream.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _stream = stream;
        _descriptor = descriptor;
        _stop = new CancellationTokenSource();
        _reader = ReadAsync(stream, collection.InputLength, descriptor, x, y, _stop.Token);
        return true;
    }

    private async Task ReadAsync(FileStream stream, int length, nint descriptor, HidCapability x, HidCapability y,
        CancellationToken token)
    {
        var buffer = new byte[length];
        var usages = new ushort[32];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                if (buffer[0] != x.ReportId)
                {
                    continue;
                }

                if (HidP_GetUsageValue(0, 1, x.LinkCollection, 0x30, out var rawX, descriptor, buffer, (uint)count) !=
                    0x00110000
                    || HidP_GetUsageValue(0, 1, y.LinkCollection, 0x31, out var rawY, descriptor, buffer,
                        (uint)count) != 0x00110000)
                {
                    continue;
                }

                var usageCount = (uint)usages.Length;
                var touch = false;
                if (HidP_GetUsages(0, 0x0D, x.LinkCollection, usages, ref usageCount, descriptor, buffer,
                        (uint)count) == 0x00110000)
                {
                    for (var index = 0; index < usageCount; index++)
                    {
                        touch |= usages[index] == 0x42; // Tip switch
                    }
                }

                usageCount = (uint)usages.Length;
                var click = false;
                if (HidP_GetUsages(0, 9, 0, usages, ref usageCount, descriptor, buffer, (uint)count) == 0x00110000)
                {
                    for (var index = 0; index < usageCount; index++)
                    {
                        click |= usages[index] == 1;
                    }
                }

                lock (_gate)
                {
                    _latest = new StandardTouchSample(touch, click, Normalize(rawX, x), -Normalize(rawY, y));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _latest = default;
            }
        }
    }

    private static float Normalize(uint value, HidCapability cap)
    {
        var signed = cap.LogicalMin < 0 && cap.BitSize is > 0 and < 32
            ? (int)(value << (32 - cap.BitSize)) >> (32 - cap.BitSize)
            : (long)value;
        return Math.Clamp((float)((signed - cap.LogicalMin) * 2d / (cap.LogicalMax - cap.LogicalMin) - 1), -1, 1);
    }

    internal async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        if (_descriptor != 0)
        {
            _ = HidD_FreePreparsedData(_descriptor);
            _descriptor = 0;
        }

        _stop?.Dispose();
        _stop = null;
        _stream = null;
        _reader = null;
        lock (_gate)
        {
            _latest = default;
        }
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint descriptor);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(nint descriptor);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value,
        nint descriptor, byte[] report, uint length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] usages, ref uint count,
        nint descriptor, byte[] report, uint length);
}
