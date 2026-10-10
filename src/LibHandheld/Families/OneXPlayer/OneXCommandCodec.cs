namespace LibHandheld.Families.OneXPlayer;

/// <summary>Vendor wire formats, independent of the HID and serial handles.</summary>
internal static class OneXCommandCodec
{
    internal static byte[] V1(byte command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var frame = new byte[64];
        frame[0] = command;
        frame[1] = 0x3F;
        frame[2] = 1;
        payload.CopyTo(frame.AsSpan(3));
        frame[62] = 0x3F;
        frame[63] = command;
        return frame;
    }

    internal static byte[] Serial(byte side, byte mode, int color = 0)
    {
        var frame = new byte[64];
        frame[0] = 0xFD;
        frame[1] = 0x3F;
        frame[2] = side;
        frame[3] = mode;
        if (mode == 0xFE)
        {
            FillRgb(frame.AsSpan(6, 56), color);
        }

        frame[62] = 0x3F;
        frame[63] = 0xFD;
        return frame;
    }

    internal static byte[] Brightness(OneXLightingKind kind, int percentage, byte side)
    {
        var enabled = percentage > 0;
        var level = (byte)(percentage <= 33 ? 1 : percentage <= 66 ? 3 : 4);
        if (kind == OneXLightingKind.HidV2)
        {
            var frame = new byte[65];
            frame[1] = 7;
            frame[2] = 0xFF;
            frame[3] = 0xFD;
            frame[4] = 1;
            frame[5] = 5;
            frame[6] = (byte)Math.Clamp((int)Math.Round(percentage / 20d), 0, 4);
            return frame;
        }

        if (kind == OneXLightingKind.Serial)
        {
            // X1's serial firmware uses a different boundary and a zero level when off.
            level = (byte)(!enabled ? 0 : percentage < 33 ? 1 : percentage > 66 ? 4 : 3);
            var frame = Serial(side, 0xFD);
            frame[4] = side == 0 ? (byte)3 : (byte)0;
            frame[6] = (byte)(side == 0 || enabled ? 1 : 0);
            if (side == 0)
            {
                frame[7] = 5;
                frame[8] = level;
            }

            return frame;
        }

        return V1(0xB8, [0xFD, side, 2, (byte)(enabled ? 1 : 0), 5, level]);
    }

    internal static byte[] Lighting(OneXLightingKind kind, string effect, int color, byte side)
    {
        var mode = Effect(effect);
        if (kind == OneXLightingKind.Serial)
        {
            return Serial(side, mode, effect == "classic" ? 0xB73000 : color);
        }

        if (kind == OneXLightingKind.HidV2)
        {
            var frame = new byte[65];
            frame[1] = 7;
            frame[2] = 0xFF;
            frame[3] = mode;
            if (effect is "solid" or "classic")
            {
                FillRgb(frame.AsSpan(4, 60), effect == "classic" ? 0xB73000 : NearestColor(color));
            }

            return frame;
        }

        if (effect is not ("solid" or "breathing" or "classic"))
        {
            return V1(0xB8, [mode, side, 2]);
        }

        var payload = new byte[59];
        payload[0] = mode;
        payload[1] = side;
        payload[2] = 2;
        FillRgb(payload.AsSpan(3, 56), effect == "classic" ? 0xB73000 : color);
        return V1(0xB8, payload);
    }

    internal static byte[] Intercept(bool enabled)
    {
        return V1(0xB2, enabled ? [3] : [0, 1, 2]);
    }

    internal static IReadOnlyList<byte[]> ControllerConfiguration(OneXProtocolProfile profile)
    {
        // HC 1.3.1.6: X1/X2 use bank 0x20; X2 Mini Pro/Apex use bank 2.
        // Only Mini/Apex end with intercept-off, and X1 Mini uses the B3 vibration command instead.
        if (profile.SimpleVibration)
        {
            return [V1(0xB3, [1, 5, 5])];
        }

        var bank = (byte)(profile.Mini || profile.Apex ? 2 : 0x20);
        var first = RemapPage(1, bank, 1, 9);
        var second = RemapPage(2, bank, 10, 16);
        AddMapping(second, 7, 0x22, 2, 1, (byte)(profile.Mini ? 104 : 103));
        AddMapping(second, 8, 0x23, 2, 1, (byte)(profile.Mini ? 105 : 102));
        var frames = new List<byte[]> { V1(0xB4, first), V1(0xB4, second) };
        if (profile.Apex)
        {
            frames.Add(V1(0xB2, [1, 0x1F, 0x40, 3, 2, 3, 0, 0, 0, 1]));
        }
        else if (profile.X2)
        {
            frames.Add(V1(0xB4, [2, 0x38, bank, 3, 1, 0x24, 2, 2, 5, 0, 0, 0x25, 1, 0x21, 0, 0, 0]));
        }

        if (profile.Mini || profile.Apex)
        {
            frames.Add(Intercept(false));
        }

        return frames;
    }

    private static byte[] RemapPage(byte page, byte bank, byte first, byte last)
    {
        var payload = new byte[59];
        payload[0] = 2;
        payload[1] = 0x38;
        payload[2] = bank;
        payload[3] = page;
        payload[4] = 1;
        for (var id = first; id <= last; id++)
        {
            AddMapping(payload, id - first, id, 1, id, 0);
        }

        return payload;
    }

    private static void AddMapping(byte[] payload, int slot, byte button, byte kind, byte mapping, byte key)
    {
        var offset = 5 + slot * 6;
        payload[offset] = button;
        payload[offset + 1] = kind;
        payload[offset + 2] = mapping;
        payload[offset + 3] = key;
    }

    internal static byte[] Vibration(int strength, bool simple = false)
    {
        if (simple)
        {
            return V1(0xB3, [(byte)(strength == 0 ? 2 : 1), (byte)strength, (byte)strength]);
        }

        var payload = new byte[59];
        byte[] prefix =
        [
            2, 0x38, 2, 0xE3, 0x39, 0xE3, 0x39, 0xE3, 0x39,
            (byte)(strength == 0 ? 2 : 1), (byte)strength, (byte)strength, 0xE3, 0x39, 0xE3
        ];
        prefix.CopyTo(payload, 0);
        byte[] suffix = [0x39, 0xE3, 0x39, 0xE3, 0xE3, 2, 4, 0x39, 0x39];
        suffix.CopyTo(payload, 50);
        return V1(0xB3, payload);
    }

    private static byte Effect(string effect)
    {
        return effect switch
        {
            "solid" or "classic" => 0xFE, "breathing" => 0xF0, "rainbow" => 3, "monster-woke" => 0x0D,
            "sunset" => 0x0B, "neon" => 5, "dreamy" => 7, "cyberpunk" => 9, "colorful" => 0x0C,
            "aurora" => 1, "sun" => 8, "aok-classic" => 0x14,
            _ => throw new ArgumentOutOfRangeException(nameof(effect))
        };
    }

    private static void FillRgb(Span<byte> output, int color)
    {
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (byte)(color >> ((index % 3) switch { 0 => 16, 1 => 8, _ => 0 }));
        }
    }

    private static int NearestColor(int color)
    {
        ReadOnlySpan<int> palette =
        [
            0xFF0000, 0xFF5200, 0xFFFF00, 0x82FF00, 0x00FF00, 0x00FF6E,
            0x00FFFF, 0x82FFFF, 0x0000FF, 0x7A00FF, 0xFF00FF, 0xFF0081
        ];
        var nearest = palette[0];
        var minimum = int.MaxValue;
        foreach (var candidate in palette)
        {
            var r = (byte)(color >> 16) - (byte)(candidate >> 16);
            var g = (byte)(color >> 8) - (byte)(candidate >> 8);
            var b = (byte)color - (byte)candidate;
            var distance = r * r + g * g + b * b;
            if (distance < minimum)
            {
                minimum = distance;
                nearest = candidate;
            }
        }

        return nearest;
    }
}
