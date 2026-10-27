using System.ComponentModel;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Tests.Transports;

public sealed class IntelMsrSettingsTests
{
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(50, 0x06600000u)]
    [InlineData(-50, 0xF9A00000u)]
    public void VoltageOffsetUsesSignedMailboxUnits(int millivolts, uint expected)
    {
        Assert.Equal(expected, IntelMsrSettings.EncodeOffset(millivolts));
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(0x100000000L, 31)]
    public void CompletedMailboxIsWrittenOnceAndFirmwareRefusalIsDefinite(long completion, int error)
    {
        var module = new Module(0, completion);
        using var settings = new IntelMsrSettings(module);
        if (error == 0)
        {
            settings.WriteUndervolt(IntelUndervoltRail.Cache, 0);
        }
        else
        {
            Assert.Equal(error, Assert.Throws<Win32Exception>(() =>
                settings.WriteUndervolt(IntelUndervoltRail.Cache, 0)).NativeErrorCode);
        }
        Assert.Equal(1, module.Writes);
        Assert.Equal(unchecked((long)0x8000021100000000UL), module.Request);
    }

    [Fact]
    public void BusyMailboxIsNeverWritten()
    {
        var module = new Module(unchecked((long)(1UL << 63)));
        using var settings = new IntelMsrSettings(module);
        Assert.Equal(170, Assert.Throws<Win32Exception>(() =>
            settings.WriteUndervolt(IntelUndervoltRail.Core, 0)).NativeErrorCode);
        Assert.Equal(0, module.Writes);
    }

    private sealed class Module(params long[] replies) : IPawnIoModule
    {
        private int _read;
        internal int Writes;
        internal long Request;
        public long[] Execute(string function, long[] input, int outputWords)
        {
            Assert.Equal(0x150, input[0]);
            if (function == "ioctl_read_msr")
            {
                return [replies[_read++]];
            }
            Assert.Equal("ioctl_write_msr", function);
            Assert.Equal(0, outputWords);
            Writes++;
            Request = input[1];
            return [];
        }
        public void Dispose() { }
    }
}
