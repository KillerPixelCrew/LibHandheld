using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>Descriptor-based acquisition of a documented DirectInput-mode HID gamepad.</summary>
internal sealed class StandardControllerHidSource(HidControllerProfile profile)
{
    private HidUsageReader? _decoder;
    private Task? _reader;
    private CancellationTokenSource? _stop;
    private FileStream? _stream;

    internal HidCollection? Discover()
    {
        return HidDevices.Unique(profile.UsbIds.SelectMany(id =>
            HidDevices.Enumerate(id.Vendor, [id.Product])), collection => collection.UsagePage == 1
                                                                          && collection.Usage is 4 or 5 &&
                                                                          collection.InputLength > 0);
    }

    internal void Start(HidCollection collection, Func<CanonicalButtons> oem, Func<MotionSample?> motion,
        Func<StandardTouchSample>? touchpad, Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault)
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException("The physical HID reader is still owned.");
        }

        var stream = HidDevices.OpenStream(collection);
        HidUsageReader decoder;
        try
        {
            decoder = new HidUsageReader(stream.SafeFileHandle);
            if (!decoder.HasValue(profile.LeftX) || !decoder.HasValue(profile.LeftY)
                                                 || !decoder.HasValue(profile.RightX) ||
                                                 !decoder.HasValue(profile.RightY)
                                                 || !decoder.HasValue(profile.LeftTrigger) ||
                                                 !decoder.HasValue(profile.RightTrigger))
            {
                decoder.Dispose();
                throw new NotSupportedException("The HID axes do not match this model's controller profile.");
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _stream = stream;
        _decoder = decoder;
        _stop = new CancellationTokenSource();
        _reader = ReadAsync(stream, decoder, collection.InputLength, oem, motion, touchpad, publish, fault,
            _stop.Token);
    }

    private async Task ReadAsync(FileStream stream, HidUsageReader decoder, int length, Func<CanonicalButtons> oem,
        Func<MotionSample?> motion, Func<StandardTouchSample>? touchpad,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken token)
    {
        var report = new byte[length];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(report, token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("The physical HID gamepad disconnected.");
                }

                if (!Decode(decoder, report.AsSpan(0, count), oem(), motion(), touchpad?.Invoke() ?? default,
                        out var sample))
                {
                    continue;
                }

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

    private bool Decode(HidUsageReader decoder, ReadOnlySpan<byte> report, CanonicalButtons buttons,
        MotionSample? motion, StandardTouchSample touch, out CanonicalControllerSample sample)
    {
        sample = default;
        if (!decoder.TryValue(report, profile.LeftX, out var leftX) || !decoder.TryValue(report, profile.LeftY,
                                                                        out var leftY)
                                                                    || !decoder.TryValue(report, profile.RightX,
                                                                        out var rightX) || !decoder.TryValue(report,
                                                                        profile.RightY, out var rightY)
                                                                    || !decoder.TryValue(report, profile.LeftTrigger,
                                                                        out var leftTrigger) ||
                                                                    !decoder.TryValue(report, profile.RightTrigger,
                                                                        out var rightTrigger))
        {
            return false;
        }

        var leftFull = false;
        var rightFull = false;
        foreach (var usage in decoder.Usages(report, 9))
        {
            if (usage > 0 && usage <= profile.Buttons.Count)
            {
                buttons |= profile.Buttons[usage - 1];
            }

            leftFull |= usage == profile.LeftTriggerButton;
            rightFull |= usage == profile.RightTriggerButton;
        }

        if (decoder.TryValue(report, 0x39, out var hat))
        {
            buttons |= HidUsageReader.Hat((int)(hat.Raw - hat.Minimum));
        }

        buttons |= (touch.Touch ? CanonicalButtons.LeftPadTouch : 0) |
                   (touch.Click ? CanonicalButtons.LeftPadClick : 0);
        sample = new CanonicalControllerSample
        {
            Timestamp = DateTimeOffset.UtcNow, Buttons = buttons, LeftStickX = leftX.Signed, LeftStickY = -leftY.Signed,
            RightStickX = rightX.Signed, RightStickY = -rightY.Signed,
            LeftTrigger = leftFull ? 1 : leftTrigger.Unsigned, RightTrigger = rightFull ? 1 : rightTrigger.Unsigned,
            LeftPadX = touch.X, LeftPadY = touch.Y, LeftPadForce = touch.Touch ? 1 : 0, Motion = motion
        };
        return true;
    }

    internal static CanonicalButtons Hat(uint value, int minimum, int maximum)
    {
        return value < minimum || value > maximum ? CanonicalButtons.None : HidUsageReader.Hat((int)value - minimum);
    }

    internal async ValueTask StopAsync(CancellationToken token)
    {
        _stop?.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            await _reader.WaitAsync(token).ConfigureAwait(false);
        }

        _decoder?.Dispose();
        _decoder = null;
        _stop?.Dispose();
        _stop = null;
        _stream = null;
        _reader = null;
    }
}
