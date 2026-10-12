using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.SteamDeck;

internal sealed partial class SteamDeckMemory
{
    internal void WriteZotacUma(byte allocation)
    {
        var requests = ZotacUmaRequests(allocation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
            {
                foreach (var request in requests)
                {
                    // Reuse the pinned InpOut driver's acknowledged ushort-port/byte ABI.
                    // An uncertain index or data dispatch is never retried through another route.
                    if (!NativeDevice.DeviceIoControl(_readiness, 0x9C402008, request, 3, new byte[10], 10,
                            out _, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(),
                            "Zotac UMA CMOS dispatch failed; completion is unknown.");
                    }
                }

                return true;
            });
        }
    }

    internal static byte[][] ZotacUmaRequests(byte allocation)
    {
        // Zone's 16/32 GB configurations plus its 0..12 GB addition fit these exact encodings.
        if (allocation % 4 != 0 || allocation is not (>= 64 and <= 112 or >= 128 and <= 176))
        {
            throw new ArgumentOutOfRangeException(nameof(allocation));
        }

        return [[0x70, 0, 0x7A], [0x71, 0, allocation]];
    }
}
