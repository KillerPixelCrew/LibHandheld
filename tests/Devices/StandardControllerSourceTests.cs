using LibHandheld.Internal.Windows;

namespace LibHandheld.Tests.Devices;

public sealed class StandardControllerSourceTests
{
    [Fact]
    public void AnUnchangedPollIsOnlyOneWhoseButtonsTriggersAndSticksAllMatch()
    {
        StandardXInput.Gamepad rest = default;

        Assert.True(StandardControllerSource.SameGamepad(rest, rest));
        // A d-pad press with the packet number unchanged still counts as a change.
        Assert.False(StandardControllerSource.SameGamepad(rest, rest with { Buttons = 0x0008 }));
        Assert.False(StandardControllerSource.SameGamepad(rest, rest with { LeftX = 12000 }));
        Assert.False(StandardControllerSource.SameGamepad(rest, rest with { RightTrigger = 40 }));
    }
}
