using System.Threading.Channels;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>Older KUN and Loki firmware emits Guide as the left-button + XButton2 mouse chord.</summary>
internal sealed class OemMouseChordSource : IProfiledOemSource
{
    private readonly LowLevelHook _hook = new(14, "LibHandheld OEM mouse chord");
    private Channel<bool>? _edges;
    private volatile bool _failed;
    private bool _left;
    private bool _pressed;
    private Task? _pump;
    private volatile bool _running;
    private bool _x2;
    public bool IsConnected => _running && !_failed;

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _failed = false;
        _edges = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = true });
        var events = _edges;
        _pump = Task.Run(async () =>
        {
            await foreach (var down in events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                publish("guide-mouse", CanonicalButtons.Guide, down);
            }
        }, CancellationToken.None);
        try
        {
            var started = await _hook.StartMouseAsync(Handle, exception =>
                {
                    _failed = true;
                    ReleaseHeld();
                    events.Writer.TryComplete(exception);
                },
                token, ReleaseHeld).ConfigureAwait(false);
            _running = started;
            if (!started)
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return started;
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        await _hook.StopAsync(token).ConfigureAwait(false);
        _running = false;
        _edges?.Writer.TryComplete();
        try
        {
            if (_pump is not null)
            {
                await _pump.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_pump is null || _pump.IsCompleted)
            {
                _pump = null;
                _edges = null;
                _left = false;
                _x2 = false;
                _pressed = false;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private bool Handle(in MouseHookEvent mouse)
    {
        if (_failed || mouse.Injected)
        {
            return false;
        }

        switch (mouse.Message)
        {
            case 0x0201: _left = true; break;
            case 0x0202: _left = false; break;
            case 0x020B when mouse.Data >> 16 == 2: _x2 = true; break;
            case 0x020C when mouse.Data >> 16 == 2: _x2 = false; break;
            default: return false;
        }

        var down = _left && _x2;
        if (down != _pressed)
        {
            _pressed = down;
            _edges?.Writer.TryWrite(down);
        }

        // HC uses silenced:false. Both physical edges must reach Windows.
        return false;
    }

    private void ReleaseHeld()
    {
        if (_pressed)
        {
            _pressed = false;
            _edges?.Writer.TryWrite(false);
        }
    }
}
