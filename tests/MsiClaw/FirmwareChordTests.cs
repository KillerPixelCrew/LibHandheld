using System.Runtime.InteropServices;

namespace LibHandheld.Families.MsiClaw.Tests;

[Collection("plugin-trace")]
public sealed class FirmwareChordTests
{
    [Fact]
    public void SyntheticInput_MatchesTheWindowsX64Layout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<NativeKeyboard.KeyboardInput>());
        Assert.Equal(32, Marshal.SizeOf<NativeKeyboard.InputUnion>());
        Assert.Equal(40, Marshal.SizeOf<NativeKeyboard.Input>());
        Assert.Equal(8, Marshal.OffsetOf<NativeKeyboard.Input>(nameof(NativeKeyboard.Input.Data)));
        Assert.Equal(16,
            Marshal.OffsetOf<NativeKeyboard.KeyboardInput>(nameof(NativeKeyboard.KeyboardInput.ExtraInfo)));

        const uint marker = 0x5753474D;
        var release = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, true, marker);
        Assert.Equal(NativeKeyboard.INPUT_KEYBOARD, release.Type);
        Assert.Equal(NativeKeyboard.VK_LWIN, release.Data.Keyboard.VirtualKey);
        Assert.Equal(NativeKeyboard.KEYEVENTF_KEYUP | NativeKeyboard.KEYEVENTF_EXTENDEDKEY,
            release.Data.Keyboard.Flags);
        Assert.Equal(marker, release.Data.Keyboard.ExtraInfo);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_RWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_LWIN, false, 1u)]
    [InlineData(NativeKeyboard.VK_DUMMY, false, 0u)]
    [InlineData(NativeKeyboard.VK_DUMMY, true, 2u)]
    public void SyntheticInput_MarksWindowsKeysAsExtended(uint key, bool keyUp, uint flags)
    {
        Assert.Equal(flags, NativeKeyboard.KeyInput(key, keyUp, 0).Data.Keyboard.Flags);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_G)]
    [InlineData(NativeKeyboard.VK_TAB)]
    public void FirmwareBurst_SuppressesTheOrphanAndReleasesWindowsOnce(uint target)
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, false));
        Assert.Equal(new ChordDecision(true, true, false), state.Observe(target, false, false));

        state.CommitSyntheticReleases(true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, true));
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(target, false, false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.Equal(default, state.Observe(target, false, false));

        // A later physical Windows press and release must pass through normally.
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    // HC's silenced "QS, Long-press" chord: the firmware sends Win+Tab for a long QS press, and HC
    // swallows it and raises QS. Task View is not opened.
    [Fact]
    public void WinTab_IsHcsLongQuickSettingsChord()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        var down = state.Observe(NativeKeyboard.VK_TAB, true, false);
        Assert.Equal(new ChordDecision(true, true, false, FirmwareChord.QuickSettingsLong), down);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(NativeKeyboard.VK_TAB, true, false));
        Assert.True(state.Observe(NativeKeyboard.VK_TAB, false, false).Suppress);
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
    }

    [Fact]
    public void ModifiedWinTab_PassesThrough()
    {
        FirmwareChordStateMachine state = new();
        state.SynchronizeModifiers(false, false, true);
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, false, false));
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, true)]
    [InlineData(NativeKeyboard.VK_LWIN, false)]
    [InlineData(NativeKeyboard.VK_RWIN, true)]
    [InlineData(NativeKeyboard.VK_RWIN, false)]
    public void WinG_BlocksDownRepeatsAndUpInEitherReleaseOrder(uint windowsKey, bool windowsUpFirst)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(windowsKey, true, false);
        var down = state.Observe(NativeKeyboard.VK_G, true, false);
        Assert.Equal(
            new ChordDecision(true, windowsKey == NativeKeyboard.VK_LWIN, windowsKey == NativeKeyboard.VK_RWIN,
                FirmwareChord.QuickSettings), down);
        state.CommitSyntheticReleases(down.ReleaseLeftWindows, down.ReleaseRightWindows);
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(NativeKeyboard.VK_G, true, false));
        if (windowsUpFirst)
        {
            Assert.True(state.Observe(windowsKey, false, false).Suppress);
        }

        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
        if (!windowsUpFirst)
        {
            Assert.True(state.Observe(windowsKey, false, false).Suppress);
        }

        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
    }

    [Fact]
    public void WinG_FailedReleaseDoesNotRetryOnRepeatOrSwallowPhysicalUps()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, true, false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(false, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, true, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void WinG_WithModifiersMatchesHcKeyDownInterception()
    {
        FirmwareChordStateMachine state = new();
        state.SynchronizeModifiers(true, true, true);
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, true, false).ReleaseLeftWindows);
    }

    [Fact]
    public void WinG_AfterOrphanReleaseStillConsumesGUp()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, false, false);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(new ChordDecision(true, false, false, FirmwareChord.QuickSettings),
            state.Observe(NativeKeyboard.VK_G, true, false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void WinG_BothWindowsKeysSuppressOnlyAcceptedSyntheticReleases(bool leftAccepted, bool rightAccepted)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_RWIN, true, false);
        Assert.Equal(new ChordDecision(true, true, true, FirmwareChord.QuickSettings),
            state.Observe(NativeKeyboard.VK_G, true, false));
        state.CommitSyntheticReleases(leftAccepted, rightAccepted);
        Assert.Equal(leftAccepted, state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.Equal(rightAccepted, state.Observe(NativeKeyboard.VK_RWIN, false, false).Suppress);
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }

    [Fact]
    public void WinG_ResetClearsAnActiveSuppression()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, true, false);
        state.CommitSyntheticReleases(true, false);
        state.Reset();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void WinG_InjectedGDoesNotStartOrEndPhysicalSuppression()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, true, true));
        Assert.True(state.Observe(NativeKeyboard.VK_G, true, false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, true));
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedOrphans_PassThrough(bool control, bool alt, bool shift)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        state.SynchronizeModifiers(control, alt, shift);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, false, false));
    }

    [Theory]
    [InlineData(0xADu)] // Volume mute
    [InlineData(0xAEu)] // Volume down
    [InlineData(0xAFu)] // Volume up
    [InlineData(0x48u)] // Unknown future firmware target
    public void OtherKeys_PassThroughEvenWithWindowsHeld(uint target)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(default, state.Observe(target, false, false));
        Assert.Equal(default, state.Observe(target, true, false));
        Assert.Equal(default, state.Observe(target, false, false));
    }

    [Fact]
    public void InjectedChord_DoesNotChangePhysicalKeyState()
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, true, true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, true));
    }

    [Fact]
    public void FailedSyntheticRelease_LeavesThePhysicalWindowsReleaseAlone()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(false, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void HookStartup_PreservesKeysAlreadyHeldOnTheDesktop()
    {
        FirmwareChordStateMachine state = new();
        state.InitializePreexisting(true, false, false, false, false, true, true);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void Reset_DropsPendingSyntheticReleaseState()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, false, false);
        state.CommitSyntheticReleases(true, false);
        state.Reset();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
    }

    [Fact]
    public void Observe_FirmwareOrphanGUpAndWinGDown_SuppressButModifiedOrphansPass()
    {
        FirmwareChordStateMachine firmware = new();
        _ = firmware.Observe(NativeKeyboard.VK_LWIN, true, false);
        var orphan = firmware.Observe(NativeKeyboard.VK_G, false, false);

        Assert.True(orphan.Suppress);
        Assert.True(orphan.ReleaseLeftWindows);
        firmware.CommitSyntheticReleases(true, false);
        Assert.True(firmware.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);

        FirmwareChordStateMachine physical = new();
        _ = physical.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.True(physical.Observe(NativeKeyboard.VK_G, true, false).Suppress);
        physical.CommitSyntheticReleases(true, false);
        Assert.True(physical.Observe(NativeKeyboard.VK_G, false, false).Suppress);

        FirmwareChordStateMachine modified = new();
        _ = modified.Observe(NativeKeyboard.VK_CONTROL, true, false);
        _ = modified.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.False(modified.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }
}
