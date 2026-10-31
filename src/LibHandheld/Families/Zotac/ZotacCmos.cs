using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Zotac;

/// <summary>The firmware UMA allocation through CMOS index 0x7A over the pinned InpOut port ABI.</summary>
internal static class ZotacCmos
{
    internal static void WriteUma(InpOutTransport inpOut, byte allocation)
    {
        var requests = UmaRequests(allocation);
        HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
        {
            // Reuse the pinned InpOut driver's acknowledged ushort-port/byte ABI.
            // An uncertain index or data dispatch is never retried through another route.
            inpOut.WritePortBytes(requests, "Zotac UMA CMOS dispatch failed; completion is unknown.");
            return true;
        });
    }

    internal static byte[][] UmaRequests(byte allocation)
    {
        // Zone's 16/32 GB configurations plus its 0..12 GB addition fit these exact encodings.
        if (allocation % 4 != 0 || allocation is not (>= 64 and <= 112 or >= 128 and <= 176))
        {
            throw new ArgumentOutOfRangeException(nameof(allocation));
        }

        return [[0x70, 0, 0x7A], [0x71, 0, allocation]];
    }
}
