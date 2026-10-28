using System.ComponentModel;
using System.Diagnostics;

namespace LibHandheld.Internal.Transports;

internal enum IntelUndervoltRail
{
    Core,
    Gpu,
    Cache,
    SystemAgent
}

/// <summary>Intel OC-mailbox voltage settings through the signed, allowlisted PawnIO IntelMSR module.</summary>
/// <remarks>
///     The module permits MSR 0150 but no MCHBAR writes, so Intel GPU clock control has no backend.
///     The mailbox command is sent once; subsequent
///     reads only wait for its busy bit and inspect the firmware completion code.
/// </remarks>
internal sealed class IntelMsrSettings(IPawnIoModule module) : IDisposable
{
    private const long Mailbox = 0x150;
    private const ulong Busy = 1UL << 63;
    private readonly Lock _gate = new();

    public void Dispose() => module.Dispose();

    internal static IntelMsrSettings Open() => new(PawnIoModule.OpenModule("IntelMSR"));

    internal void WriteUndervolt(IntelUndervoltRail rail, int millivolts)
    {
        var command = UndervoltCommandFor(rail);
        var data = EncodeUndervolt(millivolts);
        lock (_gate)
        {
            if ((Read() & Busy) != 0)
            {
                throw new Win32Exception(170, "The Intel OC mailbox is busy; no command was sent.");
            }
            var request = ((ulong)command << 32) | data;
            module.Execute("ioctl_write_msr", [Mailbox, unchecked((long)request)], outputWords: 0);
            var deadline = Stopwatch.StartNew();
            do
            {
                var reply = Read();
                if ((reply & Busy) == 0)
                {
                    var completion = (byte)(reply >> 32);
                    if (completion != 0)
                    {
                        // A completed nonzero mailbox status is a definite firmware refusal, not
                        // an uncertain transport timeout. The shared setting owner withholds it.
                        throw new Win32Exception(31, $"Intel firmware refused OC mailbox command 11 (status {completion:X2}).");
                    }
                    return;
                }
                Thread.Sleep(millisecondsTimeout: 1);
            } while (deadline.ElapsedMilliseconds < 100);
            throw new TimeoutException("Intel OC mailbox completion is unknown; the command was not retried.");
        }
    }

    // HC's OC mailbox command 0x11 with the voltage plane in bits 15:8.
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

    /// <summary>Encodes an undervolt amount the way Handheld Companion does: either sign lowers by |mV|.</summary>
    /// <remarks>
    ///     The controls are labelled undervolt and range -50 to 50, so a stored 50 must stay a 50 mV
    ///     decrease. Encoding the signed value would turn every positive setting into an overvolt.
    /// </remarks>
    internal static uint EncodeUndervolt(int millivolts)
    {
        return EncodeOffset(-Math.Abs(millivolts));
    }

    internal static uint EncodeOffset(int millivolts)
    {
        if (millivolts is < -50 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(millivolts));
        }
        // Signed 11-bit voltage offset, 1/1024 V per count, in mailbox bits 31:21.
        var units = (int)Math.Round(millivolts * 1024d / 1000, MidpointRounding.AwayFromZero);
        return ((uint)units & 0x7FF) << 21;
    }

    private ulong Read()
    {
        return IntelMsrPowerTransport.ReadMsr(module, Mailbox);
    }
}
