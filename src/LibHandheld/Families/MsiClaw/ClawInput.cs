using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

internal static class ClawControllerCodec
{
    public static CanonicalControllerSample Decode(
        ReadOnlySpan<byte> report,
        DateTimeOffset timestamp,
        OemButtonLatch? oemButtons = null)
    {
        if (report.Length != 64 || report[0] != 0x01)
        {
            throw new ArgumentException("The Claw DirectInput source requires a 64-byte report 0x01.", nameof(report));
        }

        var buttons = CanonicalButtons.None;
        buttons |= IsSet(report[5], 4) ? CanonicalButtons.X : 0;
        buttons |= IsSet(report[5], 5) ? CanonicalButtons.A : 0;
        buttons |= IsSet(report[5], 6) ? CanonicalButtons.B : 0;
        buttons |= IsSet(report[5], 7) ? CanonicalButtons.Y : 0;
        buttons |= IsSet(report[6], 0) ? CanonicalButtons.LeftShoulder : 0;
        buttons |= IsSet(report[6], 1) ? CanonicalButtons.RightShoulder : 0;
        buttons |= IsSet(report[6], 4) ? CanonicalButtons.View : 0;
        buttons |= IsSet(report[6], 5) ? CanonicalButtons.Menu : 0;
        buttons |= IsSet(report[6], 6) ? CanonicalButtons.LeftStick : 0;
        buttons |= IsSet(report[6], 7) ? CanonicalButtons.RightStick : 0;
        // Measured on MS-1T52: byte 7 bit 4 is the LEFT paddle (M1), and bit 3 is
        // the RIGHT paddle (M2). Handheld Companion has these two assignments reversed.
        buttons |= IsSet(report[7], 4) ? CanonicalButtons.RearPaddle1 : 0;
        buttons |= IsSet(report[7], 3) ? CanonicalButtons.RearPaddle2 : 0;
        buttons |= HidUsageReader.Hat(report[5] & 0x0F);

        // The two front OEM buttons are not in this report (the firmware delivers them as WMI
        // events), so they are merged in from the latch that receives those events.
        buttons |= oemButtons?.Current(timestamp) ?? CanonicalButtons.None;

        return new CanonicalControllerSample
        {
            Timestamp = timestamp,
            Buttons = buttons,
            LeftStickX = Axis(report[1]),
            LeftStickY = -Axis(report[2]),
            RightStickX = Axis(report[3]),
            RightStickY = -Axis(report[4]),
            LeftTrigger = report[8] / 255f,
            RightTrigger = report[9] / 255f
        };
    }

    public static void WriteRumbleReport(Span<byte> report, byte weak, byte strong)
    {
        if (report.Length < 11)
        {
            throw new ArgumentException("The Claw rumble payload needs at least 11 bytes.", nameof(report));
        }

        report.Clear();
        report[0] = 0x05;
        report[1] = 0x01;
        report[4] = weak;
        report[5] = strong;
    }

    private static bool IsSet(byte value, int bit)
    {
        return (value & (1 << bit)) != 0;
    }

    private static float Axis(byte value)
    {
        return Math.Clamp((value - 128) / 127f, -1, 1);
    }
}

/// <summary>The QS press a firmware chord stands for, per HC's <c>OEMChords</c>.</summary>
internal enum FirmwareChord
{
    None,

    /// <summary><c>LWin+G</c>, HC's "QS" chord.</summary>
    QuickSettings,

    /// <summary><c>LWin+Tab</c>, HC's "QS, Long-press" chord.</summary>
    QuickSettingsLong
}

internal readonly record struct ChordDecision(
    bool Suppress,
    bool ReleaseLeftWindows,
    bool ReleaseRightWindows,
    FirmwareChord Chord = FirmwareChord.None);

internal sealed class FirmwareChordStateMachine
{
    private bool _altDown;
    private bool _controlDown;
    private bool _gDown;
    private bool _gSuppressed;
    private bool _leftWindowsDown;
    private bool _leftWindowsReleased;
    private bool _pendingGSuppression;
    private bool _pendingTabSuppression;
    private bool _rightWindowsDown;
    private bool _rightWindowsReleased;
    private bool _shiftDown;
    private bool _tabDown;
    private bool _tabSuppressed;

    public ChordDecision Observe(uint virtualKey, bool keyDown, bool injected)
    {
        if (injected)
        {
            return default;
        }

        switch (virtualKey)
        {
            case NativeKeyboard.VK_LWIN:
                return ObserveWindows(true, keyDown);
            case NativeKeyboard.VK_RWIN:
                return ObserveWindows(false, keyDown);
            case NativeKeyboard.VK_CONTROL:
            case NativeKeyboard.VK_LCONTROL:
            case NativeKeyboard.VK_RCONTROL:
                _controlDown = keyDown;
                return default;
            case NativeKeyboard.VK_MENU:
            case NativeKeyboard.VK_LMENU:
            case NativeKeyboard.VK_RMENU:
                _altDown = keyDown;
                return default;
            case NativeKeyboard.VK_SHIFT:
            case NativeKeyboard.VK_LSHIFT:
            case NativeKeyboard.VK_RSHIFT:
                _shiftDown = keyDown;
                return default;
            case NativeKeyboard.VK_G:
                return ObserveChordKey(ref _gDown, ref _gSuppressed, ref _pendingGSuppression, keyDown,
                    FirmwareChord.QuickSettings);
            case NativeKeyboard.VK_TAB:
                return ObserveChordKey(ref _tabDown, ref _tabSuppressed, ref _pendingTabSuppression, keyDown,
                    FirmwareChord.QuickSettingsLong);
            default:
                return default;
        }
    }

    public void CommitSyntheticReleases(bool leftAccepted, bool rightAccepted)
    {
        _leftWindowsReleased |= leftAccepted;
        _rightWindowsReleased |= rightAccepted;
        if (_pendingGSuppression)
        {
            _gSuppressed = leftAccepted || rightAccepted;
            _pendingGSuppression = false;
        }

        if (_pendingTabSuppression)
        {
            _tabSuppressed = leftAccepted || rightAccepted;
            _pendingTabSuppression = false;
        }
    }

    public void SynchronizeModifiers(bool controlDown, bool altDown, bool shiftDown)
    {
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
    }

    public void Reset()
    {
        _leftWindowsDown = false;
        _rightWindowsDown = false;
        _leftWindowsReleased = false;
        _rightWindowsReleased = false;
        _controlDown = false;
        _altDown = false;
        _shiftDown = false;
        _gDown = false;
        _gSuppressed = false;
        _pendingGSuppression = false;
        _tabDown = false;
        _tabSuppressed = false;
        _pendingTabSuppression = false;
    }

    public void InitializePreexisting(
        bool leftWindowsDown,
        bool rightWindowsDown,
        bool controlDown,
        bool altDown,
        bool shiftDown,
        bool gDown,
        bool tabDown)
    {
        Reset();
        _leftWindowsDown = leftWindowsDown;
        _rightWindowsDown = rightWindowsDown;
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
        _gDown = gDown;
        _tabDown = tabDown;
    }

    private ChordDecision ObserveWindows(bool left, bool keyDown)
    {
        if (left)
        {
            if (!keyDown && _leftWindowsReleased)
            {
                _leftWindowsReleased = false;
                _leftWindowsDown = false;
                return new ChordDecision(true, false, false);
            }

            _leftWindowsDown = keyDown;
        }
        else
        {
            if (!keyDown && _rightWindowsReleased)
            {
                _rightWindowsReleased = false;
                _rightWindowsDown = false;
                return new ChordDecision(true, false, false);
            }

            _rightWindowsDown = keyDown;
        }

        return default;
    }

    /// <summary>
    ///     HC's silenced <c>LWin+G</c> and <c>LWin+Tab</c> chords: the firmware sends them for the QS
    ///     button (short and long), and HC swallows both and raises QS. The key down is intercepted
    ///     before Windows opens Game Bar or Task View; the hook cannot tell the OEM button from an
    ///     ordinary keyboard chord, and neither can HC's.
    /// </summary>
    private ChordDecision ObserveChordKey(
        ref bool down,
        ref bool suppressed,
        ref bool pending,
        bool keyDown,
        FirmwareChord chord)
    {
        if (suppressed)
        {
            down = keyDown;
            suppressed = keyDown;
            return new ChordDecision(true, false, false);
        }

        if (!keyDown || down || (!_leftWindowsDown && !_rightWindowsDown)
            || (chord is FirmwareChord.QuickSettingsLong && (_controlDown || _altDown || _shiftDown)))
        {
            return ObserveTarget(ref down, keyDown);
        }

        down = true;
        pending = (_leftWindowsDown && !_leftWindowsReleased)
                  || (_rightWindowsDown && !_rightWindowsReleased);
        suppressed = !pending;
        return new ChordDecision(
            true,
            _leftWindowsDown && !_leftWindowsReleased,
            _rightWindowsDown && !_rightWindowsReleased,
            chord);
    }

    private ChordDecision ObserveTarget(ref bool targetDown, bool keyDown)
    {
        if (keyDown)
        {
            targetDown = true;
            return default;
        }

        var hadDown = targetDown;
        targetDown = false;
        if (hadDown
            || (!_leftWindowsDown && !_rightWindowsDown)
            || _controlDown
            || _altDown
            || _shiftDown)
        {
            return default;
        }

        return new ChordDecision(
            true,
            _leftWindowsDown && !_leftWindowsReleased,
            _rightWindowsDown && !_rightWindowsReleased);
    }
}

internal sealed class FirmwareChordSuppressor : IFirmwareChordSuppressor
{
    private const uint Marker = 0x5753474D;
    private static readonly int InputSize = Marshal.SizeOf<NativeKeyboard.Input>();
    private readonly NativeKeyboard.Input[] _batch = new NativeKeyboard.Input[4];
    private readonly NativeKeyboard.Input[] _cleanup = new NativeKeyboard.Input[1];
    private readonly KeyboardHookHandler _handler;
    private readonly LowLevelHook _hook = new(13, "LibHandheld Claw firmware chords");
    private readonly FirmwareChordStateMachine _state = new();
    private Action<FirmwareChord>? _chord;

    public FirmwareChordSuppressor()
    {
        _handler = Decide;
    }

    /// <inheritdoc />
    public ValueTask<bool> StartAsync(
        Action<Exception> fault,
        Action<FirmwareChord> chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(chord);
        _chord = chord;
        return _hook.StartAsync(_handler, fault, cancellationToken,
            () => _state.InitializePreexisting(
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_LWIN),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_RWIN),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_CONTROL),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_MENU),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_SHIFT),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_G),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_TAB)),
            _state.Reset);
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return _hook.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _hook.DisposeAsync();
    }

    /// <summary>Decides one key event inside the hook: swallow the firmware's chords, pass everything else.</summary>
    private bool Decide(in KeyboardHookEvent key)
    {
        var injected = key.Injected || key.ExtraInfo == Marker;
        if (!injected && !key.Down && key.VirtualKey is NativeKeyboard.VK_G or NativeKeyboard.VK_TAB)
        {
            _state.SynchronizeModifiers(
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_CONTROL),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_MENU),
                LowLevelHook.IsKeyDown(NativeKeyboard.VK_SHIFT));
        }

        var decision = _state.Observe(key.VirtualKey, key.Down, injected);
        if (!decision.Suppress)
        {
            return false;
        }

        if (decision.Chord is not FirmwareChord.None)
        {
            // The receiver only queues the press; nothing here waits, allocates much or logs.
            _chord?.Invoke(decision.Chord);
        }

        if (decision is { ReleaseLeftWindows: false, ReleaseRightWindows: false })
        {
            return true;
        }

        var count = 0;
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, false, Marker);
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, true, Marker);
        var leftIndex = -1;
        var rightIndex = -1;
        if (decision.ReleaseLeftWindows)
        {
            leftIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, true, Marker);
        }

        if (decision.ReleaseRightWindows)
        {
            rightIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_RWIN, true, Marker);
        }

        var sent = NativeKeyboard.SendInput(checked((uint)count), _batch, InputSize);
        if (sent == 1)
        {
            _cleanup[0] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, true, Marker);
            _ = NativeKeyboard.SendInput(1, _cleanup, InputSize);
        }

        var leftReleased = leftIndex >= 0 && sent > leftIndex;
        var rightReleased = rightIndex >= 0 && sent > rightIndex;
        _state.CommitSyntheticReleases(leftReleased, rightReleased);
        return leftReleased || rightReleased;
    }
}

internal static partial class NativeKeyboard
{
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint INPUT_KEYBOARD = 1;
    public const uint VK_TAB = 0x09;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_LSHIFT = 0xA0;
    public const uint VK_RSHIFT = 0xA1;
    public const uint VK_LCONTROL = 0xA2;
    public const uint VK_RCONTROL = 0xA3;
    public const uint VK_LMENU = 0xA4;
    public const uint VK_RMENU = 0xA5;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_G = 0x47;
    public const uint VK_DUMMY = 0xFF;

    public static Input KeyInput(uint virtualKey, bool keyUp, nuint extraInfo)
    {
        return new Input
        {
            Type = INPUT_KEYBOARD,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = checked((ushort)virtualKey),
                    Flags = (keyUp ? KEYEVENTF_KEYUP : 0)
                            | (virtualKey is VK_LWIN or VK_RWIN ? KEYEVENTF_EXTENDEDKEY : 0),
                    ExtraInfo = extraInfo
                }
            }
        };
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // INPUT reserves the full mouse-sized union even for keyboard events: 32 bytes on x64.
    // A keyboard-only union makes cbSize 32 instead of 40, so SendInput rejects every release.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }
}
