using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Gpd;

/// <summary>Win 5 firmware 1.17 (minor byte 0x11) and later exposes rear-button press/release reports.</summary>
internal sealed class GpdWin5OemSource : IProfiledOemSource
{
    private readonly bool[] _pressed = new bool[3];
    private readonly HidReportSource _source = new();
    private Action<string, CanonicalButtons, bool>? _publish;
    public bool IsConnected => _source.IsConnected;

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x2F24, [0x0137, 0x0135]), collection =>
            collection.UsagePage == 0xFF00 && collection.Usage == 1 && collection.InputLength >= 14
            && collection.OutputLength >= 2);
        if (collection is null)
        {
            return false;
        }

        var stream = HidDevices.OpenStream(collection);
        try
        {
            var request = new byte[collection.OutputLength];
            request[0] = 1;
            request[1] = 0x41;
            var response = await HidDevices.ExchangeAsync(stream, request, collection.InputLength,
                response => response[0] == 1 && response[1] == 0x41, TimeSpan.FromSeconds(1), token,
                TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            if (!SupportsEdges(response))
            {
                stream.Dispose();
                return false;
            }

            _publish = publish;
            return _source.Start(stream, collection.InputLength, HandleReport, ReleaseHeld, token);
        }
        catch (TimeoutException)
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
        await _source.StopAsync(token).ConfigureAwait(false);
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

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (report.Length > 1 && report[1] is 0x21 or 0x22 or 0x25 or 0x27 or 0x2B or 0x41 or 0x43 or 0x44)
        {
            return;
        }

        var buttons = Decode(report);
        Emit(0, "l4", CanonicalButtons.RearPaddle1, buttons);
        Emit(1, "r4", CanonicalButtons.RearPaddle2, buttons);
        Emit(2, "gamepad", CanonicalButtons.QuickAccess, buttons);
    }

    private void ReleaseHeld()
    {
        Emit(0, "l4", CanonicalButtons.RearPaddle1, 0);
        Emit(1, "r4", CanonicalButtons.RearPaddle2, 0);
        Emit(2, "gamepad", CanonicalButtons.QuickAccess, 0);
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
