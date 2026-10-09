namespace LibHandheld.Internal.Windows;

internal delegate void HidReportHandler(ReadOnlySpan<byte> report);

/// <summary>Owns an already attributed HID stream, one reusable report buffer and its reader.</summary>
internal sealed class HidReportSource : IAsyncDisposable
{
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;
    internal bool IsConnected => _reader is { IsCompleted: false };

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal bool Start(FileStream stream, int reportLength, HidReportHandler handler, Action stopped,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (_reader is not null)
            {
                throw new InvalidOperationException("The previous HID reader must be joined.");
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _stream = stream;
        _stop = new CancellationTokenSource();
        _reader = ReadAsync(stream, reportLength, handler, stopped, _stop.Token);
        return IsConnected;
    }

    internal async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        try
        {
            if (_reader is not null)
            {
                await _reader.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            // A completed failed reader is joined too. Preserve only a reader whose caller stopped waiting.
            if (_reader is null || _reader.IsCompleted)
            {
                _reader = null;
                _stream = null;
                _stop?.Dispose();
                _stop = null;
            }
        }
    }

    private static async Task ReadAsync(FileStream stream, int length, HidReportHandler handler,
        Action stopped, CancellationToken token)
    {
        var report = new byte[length];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(report, token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                handler(report.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            stopped();
        }
    }
}
