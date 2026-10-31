using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>Recognizes complete OEM chords; ordinary keys are passed on and repeats emit no extra press.</summary>
internal sealed class KeyboardOemSource : IProfiledOemSource
{
    private readonly IReadOnlyList<KeyboardOemChord> _chords;
    private readonly bool[] _keys = new bool[256];
    private readonly bool[] _pressed;
    private readonly LowLevelHookPump<(int Index, bool Down)> _pump;

    public KeyboardOemSource(IReadOnlyList<KeyboardOemChord> chords)
    {
        _chords = chords;
        _pressed = new bool[chords.Count];
        _pump = new LowLevelHookPump<(int Index, bool Down)>("LibHandheld OEM keyboard", Handle, ReleaseHeld, Reset);
    }

    public bool IsConnected => _pump.IsConnected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish,
        CancellationToken cancellationToken)
    {
        // With no chord to recognize the source is connected without installing a hook.
        return _pump.StartAsync(item =>
        {
            var chord = _chords[item.Index];
            publish(chord.Id, chord.Button, item.Down);
        }, install: _chords.Count > 0, cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return _pump.StopAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _pump.DisposeAsync();
    }

    private bool Handle(in KeyboardHookEvent key)
    {
        if (_pump.Failed || key.Injected || key.VirtualKey >= 256)
        {
            return false;
        }

        var prior = _keys[key.VirtualKey];
        if (prior && key.Down)
        {
            return false;
        }

        _keys[key.VirtualKey] = key.Down;
        for (var index = 0; index < _chords.Count; index++)
        {
            var chord = _chords[index];
            var contains = false;
            var down = true;
            for (var keyIndex = 0; keyIndex < chord.Keys.Count; keyIndex++)
            {
                var chordKey = chord.Keys[keyIndex];
                contains |= chordKey == key.VirtualKey;
                down &= chordKey < 256 && _keys[chordKey];
            }

            if (!contains)
            {
                continue;
            }

            if (down == _pressed[index])
            {
                continue;
            }

            var wasHeld = false;
            for (var other = 0; other < _chords.Count; other++)
            {
                wasHeld |= _chords[other].Id == chord.Id && _pressed[other];
            }

            _pressed[index] = down;
            var nowHeld = false;
            for (var other = 0; other < _chords.Count; other++)
            {
                nowHeld |= _chords[other].Id == chord.Id && _pressed[other];
            }

            if (wasHeld == nowHeld)
            {
                continue;
            }

            _pump.Write((index, nowHeld));
        }

        // HC observes these manufacturer chords with silenced:false. Passing both edges also
        // prevents swallowing a release whose initial Windows-key press reached the operating system.
        return false;
    }

    private void ReleaseHeld()
    {
        HashSet<string> released = [];
        for (var index = 0; index < _chords.Count; index++)
        {
            if (!_pressed[index])
            {
                continue;
            }

            _pressed[index] = false;
            if (released.Add(_chords[index].Id))
            {
                _pump.Write((index, false));
            }
        }
    }

    private void Reset()
    {
        Array.Clear(_keys);
        Array.Clear(_pressed);
    }
}
