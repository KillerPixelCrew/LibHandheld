// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Windows;

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

/// <summary>XInput state to canonical sample. XInput's thumbs are already positive up.</summary>
internal static class AllyControllerCodec
{
    public const ushort DPadUp = 0x0001;
    public const ushort DPadDown = 0x0002;
    public const ushort DPadLeft = 0x0004;
    public const ushort DPadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;

    /// <summary>The guide bit only <c>XInputGetStateEx</c> (ordinal 100) reports; HC <c>XInputStateButtons.Xbox</c>.</summary>
    public const ushort Guide = 0x0400;

    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    public static CanonicalButtons Buttons(ushort buttons)
    {
        var result = CanonicalButtons.None;
        result |= (buttons & A) != 0 ? CanonicalButtons.A : 0;
        result |= (buttons & B) != 0 ? CanonicalButtons.B : 0;
        result |= (buttons & X) != 0 ? CanonicalButtons.X : 0;
        result |= (buttons & Y) != 0 ? CanonicalButtons.Y : 0;
        result |= (buttons & LeftShoulder) != 0 ? CanonicalButtons.LeftShoulder : 0;
        result |= (buttons & RightShoulder) != 0 ? CanonicalButtons.RightShoulder : 0;
        result |= (buttons & LeftThumb) != 0 ? CanonicalButtons.LeftStick : 0;
        result |= (buttons & RightThumb) != 0 ? CanonicalButtons.RightStick : 0;
        result |= (buttons & Back) != 0 ? CanonicalButtons.View : 0;
        result |= (buttons & Start) != 0 ? CanonicalButtons.Menu : 0;
        result |= (buttons & DPadUp) != 0 ? CanonicalButtons.DPadUp : 0;
        result |= (buttons & DPadDown) != 0 ? CanonicalButtons.DPadDown : 0;
        result |= (buttons & DPadLeft) != 0 ? CanonicalButtons.DPadLeft : 0;
        result |= (buttons & DPadRight) != 0 ? CanonicalButtons.DPadRight : 0;
        result |= (buttons & Guide) != 0 ? CanonicalButtons.Guide : 0;
        return result;
    }

    public static CanonicalControllerSample Decode(
        in XInputNative.State state,
        CanonicalButtons oem,
        DateTimeOffset timestamp)
    {
        var gamepad = state.Gamepad;
        return new CanonicalControllerSample
        {
            Timestamp = timestamp,
            Buttons = Buttons(gamepad.Buttons) | oem,
            LeftStickX = Axis(gamepad.ThumbLX),
            LeftStickY = Axis(gamepad.ThumbLY),
            RightStickX = Axis(gamepad.ThumbRX),
            RightStickY = Axis(gamepad.ThumbRY),
            LeftTrigger = gamepad.LeftTrigger / 255f,
            RightTrigger = gamepad.RightTrigger / 255f
        };
    }

    public static float Axis(short value)
    {
        return Math.Clamp(value / 32767f, -1f, 1f);
    }
}

/// <summary>Reads the Ally pad through XInput, as HC does.</summary>
/// <param name="model">Exact model supplying permitted controller product IDs.</param>
/// <param name="oem">Shared OEM-button holds and latches to merge into XInput samples.</param>
internal sealed class WindowsAllyControllerSource(AllyModel model, AllyOemButtonState oem) : IAllyControllerSource
{
    /// <summary>About 125 Hz, the Claw's pad cadence, which the motion resampler is tuned against.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);

    private readonly Lock _gate = new();
    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly AllyOemButtonState _oem = oem ?? throw new ArgumentNullException(nameof(oem));
    private CancellationTokenSource? _cancellation;
    private int _slot = -1;
    private Thread? _worker;

    /// <remarks>
    ///     Only a pad that is fully back counts: its XInput slot answers and its device nodes are present to
    ///     hide. After a wake the slot and the nodes return a few seconds apart; until both are there the
    ///     service keeps waiting, as HC waits for the pad's arrival.
    /// </remarks>
    /// <summary>Discovers a usable ASUS XInput slot together with its physical controller nodes.</summary>
    /// <param name="cancellationToken">Cancels admission before synchronous XInput and device-node discovery.</param>
    /// <returns>The usable slot and hideable nodes, or null until both are present; discovery does not write configuration.</returns>
    public ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = FindXInputSlot();
        if (slot < 0)
        {
            return ValueTask.FromResult<AllyControllerTopology?>(null);
        }

        var nodes = AllyControllerNodes.Find(_model.ControllerProductIds);
        IReadOnlyList<PhysicalDeviceIdentity> devices =
        [
            .. nodes.Select(node => new PhysicalDeviceIdentity
            {
                InstancePath = node.InstancePath,
                LocationPath = node.PhysicalLocation,
                VendorId = AllyModels.AsusVendorId.ToString("X4", CultureInfo.InvariantCulture),
                ProductId = ProductOf(node.InstancePath),
                RequiresHiding = true
            })
        ];
        if (devices.Count == 0)
        {
            return ValueTask.FromResult<AllyControllerTopology?>(null);
        }

        var observed = string.Join(", ",
            nodes.Select(node => $"{node.InstancePath} {ClassName(node.ClassGuid)}"));
        return ValueTask.FromResult<AllyControllerTopology?>(
            new AllyControllerTopology(slot, devices, $"xinput {slot}; {observed}"));
    }

    public ValueTask StartAsync(
        AllyControllerTopology topology,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(fault);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_worker is not null)
            {
                throw new InvalidOperationException("The Ally controller reader is already active.");
            }

            _slot = topology.XInputSlot;
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = new Thread(() => Run(topology.XInputSlot, publish, fault, cancellation.Token))
            {
                IsBackground = true,
                Name = "WSGM Ally controller reader"
            };
            _worker.Start();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _cancellation?.Cancel();
        }

        var stopped = worker is null || await Task.Run(() => worker.Join(TimeSpan.FromSeconds(1)),
            CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_worker == worker)
            {
                _worker = null;
                _slot = -1;
                // A reader that has not exited still waits on this token's handle; it is left to the
                // collector rather than disposed under it.
                if (stopped)
                {
                    _cancellation?.Dispose();
                }

                _cancellation = null;
            }
        }

        if (!stopped)
        {
            throw new TimeoutException("The Ally controller reader did not stop within one second.");
        }
    }

    public ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int slot;
        lock (_gate)
        {
            slot = _slot;
        }

        if (slot < 0)
        {
            throw new InvalidOperationException("The Ally controller route is no longer available for rumble.");
        }

        // HC's XInputController.SetVibration: the large (low-frequency) motor is left, the small right
        // (XInputController.cs:279-296). HHD writes an HID output report instead (base.py:226-254),
        // which the RC73XA lab run showed has no output collection to go to; HC is followed.
        var vibration = new XInputNative.Vibration
        {
            LeftMotorSpeed = (ushort)Math.Round(Math.Clamp(low, 0f, 1f) * ushort.MaxValue),
            RightMotorSpeed = (ushort)Math.Round(Math.Clamp(high, 0f, 1f) * ushort.MaxValue)
        };
        var result = XInputNative.XInputSetState((uint)slot, ref vibration);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "XInputSetState did not accept the Ally rumble frame.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private int FindXInputSlot()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            try
            {
                if (XInputNative.XInputGetCapabilitiesEx(1, slot, 0, out var caps) == 0
                    && caps.VendorId == AllyModels.AsusVendorId
                    && _model.ControllerProductIds.Contains(caps.ProductId))
                {
                    return (int)slot;
                }
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
        }

        return -1;
    }

    private void Run(
        int slot,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        try
        {
            // HC polls on an 8 ms precision timer; a plain 8 ms wait sleeps a whole 15.6 ms tick.
            using PrecisionTicker ticker = new(PollInterval, cancellationToken);
            while (ticker.Wait())
            {
                var now = DateTimeOffset.UtcNow;
                var result = XInputNative.XInputGetStateEx((uint)slot, out var state);
                if (result != 0)
                {
                    // One neutral frame first, so nothing held at the moment the pad dropped stays held;
                    // then the service waits for the pad to come back.
                    publish(CanonicalControllerSample.Neutral(now), cancellationToken).AsTask().GetAwaiter()
                        .GetResult();
                    fault(new IOException($"XInput slot {slot} stopped answering ({result})."));
                    return;
                }

                publish(AllyControllerCodec.Decode(state, _oem.Current(now), now), cancellationToken).AsTask()
                    .GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                fault(ex);
            }
            catch (Exception callback) when (callback is not OutOfMemoryException)
            {
                DeviceTrace.Failure("controller", "Controller fault reporting failed", callback);
            }
        }
    }

    private static string? ProductOf(string instancePath)
    {
        var index = instancePath.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + 8 <= instancePath.Length
            ? instancePath.Substring(index + 4, 4).ToUpperInvariant()
            : null;
    }

    private static string ClassName(Guid classGuid)
    {
        return classGuid == HidDevices.XnaCompositeClass ? "xusb"
            : classGuid == HidDevices.XboxCompositeClass ? "gip"
            : "hid";
    }
}

internal static class XInputNative
{
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    public static extern uint XInputGetStateEx(uint userIndex, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    public static extern uint XInputGetCapabilitiesEx(uint reserved, uint userIndex, uint flags,
        out CapabilitiesEx capabilities);

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputSetState(uint userIndex, ref Vibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    /// <summary>HC's <c>XInputCapabilitiesEx</c> (<c>XInputController.cs:53-89</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CapabilitiesEx
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public Gamepad Gamepad;
        public Vibration Vibration;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Revision;
        public uint Xid;
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
    private readonly LowLevelKeyboardHook _hook = new("WSGM Ally OEM keyboard hook");
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
