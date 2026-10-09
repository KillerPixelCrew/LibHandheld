using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;

namespace LibHandheld.Families.Ayaneo;

/// <summary>Older KUN firmware emits Guide as the left-button + XButton2 mouse chord.</summary>
internal sealed class AyaneoKunMouseSource : IProfiledOemSource
{
    private readonly Hook _callback;
    private Channel<bool>? _edges;
    private nint _hook;
    private bool _left;
    private bool _pressed;
    private Task? _pump;
    private volatile bool _stopRequested;
    private Thread? _thread;
    private uint _threadId;
    private bool _x2;

    internal AyaneoKunMouseSource()
    {
        _callback = Handle;
    }

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _stopRequested = false;
        _edges = Channel.CreateBounded<bool>(new BoundedChannelOptions(32)
            { SingleReader = true, SingleWriter = true });
        var events = _edges;
        _pump = Task.Run(async () =>
        {
            await foreach (var down in events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                publish("guide-mouse", CanonicalButtons.Guide, down);
            }
        }, CancellationToken.None);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "LibHandheld KUN mouse chord" };
        _thread.Start();
        try
        {
            return await started.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        var thread = _thread;
        _stopRequested = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, 0x0012, 0, 0);
        }

        if (thread is not null)
        {
            if (!await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2)), CancellationToken.None).WaitAsync(token)
                    .ConfigureAwait(false))
            {
                throw new TimeoutException("The KUN mouse hook has not stopped.");
            }
        }

        _edges?.Writer.TryComplete();
        if (_pump is not null)
        {
            await _pump.WaitAsync(token).ConfigureAwait(false);
        }

        _thread = null;
        _pump = null;
        _edges = null;
        _left = false;
        _x2 = false;
        _pressed = false;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    private void Run(TaskCompletionSource<bool> started)
    {
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0);
            _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            started.TrySetResult(true);
            if (_stopRequested)
            {
                return;
            }

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            started.TrySetException(exception);
            _edges?.Writer.TryComplete(exception);
        }
        finally
        {
            if (_hook != 0)
            {
                UnhookWindowsHookEx(_hook);
            }

            _hook = 0;
            _threadId = 0;
            _edges?.Writer.TryComplete();
        }
    }

    private unsafe nint Handle(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            var input = *(MouseData*)data;
            if ((input.Flags & 1) == 0)
            {
                switch (message)
                {
                    case 0x0201: _left = true; break;
                    case 0x0202: _left = false; break;
                    case 0x020B when input.Data >> 16 == 2: _x2 = true; break;
                    case 0x020C when input.Data >> 16 == 2: _x2 = false; break;
                }

                var down = _left && _x2;
                var claimed = down || _pressed;
                if (down != _pressed)
                {
                    _pressed = down;
                    if (_edges?.Writer.TryWrite(down) != true)
                    {
                        _edges?.Writer.TryComplete(new IOException("The KUN OEM mouse queue overflowed."));
                    }
                }

                if (claimed)
                {
                    return 1;
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, Hook callback, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public Point Point;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Hook(int code, nuint message, nint data);
}
