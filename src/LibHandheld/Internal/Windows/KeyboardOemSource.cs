using System.Threading.Channels;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>Recognizes complete OEM chords; ordinary keys are passed on and repeats emit no extra press.</summary>
internal sealed class KeyboardOemSource(IReadOnlyList<KeyboardOemChord> chords) : IProfiledOemSource
{
    private readonly LowLevelHook _hook = new(13, "LibHandheld OEM keyboard");
    private readonly bool[] _keys = new bool[256];
    private readonly bool[] _pressed = new bool[chords.Count];
    private Channel<(int Index, bool Down)>? _events;
    private volatile bool _failed;
    private Task? _pump;
    private volatile bool _running;
    public bool IsConnected => _running && !_failed;

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish,
        CancellationToken cancellationToken)
    {
        if (chords.Count == 0)
        {
            _running = true;
            _failed = false;
            return true;
        }

        _failed = false;

        _events = Channel.CreateUnbounded<(int, bool)>(new UnboundedChannelOptions
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
        try
        {
            _running = await _hook.StartAsync(Handle, exception =>
            {
                _failed = true;
                ReleaseHeld();
                events.Writer.TryComplete(exception);
            }, cancellationToken, stopped: ReleaseHeld).ConfigureAwait(false);
            if (!_running)
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return _running;
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _hook.StopAsync(cancellationToken).ConfigureAwait(false);
        _running = false;
        _events?.Writer.TryComplete();
        try
        {
            if (_pump is not null)
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_pump is null || _pump.IsCompleted)
            {
                _events = null;
                _pump = null;
                Array.Clear(_keys);
                Array.Clear(_pressed);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private bool Handle(in KeyboardHookEvent key)
    {
        if (_failed || key.Injected || key.VirtualKey >= 256)
        {
            return false;
        }

        var prior = _keys[key.VirtualKey];
        if (prior && key.Down)
        {
            return false;
        }

        _keys[key.VirtualKey] = key.Down;
        for (var index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
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

        // HC observes these manufacturer chords with silenced:false. Passing both edges also
        // prevents swallowing a release whose initial Windows-key press reached the operating system.
        return false;
    }

    private void ReleaseHeld()
    {
        HashSet<string> released = [];
        for (var index = 0; index < chords.Count; index++)
        {
            if (!_pressed[index])
            {
                continue;
            }

            _pressed[index] = false;
            if (released.Add(chords[index].Id))
            {
                _events?.Writer.TryWrite((index, false));
            }
        }
    }
}
