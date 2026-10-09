using System.Runtime.InteropServices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Windows;

/// <summary>One key event as a low-level keyboard hook sees it.</summary>
/// <param name="VirtualKey">The virtual-key code.</param>
/// <param name="Down">True for a key-down or system key-down, false for a key-up.</param>
/// <param name="Injected">Whether the event was synthesized, by SendInput or another injector.</param>
/// <param name="ExtraInfo">The event's extra information, which an injector can use as a marker.</param>
internal readonly record struct KeyboardHookEvent(uint VirtualKey, bool Down, bool Injected, nuint ExtraInfo);

/// <summary>Decides one key event inside the hook.</summary>
/// <param name="key">The event.</param>
/// <returns>True to swallow the event, false to pass it on.</returns>
/// <remarks>Runs on the hook thread inside Windows' input dispatch: no waiting, no I/O, no logging.</remarks>
internal delegate bool KeyboardHookHandler(in KeyboardHookEvent key);

internal readonly record struct MouseHookEvent(nuint Message, uint Data, bool Injected, nuint ExtraInfo);

internal delegate bool MouseHookHandler(in MouseHookEvent mouse);

/// <summary>A keyboard or mouse low-level hook with one owned thread and message loop.</summary>
/// <remarks>
///     Supports suppression of known OEM keyboard chords but cannot identify the physical keyboard
///     that produced an event. Pair it with an attributed OEM source when identity matters. The owned
///     thread installs, pumps and unhooks in its <c>finally</c>. A failed install returns false;
///     an unexpected loop termination invokes the fault callback. A later start can install again.
/// </remarks>
internal sealed partial class LowLevelHook : IAsyncDisposable
{
    private const int KeyboardLowLevel = 13;
    private const uint Quit = 0x0012;
    private const nuint KeyDown = 0x0100;
    private const nuint KeyUp = 0x0101;
    private const nuint SystemKeyDown = 0x0104;
    private const nuint SystemKeyUp = 0x0105;
    private const uint InjectedFlag = 0x10;

    private readonly Lock _gate = new();
    private readonly int _hookId;
    private readonly HookProcedure _procedure;
    private readonly string _threadName;
    private Action<Exception>? _fault;
    private int _faultReported;
    private KeyboardHookHandler? _handler;
    private nint _hook;
    private MouseHookHandler? _mouseHandler;
    private int _stopping;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Creates a hook that is not installed yet.</summary>
    /// <param name="hookId">WH_KEYBOARD_LL (13) or WH_MOUSE_LL (14).</param>
    /// <param name="threadName">The hook thread's name, for diagnostics.</param>
    public LowLevelHook(int hookId, string threadName)
    {
        if (hookId is not (13 or 14))
        {
            throw new ArgumentOutOfRangeException(nameof(hookId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        _hookId = hookId;
        _threadName = threadName;
        _procedure = Callback;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Installs the hook on its own thread.</summary>
    /// <param name="handler">Decides each key event.</param>
    /// <param name="fault">Told when the hook's message loop ends without a stop request.</param>
    /// <param name="cancellationToken">Cancels installation waiting and then requests bounded hook cleanup.</param>
    /// <param name="prepare">Runs on the hook thread just before the install, for reading key state.</param>
    /// <param name="stopped">Runs on the hook thread after it unhooked, however it ended.</param>
    /// <returns>True after installation; false after install failure or while an existing thread has no hook.</returns>
    /// <remarks>An existing thread keeps its original callbacks; another start does not replace them.</remarks>
    /// <exception cref="OperationCanceledException">Waiting was canceled and cleanup completed.</exception>
    /// <exception cref="TimeoutException">Cancellation cleanup could not join the hook thread within one second.</exception>
    public ValueTask<bool> StartAsync(
        KeyboardHookHandler handler,
        Action<Exception> fault,
        CancellationToken cancellationToken,
        Action? prepare = null,
        Action? stopped = null)
    {
        return StartCoreAsync(handler, null, fault, cancellationToken, prepare, stopped);
    }

    internal ValueTask<bool> StartMouseAsync(MouseHookHandler handler, Action<Exception> fault,
        CancellationToken cancellationToken, Action? stopped = null)
    {
        return StartCoreAsync(null, handler, fault, cancellationToken, null, stopped);
    }

    private async ValueTask<bool> StartCoreAsync(KeyboardHookHandler? keyboard, MouseHookHandler? mouse,
        Action<Exception> fault, CancellationToken cancellationToken, Action? prepare, Action? stopped)
    {
        if ((_hookId == KeyboardLowLevel && keyboard is null) || (_hookId == 14 && mouse is null))
        {
            throw new ArgumentException("The handler must match the hook type.");
        }

        ArgumentNullException.ThrowIfNull(fault);
        TaskCompletionSource<bool> started;
        lock (_gate)
        {
            if (_thread is not null)
            {
                return _hook != 0;
            }

            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handler = keyboard;
            _mouseHandler = mouse;
            _fault = fault;
            _faultReported = 0;
            Volatile.Write(ref _stopping, 0);
            _thread = new Thread(() => Run(started, prepare, stopped)) { IsBackground = true, Name = _threadName };
            _thread.Start();
        }

        try
        {
            return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes the hook and ends its thread.</summary>
    /// <param name="cancellationToken">Cancels waiting, not the already requested unhook and thread shutdown.</param>
    /// <returns>Completion after the thread ended, or immediately when no thread exists.</returns>
    /// <exception cref="OperationCanceledException">The caller stopped waiting; shutdown may still be running.</exception>
    /// <exception cref="TimeoutException">The hook thread did not end within a second.</exception>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        uint threadId;
        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
        }

        if (thread is null)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);
        if (threadId != 0)
        {
            _ = PostThreadMessage(threadId, Quit, 0, 0);
        }

        var joined = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), CancellationToken.None)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!joined)
        {
            throw new TimeoutException($"The {_threadName} thread did not stop within one second.");
        }
    }

    /// <summary>Whether a key is down right now, from the asynchronous key state.</summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <returns>True while the key is held.</returns>
    public static bool IsKeyDown(uint virtualKey)
    {
        return (GetAsyncKeyState(checked((int)virtualKey)) & 0x8000) != 0;
    }

    private void Run(TaskCompletionSource<bool> started, Action? prepare, Action? stopped)
    {
        lock (_gate)
        {
            _threadId = GetCurrentThreadId();
        }

        try
        {
            prepare?.Invoke();
            _ = PeekMessage(out _, 0, 0, 0, 0);
            _hook = SetWindowsHookEx(_hookId, _procedure, 0, 0);
            if (_hook == 0)
            {
                started.TrySetResult(false);
                return;
            }

            started.TrySetResult(true);
            var result = 0;
            while (Volatile.Read(ref _stopping) == 0 && (result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }

            if (Volatile.Read(ref _stopping) == 0)
            {
                ReportFault(new InvalidOperationException(result < 0
                    ? $"The {_threadName} message loop failed with Win32 {Marshal.GetLastPInvokeError()}."
                    : $"The {_threadName} thread ended without a stop request."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            started.TrySetResult(false);
            ReportFault(ex);
        }
        finally
        {
            if (_hook != 0)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }

            try
            {
                stopped?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFault(ex);
            }
            finally
            {
                lock (_gate)
                {
                    _thread = null;
                    _threadId = 0;
                    _handler = null;
                    _mouseHandler = null;
                    _fault = null;
                }
            }
        }
    }

    private unsafe nint Callback(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            try
            {
                if (_hookId == KeyboardLowLevel && message is KeyDown or KeyUp or SystemKeyDown or SystemKeyUp
                                                && Volatile.Read(ref _handler) is { } handler)
                {
                    var raw = *(KeyboardHookData*)data;
                    KeyboardHookEvent key = new(raw.VirtualKey, message is KeyDown or SystemKeyDown,
                        (raw.Flags & InjectedFlag) != 0, raw.ExtraInfo);
                    if (handler(in key))
                    {
                        return 1;
                    }
                }
                else if (_hookId == 14 && Volatile.Read(ref _mouseHandler) is { } mouseHandler)
                {
                    var raw = *(MouseHookData*)data;
                    MouseHookEvent mouse = new(message, raw.Data, (raw.Flags & 1) != 0, raw.ExtraInfo);
                    if (mouseHandler(in mouse))
                    {
                        return 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An exception must never cross the native callback; the key passes on and the hook keeps running.
                ReportFault(ex);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private void ReportFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) != 0)
        {
            return;
        }

        try
        {
            Volatile.Read(ref _fault)?.Invoke(exception);
        }
        catch (Exception callback) when (callback is not OutOfMemoryException)
        {
            DeviceTrace.Failure("keyboard", $"{_threadName} fault reporting failed", callback);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, HookProcedure procedure, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int PointX;
        public int PointY;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }
}
