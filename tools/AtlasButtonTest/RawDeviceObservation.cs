using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LibHandheld.AtlasButtonTest;

/// <summary>Device-attributed keyboard and consumer input, observed without changing legacy delivery.</summary>
internal sealed class RawDeviceObservation(CaptureBuffer capture) : IAsyncDisposable
{
    private const int StopMessage = 0x8001;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nint, string> _devices = [];
    private readonly byte[] _data = new byte[512];
    private Thread? _thread;
    private nint _window;
    private nint _buffer;

    internal async ValueTask<bool> StartAsync()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Atlas attributed Raw Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private void Run()
    {
        try
        {
            _buffer = Marshal.AllocHGlobal(_data.Length);
            using var window = new InputWindow(this);
            _window = window.Handle;
            RawRegistration[] registrations =
            [
                new() { Page = 1, Usage = 6, Flags = 0x100, Target = _window },
                new() { Page = 0x0C, Usage = 1, Flags = 0x100, Target = _window }
            ];
            if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawRegistration>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw Input registration failed.");
            }

            _ready.TrySetResult(true);
            Application.Run();
            foreach (ref var registration in registrations.AsSpan())
            {
                registration.Flags = 1;
                registration.Target = 0;
            }
            if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawRegistration>()))
            {
                capture.Fault("Raw Input unregister failed.");
            }
        }
        catch (Exception exception)
        {
            capture.Fault($"Device-attributed Raw Input: {exception.Message}");
            _ready.TrySetResult(false);
        }
        finally
        {
            _window = 0;
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
            }
        }
    }

    private void Observe(nint raw)
    {
        uint size = (uint)_data.Length;
        var count = GetRawInputData(raw, command: 0x10000003, _buffer, ref size, headerSize: 24);
        if (count == uint.MaxValue || count < 24 || count > _data.Length)
        {
            capture.Fault("A Raw Input record could not be read within the 512-byte bound.");
            return;
        }

        Marshal.Copy(_buffer, _data, startIndex: 0, (int)count);
        var type = BitConverter.ToUInt32(_data, startIndex: 0);
        var device = (nint)BitConverter.ToInt64(_data, startIndex: 8);
        if (!_devices.TryGetValue(device, out var source))
        {
            uint length = 0;
            GetRawInputDeviceInfo(device, command: 0x20000007, null, ref length);
            if (length is 0 or > 2048)
            {
                source = $"input-{_devices.Count + 1}: unnamed device";
            }
            else
            {
                var name = new StringBuilder((int)length);
                if (GetRawInputDeviceInfo(device, command: 0x20000007, name, ref length) == uint.MaxValue)
                {
                    source = $"input-{_devices.Count + 1}: device name unavailable";
                }
                else
                {
                    // Keep enumerator and hardware ID; omit the instance/serial and interface GUID.
                    var prefix = string.Join('#', name.ToString().Split('#').Take(2));
                    source = $"input-{_devices.Count + 1}: {prefix}";
                }
            }

            _devices[device] = source;
        }

        if (type == 1 && count >= 40)
        {
            var scan = BitConverter.ToUInt16(_data, startIndex: 24);
            var flags = BitConverter.ToUInt16(_data, startIndex: 26);
            var key = BitConverter.ToUInt16(_data, startIndex: 30);
            if (key is 0x09 or 0x10 or 0x11 or 0x12 or 0x47 or 0x5B or 0x5C or 0x85 or 0xAD or 0xAE or 0xAF
                or >= 0xA0 and <= 0xA5)
            {
                capture.DeviceKeyboard(source, key, (flags & 1) == 0, scan, flags);
            }
        }
        else if (type == 2 && count >= 32)
        {
            capture.DeviceConsumer(source, Convert.ToHexString(_data.AsSpan(32, (int)count - 32)));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_window != 0)
        {
            if (!PostMessage(_window, StopMessage, 0, 0))
            {
                capture.Fault("Could not request Raw Input reader shutdown.");
            }
        }
        if (_thread is not null && !_thread.Join(millisecondsTimeout: 5000))
        {
            capture.Fault("Raw Input reader did not stop within five seconds.");
        }

        return ValueTask.CompletedTask;
    }

    private sealed class InputWindow : NativeWindow, IDisposable
    {
        private readonly RawDeviceObservation _owner;

        internal InputWindow(RawDeviceObservation owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "Atlas device input observation", Parent = new nint(-3) });
        }

        protected override void WndProc(ref Message message)
        {
            try
            {
                if (message.Msg == 0xFF)
                {
                    _owner.Observe(message.LParam);
                }
                else if (message.Msg == StopMessage)
                {
                    Application.ExitThread();
                }
            }
            catch (Exception exception)
            {
                _owner._ready.TrySetResult(false);
                _owner.ReportFault(exception.Message);
            }

            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }

    private void ReportFault(string detail) => capture.Fault($"Raw Input message failed: {detail}");

    [StructLayout(LayoutKind.Sequential)]
    private struct RawRegistration
    {
        public ushort Page;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawRegistration[] registrations, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint raw, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
