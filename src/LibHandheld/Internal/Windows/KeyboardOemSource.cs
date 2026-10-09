using System.Threading.Channels;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;

namespace LibHandheld.Internal.Windows;

/// <summary>Recognizes complete OEM chords; ordinary keys are passed on and repeats emit no extra press.</summary>
internal sealed class KeyboardOemSource(IReadOnlyList<KeyboardOemChord> chords) : IProfiledOemSource
{
    private readonly LowLevelKeyboardHook _hook = new("LibHandheld OEM keyboard");
    private readonly bool[] _keys = new bool[256];
    private readonly bool[] _pressed = new bool[chords.Count];
    private Channel<(int Index, bool Down)>? _events;
    private Task? _pump;

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish,
        CancellationToken cancellationToken)
    {
        if (chords.Count == 0)
        {
            return true;
        }

        _events = Channel.CreateBounded<(int, bool)>(new BoundedChannelOptions(128)
            { SingleReader = true, SingleWriter = true });
        var events = _events;
        _pump = Task.Run(async () =>
        {
            await foreach (var item in events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var chord = chords[item.Index];
                publish(chord.Id, chord.Button, item.Down);
            }
        }, CancellationToken.None);
        return await _hook.StartAsync(Handle, exception => events.Writer.TryComplete(exception), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _hook.StopAsync(cancellationToken).ConfigureAwait(false);
        _events?.Writer.TryComplete();
        if (_pump is not null)
        {
            await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _events = null;
        _pump = null;
        Array.Clear(_keys);
        Array.Clear(_pressed);
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private bool Handle(in KeyboardHookEvent key)
    {
        if (key.Injected || key.VirtualKey >= 256)
        {
            return false;
        }

        _keys[key.VirtualKey] = key.Down;
        var claim = false;
        for (var index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
            if (!chord.Keys.Contains(key.VirtualKey))
            {
                continue;
            }

            var down = chord.Keys.All(value => value < 256 && _keys[value]);
            claim |= down || _pressed[index];
            if (down == _pressed[index])
            {
                continue;
            }

            var wasHeld = false;
            for (var other = 0; other < chords.Count; other++)
            {
                wasHeld |= chords[other].Id == chord.Id && _pressed[other];
            }

            _pressed[index] = down;
            var nowHeld = false;
            for (var other = 0; other < chords.Count; other++)
            {
                nowHeld |= chords[other].Id == chord.Id && _pressed[other];
            }

            if (wasHeld == nowHeld)
            {
                continue;
            }

            if (_events?.Writer.TryWrite((index, nowHeld)) != true)
            {
                _events?.Writer.TryComplete(new IOException("The OEM keyboard event queue overflowed."));
            }
        }

        return claim;
    }
}
