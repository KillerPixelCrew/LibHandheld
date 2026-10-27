using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LibHandheld;
using LibHandheld.Contracts;
using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.AtlasButtonTest;

internal static class Program
{
    private static readonly string[] Steps =
    [
        "Neutral: leave every controller button untouched for three seconds",
        "A: press and release three times",
        "B: press and release three times",
        "X: press and release three times",
        "Y: press and release three times",
        "D-pad UP: press and release three times",
        "D-pad RIGHT: press and release three times",
        "D-pad DOWN: press and release three times",
        "D-pad LEFT: press and release three times",
        "D-pad UP + RIGHT: press the diagonal and release three times",
        "Left shoulder / LB: press and release three times",
        "Right shoulder / RB: press and release three times",
        "Left trigger / LT: slowly squeeze fully and release three times",
        "Right trigger / RT: slowly squeeze fully and release three times",
        "Left stick CLICK / L3: press down and release three times",
        "Right stick CLICK / R3: press down and release three times",
        "View / Back / Select: press and release three times",
        "Menu / Start: press and release three times",
        "Front LEFT extra button: press and release three times",
        "Front RIGHT extra button: press and release three times",
        "Rear LEFT button: press and release three times",
        "Rear RIGHT button: press and release three times",
        "Both REAR buttons together: hold one second, release; repeat three times",
        "Left stick: move UP, RIGHT, DOWN, LEFT to full travel, then release",
        "Right stick: move UP, RIGHT, DOWN, LEFT to full travel, then release",
        "Rollover: hold A + B + LB together for one second, then release",
        "Final neutral: release everything and wait three seconds"
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--help"))
        {
            Console.WriteLine("AtlasButtonTest [--out <directory>] [--rear-test | --front-hold-test] [--self-check]");
            Console.WriteLine("Interactive read-only Atlas 8 button capture. Enter starts/ends each step; S skips; Q saves and exits.");
            Console.WriteLine("--rear-test temporarily acquires the native rear-button mode, tests three rear actions, then restores it.");
            Console.WriteLine("--front-hold-test attributes the two OEM holds and volume up/down to Raw Input devices (four steps).");
            return 0;
        }

        if (args.Contains("--self-check"))
        {
            return CaptureBuffer.SelfCheck();
        }

        var outputIndex = Array.IndexOf(args, "--out");
        var rearTest = args.Contains("--rear-test");
        var frontHoldTest = args.Contains("--front-hold-test");
        if (rearTest && frontHoldTest
            || args.Any(arg => arg is not ("--out" or "--rear-test" or "--front-hold-test") && (outputIndex < 0 || arg != args.ElementAtOrDefault(outputIndex + 1)))
            || outputIndex >= 0 && outputIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: AtlasButtonTest [--out <directory>] [--rear-test]");
            return 64;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Run in an interactive terminal. Hardware input cannot be tested through redirected stdin.");
            return 64;
        }

        var output = Path.GetFullPath(outputIndex >= 0 ? args[outputIndex + 1] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AtlasButtonTest-results"));
        var report = new TestReport();
        var cancelled = false;
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Volatile.Write(ref cancelled, true);
        };
        Console.CancelKeyPress += cancel;
        using var owner = new Mutex(initiallyOwned: false, "Global\\WSGM.DeviceOwner");
        var owned = false;
        try
        {
            try
            {
                owned = owner.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                throw new InvalidOperationException("WSGM or another hardware test owns this device. Exit that session first.");
            }

            var identity = HandheldDevice.ReadIdentity();
            report.Identity = new
            {
                identity.SystemManufacturer, identity.SystemProduct, identity.BaseboardManufacturer,
                identity.BaseboardProduct, identity.BiosVersion, identity.EcFirmwareVersion
            };
            if (HandheldDevice.Detect(identity)?.Id != "acer-predator-atlas-8-pa08-i51")
            {
                throw new InvalidOperationException("This tool only admits the exact Acer Atlas 8 PA08-I51.");
            }

            var capture = new CaptureBuffer();
            var collections = HidDevices.Enumerate(0x0502, [0xF901]).ToArray();
            var vendor = HidDevices.Unique(collections, AcerInputProtocol.IsVendorCollection)
                         ?? throw new IOException("The exact vendor button collection is absent or ambiguous.");
            var qam = HidDevices.Unique(collections, AcerInputProtocol.IsQamCollection)
                      ?? throw new IOException("The exact QAM collection is absent or ambiguous.");
            if (!HidDevices.SamePhysicalLocation(vendor.PhysicalLocation, qam.PhysicalLocation))
            {
                throw new IOException("The two HID lanes belong to different physical controllers.");
            }

            report.Collections = [Describe(vendor), Describe(qam)];
            using var decoded = new AcerButtonState(capture.Oem);
            var vendorSource = new HidReportSource();
            var qamSource = new HidReportSource();
            var controller = new StandardControllerSource(new StandardControllerProfile([new ControllerUsbId(0x0502, 0xF901)]));
            ShortcutObservation? keyboard = null;
            RawDeviceObservation? rawInput = null;
            AcerControllerModeService? modeService = null;
            DeviceCycleContext<ProfiledCycleIdentity>? modeCycle = null;
            try
            {
                if (frontHoldTest)
                {
                    rawInput = new RawDeviceObservation(capture);
                    report.RawInputAvailable = rawInput.StartAsync().AsTask().GetAwaiter().GetResult();
                    keyboard = new ShortcutObservation(capture);
                    report.KeyboardLongPressAvailable = keyboard.StartAsync().AsTask().GetAwaiter().GetResult();
                    if (report.KeyboardLongPressAvailable != true)
                    {
                        capture.Fault("Keyboard shortcut observation could not start; HID capture remains available.");
                    }
                }

                if (rearTest)
                {
                    var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LibHandheld", "AtlasButtonTest", "recovery");
                    var context = new ProfiledFeatureContext(statePath,
                        $"acer-predator-atlas-8-pa08-i51|{identity.BiosVersion}|{identity.EcFirmwareVersion}|controller:{vendor.ReleaseNumber:X4}", null!)
                    {
                        Journal = DeviceRecoveryJournal.OpenAsync(statePath, CancellationToken.None).AsTask().GetAwaiter().GetResult()
                    };
                    modeCycle = new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new(context.Firmware));
                    modeService = new AcerControllerModeService(context);
                    var acquired = modeService.AcquireAsync(modeCycle.Value, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    report.ModeAcquisition = acquired.State.ToString();
                    if (acquired.State != DeviceServiceState.Owned)
                    {
                        throw new IOException($"Native rear-button mode could not be acquired: {acquired.Reason}");
                    }
                }

                if (!vendorSource.Start(HidDevices.OpenStream(vendor), vendor.InputLength, bytes =>
                    {
                        capture.Raw(lane: 1, bytes);
                        decoded.HandleVendorReport(bytes);
                    }, decoded.ReleaseVendor, CancellationToken.None)
                    || !qamSource.Start(HidDevices.OpenStream(qam), qam.InputLength, bytes =>
                    {
                        capture.Raw(lane: 2, bytes);
                        decoded.HandleQamReport(bytes);
                    }, decoded.ReleaseQam, CancellationToken.None))
                {
                    throw new IOException("A button reader could not start.");
                }

                var topology = controller.Discover();
                report.ControllerAvailable = topology is not null;
                if (topology is not null)
                {
                    controller.Start(topology, () => CanonicalButtons.None, () => null,
                        (sample, _) =>
                        {
                            capture.Controller(sample);
                            return ValueTask.CompletedTask;
                        }, exception => capture.Fault(exception.Message));
                }

                Console.WriteLine("ACER ATLAS 8 BUTTON TEST\n");
                Console.WriteLine(rearTest
                    ? "Temporary native rear-button mode acquired. The original mode will be restored when this tool exits."
                    : "Read-only: no firmware, lighting, rumble, power or controller-mode writes.");
                Console.WriteLine("Use the KEYBOARD for Enter/S/Q. Keep the console focused; an OEM button may open Acer's app.");
                Console.WriteLine("Close any game first. Existing Acer services stay running.");
                Console.WriteLine("Each step: Enter to start, perform ONLY that action, release everything, Enter to finish.");
                Console.WriteLine("S skips an unavailable control; Q or Ctrl+C saves the partial result. Each step times out after 60 seconds.");
                Console.WriteLine($"Controller lane: {(topology is null ? "UNAVAILABLE; raw OEM capture still works" : "available")}");
                Console.WriteLine($"Results: {output}\n");
                if (frontHoldTest)
                {
                    Console.WriteLine("Use the two EXTRA OEM buttons, not the ordinary View/Menu buttons.");
                    Console.WriteLine("The right one is Predator / QAM, the left one Guide / Task.");
                    Console.WriteLine("Capturing device-attributed keyboard/volume reports and shortcut keys. Nothing is blocked.");
                }
                var deadline = DateTime.UtcNow.AddMinutes(30);
                var steps = frontHoldTest ? new[]
                {
                    "Front LEFT Guide / Task extra: hold THREE seconds, release, wait TWO seconds",
                    "Front RIGHT Predator / QAM extra: hold THREE seconds, release, wait TWO seconds",
                    "Built-in VOLUME UP button: press once and release",
                    "Built-in VOLUME DOWN button: press once and release"
                } : rearTest ? Steps.Where(step => step.Contains("REAR", StringComparison.OrdinalIgnoreCase)).ToArray() : Steps;
                for (var index = 0; index < steps.Length && !cancelled && DateTime.UtcNow < deadline; index++)
                {
                    Console.WriteLine($"[{index + 1}/{steps.Length}] {steps[index]}");
                    Console.WriteLine("Enter = start   S = skip   Q = finish");
                    var key = WaitKey(deadline, () => cancelled);
                    if (key is null or ConsoleKey.Q)
                    {
                        break;
                    }

                    if (key == ConsoleKey.S)
                    {
                        report.Steps.Add(new StepReport(steps[index], "skipped", [], 0, 0, [], []));
                        continue;
                    }

                    capture.Begin();
                    Console.WriteLine("RECORDING: perform the action now, release it, then press Enter.");
                    key = WaitKey(DateTime.UtcNow.AddSeconds(60), () => cancelled);
                    var step = capture.End(steps[index], cancelled ? "cancelled" : key is null ? "timeout" : "recorded");
                    report.Steps.Add(step);
                    Console.WriteLine($"Saved {step.Events.Length} changes, {step.ControllerSamples} controller samples, {step.RawReports} HID reports.");
                    foreach (var entry in step.Events.Where(item => item.Kind == "oem").Take(24))
                    {
                        Console.WriteLine($"  {entry.Control} {(entry.Down ? "DOWN" : "UP")}");
                    }

                    if (!step.Events.Any())
                    {
                        Console.WriteLine("  No input changes observed. This is evidence, not a pass.");
                    }

                    Console.WriteLine();
                    if (key == ConsoleKey.Q)
                    {
                        break;
                    }
                }

                report.Completed = report.Steps.Count == steps.Length && !cancelled;
                report.Faults = capture.Faults();
                if (!vendorSource.IsConnected || !qamSource.IsConnected)
                {
                    report.Faults = [.. report.Faults, "An OEM HID reader disconnected during the session."];
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        try
                        {
                            rawInput?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        finally
                        {
                            keyboard?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
                    finally
                    {
                        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    try
                    {
                        vendorSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        try
                        {
                            qamSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        finally
                        {
                            if (modeService is not null)
                            {
                                var release = modeService.ReleaseAsync(modeCycle!.Value, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                                report.ModeRelease = release.State.ToString();
                                Console.WriteLine($"Controller mode cleanup: {release.State}");
                                if (release.State != DeviceServiceState.Idle)
                                {
                                    report.Faults = [.. report.Faults, "Original controller mode restoration was not verified."];
                                }
                            }
                            report.Faults = report.Faults.Concat(capture.Faults()).Distinct().ToArray();
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            report.Faults = [.. report.Faults, exception.Message];
            Console.Error.WriteLine(exception.Message);
        }
        finally
        {
            if (owned)
            {
                owner.ReleaseMutex();
            }

            Console.CancelKeyPress -= cancel;
        }

        try
        {
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, $"Atlas-buttons-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"RESULT FILE: {path}");
            Console.WriteLine("Share that JSON file, or paste its contents. Results need review; completion alone does not mean all buttons passed.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not save results: {exception.Message}");
            return 70;
        }

        return report.Faults.Length == 0 ? 0 : 70;
    }

    private static ConsoleKey? WaitKey(DateTime deadline, Func<bool> cancelled)
    {
        while (!cancelled() && DateTime.UtcNow < deadline)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Enter or ConsoleKey.S or ConsoleKey.Q)
                {
                    return key;
                }
            }

            Thread.Sleep(millisecondsTimeout: 25);
        }

        return null;
    }

    private static object Describe(HidCollection collection)
    {
        return new { collection.VendorId, collection.ProductId, collection.ReleaseNumber,
            collection.UsagePage, collection.Usage, collection.InputLength };
    }
}

internal sealed class TestReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Tool { get; init; } = "LibHandheld.AtlasButtonTest";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public object? Identity { get; set; }
    public object[] Collections { get; set; } = [];
    public bool ControllerAvailable { get; set; }
    public bool Completed { get; set; }
    public string? ModeAcquisition { get; set; }
    public string? ModeRelease { get; set; }
    public bool? KeyboardLongPressAvailable { get; set; }
    public bool? RawInputAvailable { get; set; }
    public string[] AxisOrder { get; init; } = ["LeftStickX", "LeftStickY", "RightStickX", "RightStickY", "LeftTrigger", "RightTrigger"];
    public List<StepReport> Steps { get; } = [];
    public string[] Faults { get; set; } = [];
}

internal sealed record StepReport(string Action, string Status, CapturedEvent[] Events,
    int ControllerSamples, int RawReports, float[] MinimumAxes, float[] MaximumAxes, int DroppedEvents = 0);

internal readonly record struct CapturedEvent(double Milliseconds, string Kind, string? Control = null,
    bool Down = false, uint Buttons = 0, string? RawHex = null, bool Injected = false,
    string? Device = null, ushort ScanCode = 0, ushort RawFlags = 0);

internal sealed class CaptureBuffer
{
    private readonly Lock _gate = new();
    private readonly CapturedEvent[] _events = new CapturedEvent[2048];
    private readonly byte[] _vendor = new byte[64];
    private readonly byte[] _qam = new byte[64];
    private readonly float[] _minimum = new float[6];
    private readonly float[] _maximum = new float[6];
    private readonly List<string> _faults = [];
    private bool _active;
    private long _start;
    private int _count;
    private int _dropped;
    private int _samples;
    private int _reports;
    private uint _buttons;
    private int _vendorLength;
    private int _qamLength;

    internal void Begin()
    {
        lock (_gate)
        {
            Array.Clear(_events);
            Array.Fill(_minimum, float.PositiveInfinity);
            Array.Fill(_maximum, float.NegativeInfinity);
            _count = _dropped = _samples = _reports = _vendorLength = _qamLength = 0;
            _buttons = uint.MaxValue;
            _start = Stopwatch.GetTimestamp();
            _active = true;
        }
    }

    internal void Raw(int lane, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            _reports++;
            var previous = lane == 1 ? _vendor : _qam;
            ref var length = ref (lane == 1 ? ref _vendorLength : ref _qamLength);
            if (bytes.Length <= previous.Length && (length != bytes.Length || !bytes.SequenceEqual(previous.AsSpan(0, length))))
            {
                bytes.CopyTo(previous);
                length = bytes.Length;
                // Only bounded changed reports allocate strings; duplicate input allocates nothing.
                if (_count < _events.Length)
                {
                    Add(new CapturedEvent(Elapsed(), lane == 1 ? "vendor" : "qam", RawHex: Convert.ToHexString(bytes)));
                }
                else
                {
                    _dropped++;
                }
            }
        }
    }

    internal void Oem(string id, CanonicalButtons button, bool down)
    {
        lock (_gate)
        {
            if (_active)
            {
                Add(new CapturedEvent(Elapsed(), "oem", id, down, (uint)button));
            }
        }
    }

    internal void Keyboard(string id, bool down, bool injected)
    {
        lock (_gate)
        {
            if (_active)
            {
                Add(new CapturedEvent(Elapsed(), "global-keyboard", id, down, Injected: injected));
            }
        }
    }

    internal void DeviceKeyboard(string device, ushort key, bool down, ushort scan, ushort flags)
    {
        lock (_gate)
        {
            if (_active)
            {
                Add(new CapturedEvent(Elapsed(), "device-keyboard", $"VK_{key:X2}", down,
                    Device: device, ScanCode: scan, RawFlags: flags));
            }
        }
    }

    internal void DeviceConsumer(string device, string report)
    {
        lock (_gate)
        {
            if (_active)
            {
                Add(new CapturedEvent(Elapsed(), "device-consumer", RawHex: report, Device: device));
            }
        }
    }

    internal void Controller(CanonicalControllerSample sample)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            _samples++;
            ReadOnlySpan<float> axes = [sample.LeftStickX, sample.LeftStickY, sample.RightStickX,
                sample.RightStickY, sample.LeftTrigger, sample.RightTrigger];
            for (var index = 0; index < axes.Length; index++)
            {
                _minimum[index] = Math.Min(_minimum[index], axes[index]);
                _maximum[index] = Math.Max(_maximum[index], axes[index]);
            }

            var buttons = (uint)sample.Buttons;
            if (buttons != _buttons)
            {
                _buttons = buttons;
                Add(new CapturedEvent(Elapsed(), "controller", sample.Buttons.ToString(), Buttons: buttons));
            }
        }
    }

    internal void Fault(string message)
    {
        lock (_gate)
        {
            if (_faults.Count < 16)
            {
                _faults.Add(message);
            }
        }
    }

    internal string[] Faults()
    {
        lock (_gate)
        {
            return _faults.ToArray();
        }
    }

    internal StepReport End(string action, string status)
    {
        lock (_gate)
        {
            _active = false;
            return new StepReport(action, status, _events[.._count], _samples, _reports,
                _samples == 0 ? [] : (float[])_minimum.Clone(),
                _samples == 0 ? [] : (float[])_maximum.Clone(), _dropped);
        }
    }

    private double Elapsed() => Stopwatch.GetElapsedTime(_start).TotalMilliseconds;

    private void Add(CapturedEvent entry)
    {
        if (_count < _events.Length)
        {
            _events[_count++] = entry;
        }
        else
        {
            _dropped++;
        }
    }

    internal static int SelfCheck()
    {
        var capture = new CaptureBuffer();
        capture.Begin();
        for (var index = 0; index < 3000; index++)
        {
            capture.Raw(lane: 1, [0x9A, (byte)(index % 2)]);
        }

        capture.Controller(new CanonicalControllerSample { Timestamp = DateTimeOffset.UtcNow,
            Buttons = CanonicalButtons.A, LeftStickX = -1, RightTrigger = 1 });
        var result = capture.End("fixture", "recorded");
        if (result.Events.Length != 2048 || result.DroppedEvents == 0 || result.RawReports != 3000
            || result.MinimumAxes[0] != -1 || result.MaximumAxes[5] != 1)
        {
            throw new InvalidOperationException("Bounded capture fixture failed.");
        }

        capture.Begin();
        capture.Raw(lane: 2, [1, 1, 0]);
        capture.Raw(lane: 2, [1, 1, 0]);
        if (capture.End("deduplicate", "recorded").Events.Length != 1)
        {
            throw new InvalidOperationException("Duplicate report fixture failed.");
        }

        Console.WriteLine("Capture bounds, axes, reset and duplicate filtering passed; no hardware opened.");
        return 0;
    }
}

/// <summary>Pass-through observation of a closed key set; never suppresses or injects input.</summary>
internal sealed class ShortcutObservation(CaptureBuffer capture) : IAsyncDisposable
{
    private readonly LowLevelHook _hook = new(hookId: 13, "Atlas shortcut observation");
    private readonly Channel<KeyboardHookEvent> _events = Channel.CreateBounded<KeyboardHookEvent>(
        new BoundedChannelOptions(capacity: 256) { SingleReader = true, SingleWriter = true });
    private Task? _pump;
    private int _overflow;

    internal ValueTask<bool> StartAsync()
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var key in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var name = key.VirtualKey switch
                {
                    0x09 => "Tab", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
                    0x47 => "G", 0x5B => "LeftWin", 0x5C => "RightWin", 0x85 => "F22",
                    0xA0 => "LeftShift", 0xA1 => "RightShift", 0xA2 => "LeftCtrl", 0xA3 => "RightCtrl",
                    0xA4 => "LeftAlt", 0xA5 => "RightAlt", _ => "Unknown"
                };
                capture.Keyboard(name, key.Down, key.Injected);
            }
        });
        return _hook.StartAsync(Observe, exception => capture.Fault(exception.Message), CancellationToken.None);
    }

    private bool Observe(in KeyboardHookEvent key)
    {
        if (key.VirtualKey is 0x09 or 0x10 or 0x11 or 0x12 or 0x47 or 0x5B or 0x5C or 0x85 or >= 0xA0 and <= 0xA5)
        {
            if (!_events.Writer.TryWrite(key))
            {
                Interlocked.Exchange(ref _overflow, value: 1);
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _hook.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _events.Writer.TryComplete();
            if (_pump is not null)
            {
                await _pump.ConfigureAwait(false);
            }
            if (_overflow != 0)
            {
                capture.Fault("Keyboard shortcut queue overflowed; this session cannot establish complete key ordering.");
            }
        }
    }
}
