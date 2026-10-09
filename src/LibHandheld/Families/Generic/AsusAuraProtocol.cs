namespace LibHandheld.Families.Generic;

/// <summary>The Aura module chooses its report family and capabilities before any command is built.</summary>
internal static class AsusAuraProtocol
{
    internal const ushort Vendor = 0x0B05;
    internal const ushort Lightbar = 0x18C6;
    internal const ushort Keyboard = 0x1A30;
    internal const ushort Xg2025 = 0x1BC1;

    internal static byte ReportId(ushort product)
    {
        return product == Xg2025 ? (byte)0x5E : (byte)0x5D;
    }

    internal static int ReportLength(ushort product)
    {
        return product == Xg2025 ? 300 : 64;
    }

    internal static bool Supported(ushort product)
    {
        return product is Lightbar or Keyboard or Xg2025;
    }

    internal static bool Application(ushort page, ushort usage)
    {
        return ((uint)page << 16 | usage) is
            0xFF310076 or 0xFF310079 or 0xFF310080 or 0xFF000020;
    }

    internal static string[] Effects(ushort product)
    {
        return product switch
        {
            Lightbar => ["solid", "pulse", "duality", "cycle", "rainbow", "strobe"],
            Keyboard => ["solid", "pulse", "duality", "rainbow", "strobe"],
            Xg2025 => ["solid", "pulse", "duality", "rainbow"],
            _ => throw new ArgumentOutOfRangeException(nameof(product))
        };
    }

    internal static byte[] Handshake(ushort product)
    {
        var packet = Packet(product, 0);
        packet[1] = 0x41;
        "ASUS Tech.Inc."u8.CopyTo(packet.AsSpan(1));
        return packet;
    }

    internal static IReadOnlyList<byte[]> Initialize(ushort product)
    {
        return product == Xg2025
            ? [Packet(product, 0xE4, 2), Packet(product, 0xC5, 0x50), Packet(product, 0xBD, 0, 1)]
            : [];
    }

    internal static byte[] Brightness(ushort product, int percentage)
    {
        return Packet(product, 0xBA, 0xC5, 0xC4,
                (byte)Math.Clamp((int)Math.Round(Math.Clamp(percentage, 0, 100) * 3d / 100), 0, 3))
            .WithReport(product == Xg2025 ? (byte)0x5E : (byte)0x5A);
    }

    private static byte[] WithReport(this byte[] packet, byte report)
    {
        packet[0] = report;
        return packet;
    }

    internal static byte[] Power(ushort product, bool boot, bool awake, bool sleep, bool shutdown)
    {
        if (product == Xg2025)
        {
            return Packet(product, 0xC5, awake ? (byte)0x50 : (byte)0);
        }

        var keyb = (byte)((boot ? 3 : 0) | (awake ? 12 : 0) | (sleep ? 48 : 0) | (shutdown ? 192 : 0));
        var bar = (byte)((boot ? 2 : 0) | (awake ? 5 : 0) | (sleep ? 8 : 0) | (shutdown ? 16 : 0));
        var lid = (byte)((boot ? 17 : 0) | (awake ? 34 : 0) | (sleep ? 68 : 0) | (shutdown ? 136 : 0));
        return Packet(product, 0xBD, 1, keyb, bar, lid, lid, 0xFF);
    }

    internal static byte[] Color(ushort product, string effect, int primary, int secondary, int speed, bool left)
    {
        if (!Effects(product).Contains(effect, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(effect));
        }

        var mode = effect switch
        {
            "solid" => 0, "pulse" or "duality" => 1, "rainbow" => 2, "cycle" => 3, "strobe" => 10, _ => 0
        };
        if (effect == "cycle")
        {
            primary = 0;
        }

        if (effect != "duality")
        {
            secondary = 0;
        }

        var packet = Packet(product, 0xB3, 0, (byte)mode, (byte)(primary >> 16), (byte)(primary >> 8), (byte)primary,
            effect == "solid" ? (byte)0 : (byte)(speed <= 33 ? 0xE1 : speed <= 66 ? 0xEB : 0xF5),
            (byte)(effect == "cycle" && left ? 1 : 0), 0, (byte)(secondary >> 16), (byte)(secondary >> 8),
            (byte)secondary);
        return packet;
    }

    internal static IReadOnlyList<byte[]> Apply(ushort product)
    {
        return product == Xg2025
            ? [Packet(product, 0xB4), Packet(product, 0xB5)]
            : [Packet(product, 0xB5, 0, 0, 0), Packet(product, 0xB4)];
    }

    internal static byte[] Packet(ushort product, byte command, params byte[] arguments)
    {
        if (!Supported(product))
        {
            throw new ArgumentOutOfRangeException(nameof(product));
        }

        var packet = new byte[ReportLength(product)];
        packet[0] = ReportId(product);
        packet[1] = command;
        if (arguments.Length > packet.Length - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments));
        }

        arguments.CopyTo(packet, 2);
        return packet;
    }
}
