using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Gpd;

/// <summary>Win 5 firmware 1.11 and later exposes real rear-button press/release reports.</summary>
internal sealed class GpdWin5OemSource : IProfiledOemSource
{
    private readonly bool[] _pressed = new bool[3];
    private Action<string, CanonicalButtons, bool>? _publish;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;
    public bool IsConnected => _reader is { IsCompleted: false };

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        var collections = HidDevices.Enumerate(0x2F24, [0x0137, 0x0135]).Where(collection =>
            collection.UsagePage == 0xFF00 && collection.Usage == 1 && collection.InputLength >= 14
            && collection.OutputLength >= 2 && collection.InputLength <= 1024).ToArray();
        if (collections.Length != 1)
        {
            return false;
        }

        var collection = collections[0];
        var stream = HidDevices.OpenStream(collection);
        try
        {
            var request = new byte[collection.OutputLength];
            request[0] = 1;
            request[1] = 0x41;
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var response = new byte[collection.InputLength];
            var count = await stream.ReadAsync(response, timeout.Token).ConfigureAwait(false);
            if (!SupportsEdges(response.AsSpan(0, count)))
            {
                stream.Dispose();
                return false;
            }

            _stream = stream;
            _publish = publish;
            _stop = new CancellationTokenSource();
            _reader = ReadAsync(stream, collection.InputLength, _stop.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            stream.Dispose();
            return false;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        _stop?.Dispose();
        _stop = null;
        _stream = null;
        _reader = null;
        _publish = null;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal static bool SupportsEdges(ReadOnlySpan<byte> response)
    {
        return response.Length >= 14
               && response[0] == 1 && response[1] == 0x41 && response[2] == 0x10
               && (response[12] > 1 || (response[12] == 1 && response[13] >= 0x11));
    }

    internal static CanonicalButtons Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length < 11 || report[0] != 1)
        {
            return CanonicalButtons.None;
        }

        return ((report[9] & 0x69) != 0 ? CanonicalButtons.RearPaddle1 : 0)
               | ((report[10] & 0x69) != 0 ? CanonicalButtons.RearPaddle2 : 0)
               | ((report[8] & 0x69) != 0 ? CanonicalButtons.QuickAccess : 0);
    }

    private async Task ReadAsync(FileStream stream, int length, CancellationToken token)
    {
        var report = new byte[length];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(report, token).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                if (count > 1 && report[1] is 0x21 or 0x22 or 0x25 or 0x27 or 0x2B or 0x41 or 0x43 or 0x44)
                {
                    continue;
                }

                var buttons = Decode(report.AsSpan(0, count));
                Emit(0, "l4", CanonicalButtons.RearPaddle1, buttons);
                Emit(1, "r4", CanonicalButtons.RearPaddle2, buttons);
                Emit(2, "gamepad", CanonicalButtons.QuickAccess, buttons);
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
            Emit(0, "l4", CanonicalButtons.RearPaddle1, 0);
            Emit(1, "r4", CanonicalButtons.RearPaddle2, 0);
            Emit(2, "gamepad", CanonicalButtons.QuickAccess, 0);
        }
    }

    private void Emit(int index, string id, CanonicalButtons button, CanonicalButtons state)
    {
        var down = (state & button) != 0;
        if (_pressed[index] == down)
        {
            return;
        }

        _pressed[index] = down;
        _publish?.Invoke(id, button, down);
    }
}
