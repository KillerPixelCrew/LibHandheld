using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>Older KUN and Loki firmware emits Guide as the left-button + XButton2 mouse chord.</summary>
internal sealed class OemMouseChordSource : IProfiledOemSource
{
    private readonly LowLevelHookPump<bool> _pump;
    private bool _left;
    private bool _pressed;
    private bool _x2;

    public OemMouseChordSource()
    {
        _pump = new LowLevelHookPump<bool>("LibHandheld OEM mouse chord", Handle, ReleaseHeld, Reset);
    }

    public bool IsConnected => _pump.IsConnected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        return _pump.StartAsync(down => publish("guide-mouse", CanonicalButtons.Guide, down), install: true, token);
    }

    public ValueTask StopAsync(CancellationToken token)
    {
        return _pump.StopAsync(token);
    }

    public ValueTask DisposeAsync()
    {
        return _pump.DisposeAsync();
    }

    private bool Handle(in MouseHookEvent mouse)
    {
        if (_pump.Failed || mouse.Injected)
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
            _pump.Write(down);
        }

        // HC uses silenced:false. Both physical edges must reach Windows.
        return false;
    }

    private void ReleaseHeld()
    {
        if (_pressed)
        {
            _pressed = false;
            _pump.Write(false);
        }
    }

    private void Reset()
    {
        _left = false;
        _x2 = false;
        _pressed = false;
    }
}
