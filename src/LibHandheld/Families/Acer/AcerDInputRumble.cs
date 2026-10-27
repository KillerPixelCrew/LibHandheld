using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Atlas F90A gamepad rumble through its measured 32-byte output report 05.</summary>
/// <remarks>
///     Report byte 1 enables rumble only; right/high and left/low motor amplitudes are bytes 4/5.
///     The 2026-10-27 DInput trial produced a felt pulse and a measurable chassis acceleration
///     increase. Lighting flags stay clear. The owned output handle is independent of input reads.
/// </remarks>
internal sealed class AcerDInputRumble : IHidControllerRumble
{
    private readonly Lock _gate = new();
    private readonly byte[] _report = new byte[32];
    private FileStream? _stream;

    internal AcerDInputRumble(HidCollection collection)
    {
        if (collection is not { VendorId: 0x0502, ProductId: 0xF90A, ReleaseNumber: 0x0100,
                UsagePage: 1, Usage: 5, InputLength: 64, OutputLength: 32 })
        {
            throw new NotSupportedException("The Atlas DInput rumble report shape is not supported.");
        }

        _stream = HidDevices.OpenStream(collection);
        _report[0] = 5;
        _report[1] = 1;
    }

    public void Write(float lowFrequency, float highFrequency)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stream is null, this);
            _report[4] = Amplitude(highFrequency);
            _report[5] = Amplitude(lowFrequency);
            _stream.Write(_report);
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stream is not { } stream)
            {
                return;
            }

            try
            {
                _report[4] = 0;
                _report[5] = 0;
                stream.Write(_report);
                stream.Flush();
            }
            finally
            {
                _stream = null;
                stream.Dispose();
            }
        }
    }

    private static byte Amplitude(float value) =>
        float.IsFinite(value) ? (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue) : (byte)0;
}
