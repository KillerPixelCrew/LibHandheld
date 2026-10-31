using System.Threading.Channels;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>The transport an OEM button edge arrived on.</summary>
internal enum AllyOemSource
{
    /// <summary>A 0x5A report on the ASUS vendor collection.</summary>
    Vendor,

    /// <summary>An F-key on the ASUS keyboard collection.</summary>
    Keyboard
}

/// <summary>Buttons that reach the controller sample from outside the gamepad report.</summary>
/// <remarks>
///     The front OEM buttons arrive as vendor events with no release (HC releases them after its
///     <c>KeyPressDelay</c>, <c>ROGAlly.cs:485-505</c>), and the rear buttons and the Xbox models' front
///     buttons as keyboard keys with real edges. An event is latched by the SDK's
///     <see cref="OemButtonLatch" /> for HC's key press delay; a key is held for as long as it is down.
///     The same physical button may report on both transports, so <see cref="Admit" /> passes only the
///     first report of each press.
/// </remarks>
internal sealed class AllyOemButtonState
{
    private readonly Dictionary<string, ControlEdges> _controls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly OemButtonLatch _latch = new();
    private CanonicalButtons _heldByKeyboard;
    private CanonicalButtons _heldByVendor;

    /// <summary>Decides whether one edge is a new report of its control, not the other transport's echo.</summary>
    /// <param name="controlId">The OEM control the edge belongs to.</param>
    /// <param name="source">The transport it arrived on.</param>
    /// <param name="edge">Press or release.</param>
    /// <param name="releases">Whether this source sends a release for its presses.</param>
    /// <param name="now">When it arrived.</param>
    /// <returns>True when the edge should change button state and be published.</returns>
    public bool Admit(string controlId, AllyOemSource source, OemControlEdge edge, bool releases, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_controls.TryGetValue(controlId, out var control))
            {
                control = new ControlEdges();
                _controls.Add(controlId, control);
            }

            var index = (int)source;
            if (edge is OemControlEdge.Released)
            {
                var admitted = control.Admitted[index];
                control.Down[index] = false;
                control.Admitted[index] = false;
                return admitted;
            }

            var other = 1 - index;
            var echo = control.Down[other]
                       || (control.LastPressSource == other && now - control.LastPress < OemButtonLatch.HoldDuration);
            control.Down[index] = releases;
            control.Admitted[index] = releases && !echo;
            if (echo)
            {
                return false;
            }

            control.LastPress = now;
            control.LastPressSource = index;
            return true;
        }
    }

    /// <summary>Forgets one source's outstanding presses of the given controls, when it stops reporting them.</summary>
    /// <param name="source">Input source whose per-control down/admission state is cleared.</param>
    /// <param name="controlIds">Controls to forget; unknown IDs are ignored and other sources retain their state.</param>
    public void Forget(AllyOemSource source, params ReadOnlySpan<string> controlIds)
    {
        lock (_gate)
        {
            foreach (var controlId in controlIds)
            {
                if (_controls.TryGetValue(controlId, out var control))
                {
                    control.Down[(int)source] = false;
                    control.Admitted[(int)source] = false;
                }
            }
        }
    }

    public void Latch(CanonicalButtons button, DateTimeOffset now)
    {
        _latch.Press(button, now);
    }

    /// <summary>Holds or releases buttons for one source; a button is down while either source holds it.</summary>
    /// <param name="source">Vendor or keyboard source contributing the hold.</param>
    /// <param name="button">Canonical button bitmask to update for this source.</param>
    /// <param name="down">True adds the hold; false removes only this source's hold.</param>
    public void Hold(AllyOemSource source, CanonicalButtons button, bool down)
    {
        lock (_gate)
        {
            if (source is AllyOemSource.Vendor)
            {
                _heldByVendor = down ? _heldByVendor | button : _heldByVendor & ~button;
            }
            else
            {
                _heldByKeyboard = down ? _heldByKeyboard | button : _heldByKeyboard & ~button;
            }
        }
    }

    /// <summary>Releases buttons whichever source holds them.</summary>
    /// <param name="button">Canonical button bitmask to clear from both vendor and keyboard holds.</param>
    public void Release(CanonicalButtons button)
    {
        lock (_gate)
        {
            _heldByVendor &= ~button;
            _heldByKeyboard &= ~button;
        }
    }

    public void ClearSource(AllyOemSource source)
    {
        lock (_gate)
        {
            if (source is AllyOemSource.Keyboard)
            {
                _heldByKeyboard = CanonicalButtons.None;
            }
            else
            {
                _heldByVendor = CanonicalButtons.None;
            }

            foreach (var control in _controls.Values)
            {
                control.Down[(int)source] = false;
                control.Admitted[(int)source] = false;
                if (control.LastPressSource == (int)source)
                {
                    control.LastPressSource = -1;
                    control.LastPress = default;
                }
            }
        }
    }

    public CanonicalButtons Current(DateTimeOffset now)
    {
        CanonicalButtons held;
        lock (_gate)
        {
            held = _heldByVendor | _heldByKeyboard;
        }

        return held | _latch.Current(now);
    }

    private sealed class ControlEdges
    {
        public readonly bool[] Admitted = new bool[2];
        public readonly bool[] Down = new bool[2];
        public DateTimeOffset LastPress;
        public int LastPressSource = -1;
    }
}

/// <summary>The XInput slot this cycle reads, and the device nodes WSGM must hide while it does.</summary>
/// <remarks>
///     HC reads every Ally, the Xbox models included, through XInput (
///     <c>XboxAdaptiveController : XInputController</c>).
/// </remarks>
/// <param name="XInputSlot">Discovered ASUS XInput slot used for sampling and rumble.</param>
/// <param name="PhysicalDevices">Present controller device nodes to hide while the virtual pad is driven.</param>
/// <param name="Observed">Diagnostic description of the slot and device-node identities.</param>
internal sealed record AllyControllerTopology(
    int XInputSlot,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices,
    string Observed);

internal interface IAllyControllerSource : IAsyncDisposable
{
    ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(
        AllyControllerTopology topology,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken);
}

/// <summary>Reads the Ally pad through XInput, as HC does.</summary>
internal sealed class WindowsAllyControllerSource : IAllyControllerSource
{
    private readonly AllyOemButtonState _oem;
    private readonly StandardControllerSource _source;

    internal WindowsAllyControllerSource(AllyModel model, AllyOemButtonState oem)
    {
        _source = new StandardControllerSource(new StandardControllerProfile(model.ControllerProductIds
            .Select(product => new ControllerUsbId(AllyModels.AsusVendorId, product)).ToArray()));
        _oem = oem;
    }

    public ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var topology = _source.Discover();
        return ValueTask.FromResult(topology is null
            ? null
            : new AllyControllerTopology(topology.Slot,
                topology.Devices, $"xinput {topology.Slot}; {topology.Devices.Count} physical interfaces"));
    }

    public ValueTask StartAsync(AllyControllerTopology topology,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _source.Start(new StandardControllerTopology(topology.XInputSlot, topology.PhysicalDevices),
            () => _oem.Current(DateTimeOffset.UtcNow), () => null, publish, fault);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken token)
    {
        return _source.StopAsync(token);
    }

    public ValueTask WriteRumbleAsync(float low, float high, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _source.Rumble(low, high);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return _source.DisposeAsync();
    }
}

/// <summary>Raw keyboard events the hook claimed.</summary>
/// <param name="VirtualKey">Claimed Windows virtual-key code.</param>
/// <param name="Down">True for key down, false for release.</param>
/// <param name="Timestamp">UTC observation time recorded by the hook.</param>
internal readonly record struct AllyKeyEvent(uint VirtualKey, bool Down, DateTimeOffset Timestamp);

internal interface IAllyKeyboardSource : IAsyncDisposable
{
    /// <summary>Installs the hook. Only keys in the watched set are consumed and reported.</summary>
    /// <param name="callback">Sequential event-pump callback outside the native hook; processes claimed key events.</param>
    /// <param name="fault">Hook/pump failure callback, including event-queue overflow.</param>
    /// <param name="cancellationToken">Cancels hook installation, not an already-running stream.</param>
    /// <returns>Whether the hook started; the caller must stop it when the service releases ownership.</returns>
    ValueTask<bool> StartAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken);

    /// <summary>Replaces the set of consumed keys. Keys outside it pass through untouched.</summary>
    /// <param name="virtualKeys">
    ///     Complete replacement set of virtual-key codes; the Windows implementation ignores values
    ///     outside 0-255.
    /// </param>
    void Watch(IReadOnlyCollection<uint> virtualKeys);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>A low-level keyboard hook that claims the F-keys the Ally firmware sends for OEM buttons.</summary>
/// <remarks>
///     HHD grabs the ASUS keyboard evdev for F17/F18 (<c>rog_ally/base.py:396-403</c>); Windows opens
///     keyboards exclusively, so a hook is the Windows equivalent, as HC's keyboard chords are. The hook
///     cannot tell the ASUS keyboard from another one; F17, F18, F21 and F22 are claimed because no
///     ordinary keyboard sends them. Injected input always passes. Inside the hook a claimed key only
///     goes into a bounded channel, which keeps the presses in order for the service.
/// </remarks>
internal sealed class WindowsAllyKeyboardHook : IAllyKeyboardSource
{
    private readonly Channel<AllyKeyEvent> _events = Channel.CreateBounded<AllyKeyEvent>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

    private readonly Lock _gate = new();

    private readonly KeyboardHookHandler _handler;
    private readonly LowLevelHook _hook = new(13, "LibHandheld Ally OEM keyboard");
    private CancellationTokenSource? _cancellation;
    private Action<Exception>? _fault;
    private Task? _pump;
    private long _pumpGeneration;
    private int _queueFault;
    private bool[] _watched = new bool[256];

    public WindowsAllyKeyboardHook()
    {
        _handler = Claim;
    }

    public void Watch(IReadOnlyCollection<uint> virtualKeys)
    {
        var watched = new bool[256];
        foreach (var key in virtualKeys.Where(key => key < 256))
        {
            watched[key] = true;
        }

        Volatile.Write(ref _watched, watched);
    }

    public async ValueTask<bool> StartAsync(
        Func<AllyKeyEvent, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(fault);
        lock (_gate)
        {
            if (_pump?.IsCompleted == true)
            {
                _pump = null;
                _cancellation?.Dispose();
                _cancellation = null;
                while (_events.Reader.TryRead(out _))
                {
                }
            }

            if (_pump is not null && _cancellation?.IsCancellationRequested == true)
            {
                throw new InvalidOperationException("The previous Ally keyboard pump has not finished stopping.");
            }

            if (_pump is null)
            {
                _fault = fault;
                Interlocked.Increment(ref _pumpGeneration);
                Volatile.Write(ref _queueFault, 0);
                _cancellation = new CancellationTokenSource();
                _pump = PumpAsync(callback, fault, _cancellation.Token);
            }
        }

        return await _hook.StartAsync(_handler, fault, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _hook.StopAsync(cancellationToken).ConfigureAwait(false);
        Task? pump;
        lock (_gate)
        {
            pump = _pump;
            _cancellation?.Cancel();
        }

        if (pump is not null)
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _pump = null;
            Interlocked.Increment(ref _pumpGeneration);
            _fault = null;
            _cancellation?.Dispose();
            _cancellation = null;
            while (_events.Reader.TryRead(out _))
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private bool Claim(in KeyboardHookEvent key)
    {
        var watched = Volatile.Read(ref _watched);
        if (key.Injected || key.VirtualKey >= (uint)watched.Length || !watched[key.VirtualKey])
        {
            return false;
        }

        if (_events.Writer.TryWrite(new AllyKeyEvent(key.VirtualKey, key.Down, DateTimeOffset.UtcNow)))
        {
            return true;
        }

        Volatile.Write(ref _watched, Array.Empty<bool>());
        if (Interlocked.Exchange(ref _queueFault, 1) == 0)
        {
            var generation = Interlocked.Read(ref _pumpGeneration);
            var fault = _fault;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (generation != Interlocked.Read(ref _pumpGeneration))
                {
                    return;
                }

                try
                {
                    fault?.Invoke(new IOException(
                        "The Ally keyboard queue filled; its source was withdrawn instead of losing key releases."));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DeviceTrace.Failure("keyboard", "Queue fault reporting failed", ex);
                }
            });
        }

        return false;
    }

    private async Task PumpAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var keyEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await callback(keyEvent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault(ex);
        }
    }
}
