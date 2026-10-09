using System.Buffers.Binary;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Ayaneo;

/// <summary>SuperJoy's vendor collection is separate from the physical XInput input collection.</summary>
internal sealed class AyaneoSuperJoy : IAsyncDisposable
{
    private FileStream? _stream;
    internal bool IsOpen => _stream is not null;

    public async ValueTask DisposeAsync()
    {
        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal bool Open()
    {
        if (_stream is not null)
        {
            return true;
        }

        var collections = HidDevices.Enumerate(0x1C4F, [0x0002]).Where(collection =>
            collection.UsagePage == 0xFF00 && collection.Usage == 1
                                           && collection.InputLength == 65 && collection.OutputLength == 65).ToArray();
        // Identical externally attached controllers must not be configured by accident.
        if (collections.Length != 1)
        {
            return false;
        }

        _stream = HidDevices.OpenStream(collections[0]);
        return true;
    }

    internal async ValueTask<byte[]> ExchangeAsync(byte[] report, CancellationToken token,
        int timeoutMilliseconds = 400)
    {
        var stream = _stream ?? throw new InvalidOperationException("The SuperJoy collection is absent or ambiguous.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        try
        {
            // The write is submitted exactly once. Reading several unrelated queued replies is safe;
            // a missing reply must never result in an automatic repeat of a potentially applied write.
            await stream.WriteAsync(report, timeout.Token).ConfigureAwait(false);
            var response = new byte[65];
            for (var unrelated = 0; unrelated < 8; unrelated++)
            {
                var length = await stream.ReadAsync(response, timeout.Token).ConfigureAwait(false);
                if (length != response.Length)
                {
                    throw new IOException("The SuperJoy response was incomplete.");
                }

                if (response[4] == report[4])
                {
                    return response;
                }
            }

            throw new IOException("No reply matched the SuperJoy command.");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<byte[]> QueryAsync(CancellationToken token)
    {
        return ExchangeAsync(Command(0x08), token);
    }

    internal ValueTask<byte[]> SelectCustomAsync(bool custom, CancellationToken token)
    {
        return ExchangeAsync(Command(0x0A, custom ? (byte)2 : (byte)1), token);
    }

    internal static byte[] Command(byte command, byte argument = 0)
    {
        var frame = new byte[65];
        frame[4] = command;
        frame[5] = argument;
        return frame;
    }

    internal static byte[] Lighting(int leftRgb, int rightRgb, int brightness, bool enabled,
        string effect = "solid", byte eject = 0, byte vibration = 0x20)
    {
        var frame = Command(0x09);
        frame[3] = 0x21;
        var mode = !enabled ? (byte)0xFF : effect switch { "pulse" => (byte)2, "rainbow" => (byte)3, _ => (byte)1 };
        WriteColor(frame.AsSpan(8, 4), rightRgb, brightness, mode);
        WriteColor(frame.AsSpan(12, 4), leftRgb, brightness, mode);
        frame[20] = eject;
        frame[22] = 0x33;
        frame[23] = 0x22;
        frame[24] = vibration;
        frame[32] = 1;
        frame[37] = 100;
        frame[38] = 100;
        Checksum(frame);
        return frame;
    }

    private static void WriteColor(Span<byte> destination, int rgb, int brightness, byte mode)
    {
        destination[0] = mode;
        for (var channel = 0; channel < 3; channel++)
        {
            destination[channel + 1] = (byte)(((rgb >> ((2 - channel) * 8)) & 255) * brightness / 100);
        }
    }

    /// <summary>The physical modules' known key indices are encoded rather than stored as source packets.</summary>
    internal static byte[] Mapping(byte index, byte virtualKey, byte kind = 0, byte modifier = 0)
    {
        var frame = Command(0x07);
        frame[3] = 0x0B;
        frame[5] = index;
        frame[7] = kind;
        frame[9] = modifier;
        frame[11] = virtualKey;
        // The command's checksum includes the keyboard key from byte 11 onward.
        Checksum(frame);
        return frame;
    }

    internal static void Checksum(Span<byte> frame)
    {
        ushort sum = 0;
        foreach (var value in frame[7..])
        {
            sum += value;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(frame[1..3], sum);
    }

    internal static string LeftModule(byte code)
    {
        return code switch
        {
            0 => "Disconnected", 0x02 => "Cross Film / Joystick", 0x04 => "Cross / Joystick",
            0x06 => "Cross / Touchpad", 0x08 => "Direction / Joystick", 0x42 => "Joystick / Cross Film",
            0x44 => "Joystick / Cross", 0x46 => "Touchpad / Cross", 0x48 => "Joystick / Direction", _ => "Unknown"
        };
    }

    internal static string RightModule(byte code)
    {
        return code switch
        {
            0 => "Disconnected", 0x10 => "ABXY / Joystick", 0x12 => "ABXY / Touchpad", 0x14 => "ABXYCZ",
            0x16 => "ABXY Film / Joystick", 0x50 => "Joystick / ABXY", 0x52 => "Touchpad / ABXY",
            0x54 => "ABXYCZ rotated", 0x56 => "Joystick / ABXY Film", _ => "Unknown"
        };
    }
}
