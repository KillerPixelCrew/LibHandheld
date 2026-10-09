using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

internal readonly record struct StandardTouchSample(bool Touch, bool Click, float X, float Y);

/// <summary>Reads a selected model's real digitizer through the common HID usage parser.</summary>
internal sealed class StandardControllerTouchpad(StandardTouchProfile profile) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HidReportSource _source = new();
    private HidUsageReader? _decoder;
    private StandardTouchSample _latest;

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

    internal bool IsConnected => _source.IsConnected;

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal bool Start()
    {
        var collection = HidDevices.Unique(
            profile.UsbIds.SelectMany(id => HidDevices.Enumerate(id.Vendor, [id.Product])),
            candidate => candidate.UsagePage == 0x0D && candidate.Usage == 5 && candidate.InputLength > 0);
        if (collection is null)
        {
            return false;
        }

        var stream = HidDevices.OpenStream(collection);
        HidUsageReader decoder;
        try
        {
            decoder = new HidUsageReader(stream.SafeFileHandle);
            if (!decoder.HasValue(0x30) || !decoder.HasValue(0x31))
            {
                decoder.Dispose();
                stream.Dispose();
                return false;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _decoder = decoder;
        return _source.Start(stream, collection.InputLength, HandleReport,
            () =>
            {
                lock (_gate)
                {
                    _latest = default;
                }
            }, CancellationToken.None);
    }

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (_decoder is { } decoder)
        {
            Decode(decoder, report);
        }
    }

    private void Decode(HidUsageReader decoder, ReadOnlySpan<byte> report)
    {
        if (!decoder.TryValue(report, 0x30, out var x) ||
            !decoder.TryValue(report, 0x31, out var y, link: x.LinkCollection))
        {
            return;
        }

        var touch = false;
        foreach (var usage in decoder.Usages(report, 0x0D, x.LinkCollection))
        {
            touch |= usage == 0x42;
        }

        var click = false;
        foreach (var usage in decoder.Usages(report, 9))
        {
            click |= usage == 1;
        }

        lock (_gate)
        {
            _latest = new StandardTouchSample(touch, click, x.Signed, -y.Signed);
        }
    }

    internal async ValueTask StopAsync(CancellationToken token)
    {
        try
        {
            await _source.StopAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (!_source.IsConnected)
            {
                _decoder?.Dispose();
                _decoder = null;
            }

            lock (_gate)
            {
                _latest = default;
            }
        }
    }
}
