using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Windows;

/// <summary>Descriptor-based acquisition of a documented DirectInput-mode HID gamepad.</summary>
internal sealed class StandardControllerHidSource(HidControllerProfile profile)
{
    private nint _descriptor;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;

    internal HidCollection? Discover()
    {
        var matches = profile.UsbIds.SelectMany(id => HidDevices.Enumerate(id.Vendor, [id.Product])).Where(collection =>
                collection.UsagePage == 1 && collection.Usage is 4 or 5 && collection.InputLength is > 0 and <= 4096)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal void Start(HidCollection collection, Func<CanonicalButtons> oem, Func<MotionSample?> motion,
        Func<StandardTouchSample>? touchpad, Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault)
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException("The alternate physical HID reader is still owned.");
        }

        var details = HidDevices.Inspect(collection);
        var usages = new ushort[]
        {
            profile.LeftX, profile.LeftY, profile.RightX, profile.RightY, profile.LeftTrigger, profile.RightTrigger,
            0x39
        };
        var axes = usages.Select(usage => details.Capabilities.FirstOrDefault(cap =>
            cap.ReportType == HidReportType.Input && !cap.IsButton
                                                  && cap.UsagePage == 1 && cap.UsageMin <= usage &&
                                                  cap.UsageMax >= usage)).ToArray();
        if (axes.Take(6).Any(axis => axis is null))
        {
            throw new NotSupportedException("The alternate controller HID axes do not match its model profile.");
        }

        var stream = HidDevices.OpenStream(collection);
        if (!HidD_GetPreparsedData(stream.SafeFileHandle, out var descriptor))
        {
            stream.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _stream = stream;
        _descriptor = descriptor;
        _stop = new CancellationTokenSource();
        _reader = ReadAsync(stream, collection.InputLength, descriptor, usages, axes, oem, motion, touchpad, publish,
            fault, _stop.Token);
    }

    private async Task ReadAsync(FileStream stream, int length, nint descriptor, ushort[] usages, HidCapability?[] axes,
        Func<CanonicalButtons> oem, Func<MotionSample?> motion, Func<StandardTouchSample>? touchpad,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken token)
    {
        var report = new byte[length];
        var pressed = new ushort[64];
        var values = new uint[7];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(report, token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("The alternate physical HID gamepad disconnected.");
                }

                var matches = true;
                for (var axis = 0; axis < 6; axis++)
                {
                    var cap = axes[axis]!;
                    matches &= report[0] == cap.ReportId && HidP_GetUsageValue(0, 1, cap.LinkCollection, usages[axis],
                        out values[axis], descriptor, report, (uint)count) == 0x00110000;
                }

                if (!matches)
                {
                    continue;
                }

                var buttonCount = (uint)pressed.Length;
                if (HidP_GetUsages(0, 9, 0, pressed, ref buttonCount, descriptor, report, (uint)count) != 0x00110000)
                {
                    buttonCount = 0;
                }

                var buttons = oem();
                var leftFull = false;
                var rightFull = false;
                for (var index = 0; index < buttonCount; index++)
                {
                    var usage = pressed[index];
                    if (usage > 0 && usage <= profile.Buttons.Count)
                    {
                        buttons |= profile.Buttons[usage - 1];
                    }

                    leftFull |= usage == profile.LeftTriggerButton;
                    rightFull |= usage == profile.RightTriggerButton;
                }

                if (axes[6] is { } hat && report[0] == hat.ReportId
                                       && HidP_GetUsageValue(0, 1, hat.LinkCollection, 0x39, out values[6], descriptor,
                                           report, (uint)count) == 0x00110000)
                {
                    buttons |= Hat(values[6], hat.LogicalMin, hat.LogicalMax);
                }

                var touch = touchpad?.Invoke() ?? default;
                buttons |= (touch.Touch ? CanonicalButtons.LeftPadTouch : 0) |
                           (touch.Click ? CanonicalButtons.LeftPadClick : 0);
                var sample = new CanonicalControllerSample
                {
                    Timestamp = DateTimeOffset.UtcNow, Buttons = buttons,
                    LeftStickX = Normalize(values[0], axes[0]!), LeftStickY = -Normalize(values[1], axes[1]!),
                    RightStickX = Normalize(values[2], axes[2]!), RightStickY = -Normalize(values[3], axes[3]!),
                    LeftTrigger = leftFull ? 1 : (Normalize(values[4], axes[4]!) + 1) / 2,
                    RightTrigger = rightFull ? 1 : (Normalize(values[5], axes[5]!) + 1) / 2,
                    LeftPadX = touch.X, LeftPadY = touch.Y, LeftPadForce = touch.Touch ? 1 : 0, Motion = motion()
                };
                await publish(sample, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            await publish(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow), CancellationToken.None)
                .ConfigureAwait(false);
            fault(exception);
        }
    }

    internal static CanonicalButtons Hat(uint value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            return CanonicalButtons.None;
        }

        return ((int)value - minimum) switch
        {
            0 => CanonicalButtons.DPadUp, 1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
            2 => CanonicalButtons.DPadRight, 3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
            4 => CanonicalButtons.DPadDown, 5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
            6 => CanonicalButtons.DPadLeft, 7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp,
            _ => CanonicalButtons.None
        };
    }

    private static float Normalize(uint raw, HidCapability cap)
    {
        if (cap.LogicalMax <= cap.LogicalMin)
        {
            return 0;
        }

        var value = cap.LogicalMin < 0 && cap.BitSize is > 0 and < 32
            ? (int)(raw << (32 - cap.BitSize)) >> (32 - cap.BitSize)
            : (long)raw;
        return Math.Clamp((float)((value - cap.LogicalMin) * 2d / (cap.LogicalMax - cap.LogicalMin) - 1), -1, 1);
    }

    internal async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        if (_descriptor != 0)
        {
            _ = HidD_FreePreparsedData(_descriptor);
            _descriptor = 0;
        }

        _stop?.Dispose();
        _stop = null;
        _stream = null;
        _reader = null;
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint descriptor);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(nint descriptor);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value,
        nint descriptor, byte[] report, uint length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] usages, ref uint count,
        nint descriptor, byte[] report, uint length);
}
