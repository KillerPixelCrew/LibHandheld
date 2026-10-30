using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Transports;

public sealed class ControllerStallRecoveryTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void AReaderWithoutProgressForThreeRunningSecondsIsStalledOnce()
    {
        ControllerStallWatch watch = new();
        Assert.False(watch.Check(10, Tick));
        for (var check = 0; check < 5; check++)
        {
            Assert.False(watch.Check(10, Tick));
        }

        // The sixth unchanged half second reaches three seconds.
        Assert.True(watch.Check(10, Tick));
        Assert.False(watch.Check(10, Tick));
        Assert.False(watch.Check(10, TimeSpan.FromSeconds(10)));
        Assert.False(watch.Check(10, Tick));
    }

    [Fact]
    public void ProgressOrAFrozenWatchRestartsTheCount()
    {
        ControllerStallWatch watch = new();
        watch.Check(1, Tick);
        for (var check = 0; check < 5; check++)
        {
            watch.Check(1, Tick);
        }

        // The reader moved just in time.
        Assert.False(watch.Check(2, Tick));
        for (var check = 0; check < 5; check++)
        {
            Assert.False(watch.Check(2, Tick));
        }

        // A standby froze the watch with the reader: the long gap is not the reader's fault.
        Assert.False(watch.Check(2, TimeSpan.FromMinutes(20)));
        for (var check = 0; check < 5; check++)
        {
            Assert.False(watch.Check(2, Tick));
        }

        Assert.True(watch.Check(2, Tick));
    }

    [Fact]
    public void OnlyThePresentCompositeDeviceIsCycledNotItsInterfaces()
    {
        string[] present =
        [
            @"USB\VID_0502&PID_F901&MI_00\6&1AFCFAAD&0&0000",
            @"USB\VID_0502&PID_F901&IG_01\7&27799D0B&0&01",
            @"USB\VID_0502&PID_F901\00006F64096B22E7",
            @"USB\ROOT_HUB30\4&F2DE48D&0&0"
        ];
        ControllerUsbId[] atlas = [new(0x0502, 0xF901), new(0x0502, 0xF90A)];

        Assert.Equal(@"USB\VID_0502&PID_F901\00006F64096B22E7", UsbPortCycle.CompositeDevice(present, atlas));
        Assert.Null(UsbPortCycle.CompositeDevice(present, [new ControllerUsbId(0x0502, 0xF90A)]));
        Assert.Null(UsbPortCycle.CompositeDevice([present[0], present[1]], atlas));
    }
}
