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

        var collection = HidDevices.Unique(HidDevices.Enumerate(0x1C4F, [0x0002]), collection =>
            collection.UsagePage == 0xFF00 && collection.Usage == 1
                                           && collection.InputLength == 65 && collection.OutputLength == 65);
        // Identical externally attached controllers must not be configured by accident.
        if (collection is null)
        {
            return false;
        }

        _stream = HidDevices.OpenStream(collection);
        return true;
    }

    internal async ValueTask<byte[]> ExchangeAsync(byte[] report, CancellationToken token,
        int timeoutMilliseconds = 400)
    {
        var stream = _stream ?? throw new InvalidOperationException("The SuperJoy collection is absent or ambiguous.");
        try
        {
            return await HidDevices.ExchangeAsync(stream, report, 65, response => response[4] == report[4],
                TimeSpan.FromMilliseconds(timeoutMilliseconds), token).ConfigureAwait(false);
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
        var level = enabled ? Math.Clamp(brightness, 0, 100) : 0;
        WriteColor(frame.AsSpan(8, 4), rightRgb, level, mode);
        WriteColor(frame.AsSpan(12, 4), leftRgb, level, mode);
        frame[20] = eject;
        frame[24] = vibration;
        frame[32] = 1;
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
        frame[10] = modifier;
        frame[12] = virtualKey;
        // Windows' report prefix shifts HHD's modifier/key offsets to 10 and 12.
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
