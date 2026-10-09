using System.Buffers.Binary;

namespace LibHandheld.Families.LegionGo;

/// <summary>Wire encodings independently assembled from the Lenovo command mechanics.</summary>
internal static class LegionProtocol
{
    public const int ShortPower = 0x0101FF00;
    public const int LongPower = 0x0102FF00;
    public const int PeakPower = 0x0103FF00;
    public const int CrossPower = 0x0106FF00;
    public const int ChargeProtection = 0x03010001;
    public const int FullFan = 0x04020000;
    public const int FanRpm = 0x04030001;
    public const int Temperature = 0x05040000;

    public static byte[] FanTable(ReadOnlySpan<ushort> values)
    {
        if (values.Length != 10)
        {
            throw new ArgumentException("Lenovo fan tables contain ten entries.", nameof(values));
        }

        var buffer = new byte[64];
        buffer[0] = 1;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(values));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6 + 2 * i), values[i]);
        }

        return buffer;
    }

    public static byte[] SlimReport(ReadOnlySpan<byte> command)
    {
        if (command.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var report = new byte[65];
        command.CopyTo(report.AsSpan(1));
        return report;
    }

    public static byte[] Rgb(LegionGoModel model, int side, string effect, int color, int brightness, int speed)
    {
        if (brightness is < 0 or > 100 || speed is < 0 or > 100 || color is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }

        var mode = effect switch
        {
            "solid" => model.Slim ? 0 : 1,
            "breathing" => model.Slim ? 1 : 2,
            "rainbow" => 3,
            "wheel" => model.Slim ? 2 : 4,
            _ => throw new ArgumentException("Unknown lighting effect.", nameof(effect))
        };
        var r = (byte)(color >> 16);
        var g = (byte)(color >> 8);
        var b = (byte)color;
        // HC's active Windows Go S path uses percentages directly; tablet firmware uses six bits.
        return model.Slim
            ? SlimReport([0x10, 5, (byte)mode, r, g, b, (byte)brightness, (byte)(100 - speed)])
            :
            [
                5, 12, 0x72, 1, (byte)side, (byte)mode, r, g, b,
                (byte)Math.Min(63, 64 * brightness / 100), (byte)Math.Min(63, 64 * (100 - speed) / 100), 3, 1
            ];
    }

    public static IEnumerable<byte[]> LightingEnabled(LegionGoModel model, bool enabled)
    {
        if (model.Slim)
        {
            yield return SlimReport([4, 6, (byte)(enabled ? 1 : 0)]);
            yield break;
        }

        yield return [5, 6, 0x70, 2, 3, (byte)(enabled ? 1 : 0), 1];
        yield return [5, 6, 0x70, 2, 4, (byte)(enabled ? 1 : 0), 1];
    }

    public static IEnumerable<byte[]> Motion(LegionGoModel model, bool enabled)
    {
        if (model.Slim)
        {
            yield return SlimReport([4, 7, (byte)(enabled ? 1 : 0)]);
            yield return SlimReport([4, 5, (byte)(enabled ? 1 : 0)]);
            yield break;
        }

        foreach (var side in new byte[] { 3, 4 })
        {
            if (enabled)
            {
                yield return [5, 6, 0x6A, 2, side, 1, 1];
            }

            yield return [5, 6, 0x6A, 7, side, (byte)(enabled ? 2 : 1), 1];
        }
    }

    public static IEnumerable<byte[]> Touchpad(LegionGoModel model, bool nativeMouse)
    {
        if (model.Slim)
        {
            yield return SlimReport([4, 8, (byte)(nativeMouse ? 0 : 1)]);
            yield return SlimReport([8, 3, (byte)(nativeMouse ? 0 : 1)]);
        }
        else
        {
            yield return [5, 6, 0x6B, 2, 4, (byte)(nativeMouse ? 1 : 0), 1];
        }
    }

    public static byte[] ControllerMode(bool directInput)
    {
        return [5, 0, 4, 14, 3, (byte)(directInput ? 2 : 1)];
    }

    // Explicit user action only: there is no readable original for these persistent controller mappings.
    public static IEnumerable<byte[]> ResetMappings(LegionGoModel model)
    {
        if (!model.Slim)
        {
            for (byte target = 1; target <= 4; target++)
            {
                yield return [4, 5, 5, 1, 1, target, 1];
            }

            yield break;
        }

        byte[][] groups =
        [
            [3, 4, 5, 6, 7, 8, 9, 10], [11, 12, 13, 14, 15, 16, 18, 19],
            [20, 21, 22, 23, 24, 25, 28, 29], [35, 36]
        ];
        for (var block = 0; block < groups.Length; block++)
        {
            var payload = new byte[64];
            payload[0] = 0x12;
            payload[1] = 1;
            payload[2] = (byte)(block + 1);
            payload[3] = (byte)groups[block].Length;
            for (var index = 0; index < groups[block].Length; index++)
            {
                var offset = 4 + index * 7;
                payload[offset] = groups[block][index];
                payload[offset + 1] = 0x82;
                payload[offset + 2] = groups[block][index];
            }

            yield return SlimReport(payload);
        }
    }

    public static int FanPercentToRpm(int percent)
    {
        return 1500 + (int)Math.Round(Math.Clamp(percent, 0, 100) * 37d);
    }
}
