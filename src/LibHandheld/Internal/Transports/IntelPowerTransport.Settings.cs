namespace LibHandheld.Internal.Transports;

internal enum IntelUndervoltRail
{
    Core,
    Gpu,
    Cache,
    SystemAgent
}

internal sealed partial class IntelPowerTransport
{
    internal int ReadGpuClock()
    {
        return Transaction(() => checked((byte)MemoryReturn("/rdmem8", 0x5994)) * 50);
    }

    internal void WriteGpuClock(int megahertz)
    {
        var encoded = EncodeGpuClock(megahertz);
        Transaction(() =>
        {
            var response = ParseReturn(Run(_path, "/wrmem8", Hex(_mchbar + 0x5994), Hex(encoded)));
            if (response != encoded)
            {
                throw new IOException("Intel KX did not acknowledge the submitted GPU clock.");
            }

            return true;
        });
    }

    internal void WriteUndervolt(IntelUndervoltRail rail, int offsetMillivolts)
    {
        var command = UndervoltCommandFor(rail);
        var data = EncodeUndervolt(offsetMillivolts);
        Transaction(() =>
        {
            WriteMsr(0x150, ((ulong)command << 32) | data);
            return true;
        });
    }

    internal static uint UndervoltCommandFor(IntelUndervoltRail rail)
    {
        return rail switch
        {
            IntelUndervoltRail.Core => 0x80000011,
            IntelUndervoltRail.Gpu => 0x80000111,
            IntelUndervoltRail.Cache => 0x80000211,
            IntelUndervoltRail.SystemAgent => 0x80000411,
            _ => throw new ArgumentOutOfRangeException(nameof(rail))
        };
    }

    internal static uint EncodeUndervolt(int offsetMillivolts)
    {
        if (offsetMillivolts is < -50 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetMillivolts));
        }

        // HC/Cyphray encode the magnitude in a signed 12-bit VID, with rounding before doubling.
        var magnitude = Math.Abs(offsetMillivolts);
        var code = magnitude == 0 ? 0 : 4096 - (magnitude * 1024 + 500) / 1000 * 2;
        return checked((uint)code) << 20;
    }

    internal static byte EncodeGpuClock(int megahertz)
    {
        if (megahertz is < 0 or > 12750 || megahertz % 50 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(megahertz));
        }

        return checked((byte)(megahertz / 50));
    }
}
