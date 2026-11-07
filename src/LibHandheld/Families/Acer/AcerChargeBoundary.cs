using System.Buffers.Binary;

namespace LibHandheld.Families.Acer;

/// <summary>The Atlas EC's native charge stop/resume thresholds, separate from WMI optimized charging.</summary>
internal sealed record AcerChargeBoundary(bool Enabled, byte Upper, byte Lower)
{
    internal const byte ResumePercent = 50;

    /// <summary>Queries battery selector 3 through EC command 0B03.</summary>
    internal static byte[] Query()
    {
        var payload = new byte[64];
        payload[1] = 0xA0;
        payload[2] = 3;
        payload[3] = 0x0B;
        payload[4] = 2;
        payload[5] = 3;
        return payload;
    }

    /// <summary>Builds one complete threshold write; firmware clears the thresholds when disabled.</summary>
    internal byte[] Packet()
    {
        if (Upper > 100 || Lower >= Upper)
        {
            throw new ArgumentOutOfRangeException(nameof(Upper), "Charge stop must exceed the resume threshold.");
        }
        var payload = Query();
        payload[4] = 1;
        payload[6] = Enabled ? (byte)1 : (byte)0;
        payload[7] = Upper;
        payload[8] = Lower;
        return payload;
    }

    /// <summary>An uninitialized FF state has no observed thresholds; it must not become a fabricated reading.</summary>
    internal static AcerChargeBoundary? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 0xE000
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) != 0x0B03)
        {
            throw new IOException("Acer charge-boundary reply did not match the query.");
        }
        if (payload[5] == 0xFF && payload[6] == 0xFF && payload[7] == 0xFF && payload[8] == 0xFF)
        {
            return null;
        }
        if (payload[4] != 0 || payload[5] != 3 || payload[6] > 1)
        {
            throw new IOException("Acer charge-boundary fields are invalid.");
        }
        if (payload[6] == 0)
        {
            // Disabled firmware returns FF for both thresholds; 100 means no active charge ceiling.
            return new AcerChargeBoundary(false, 100, ResumePercent);
        }
        if (payload[7] > 100 || payload[8] >= payload[7])
        {
            throw new IOException("Acer charge thresholds are invalid.");
        }
        return new AcerChargeBoundary(payload[6] == 1, payload[7], payload[8]);
    }
}
