using PegLine.Core;

namespace PegLine.Core.Tests;

public class RopeVisibilityTests
{
    [Fact]
    public void ToggleShouldShow_ShowsWhenTheRopeIsOffScreen()
    {
        Assert.True(RopeVisibility.ToggleShouldShow(visuallyShown: false, allowed: true, canShow: true));
        Assert.True(RopeVisibility.ToggleShouldShow(visuallyShown: false, allowed: false, canShow: true));
        Assert.False(RopeVisibility.ToggleShouldShow(visuallyShown: true, allowed: true, canShow: true));
    }

    [Fact]
    public void ToggleShouldShow_TurnsOffWhenNothingCanHang()
    {
        Assert.False(RopeVisibility.ToggleShouldShow(visuallyShown: false, allowed: true, canShow: false));
        Assert.True(RopeVisibility.ToggleShouldShow(visuallyShown: false, allowed: false, canShow: false));
    }

    [Fact]
    public void ExtendIdle_ArmsAFreshShowSoItIsNotAlreadyIdle()
    {
        const long now = 10_000;
        var until = RopeVisibility.ExtendIdle(idleHideAt: 0, now, RopeVisibility.ShowLingerMs);

        Assert.Equal(now + RopeVisibility.ShowLingerMs, until);
        Assert.False(RopeVisibility.ShouldAutoHide(
            opening: false,
            visuallyVisible: true,
            now: now,
            idleHideAt: until));
    }

    [Fact]
    public void ExtendIdle_DoesNotShortenALongerLinger()
    {
        const long now = 10_000;
        var later = now + 8_000;

        Assert.Equal(later, RopeVisibility.ExtendIdle(later, now, RopeVisibility.ShowLingerMs));
    }

    [Fact]
    public void ShouldAutoHide_WhenTheShowNeverArmedTheIdleTimer()
    {
        // idleHideAt stays 0 if ShowRope does not extend it, so the next 16ms tick hides.
        Assert.True(RopeVisibility.ShouldAutoHide(
            opening: false,
            visuallyVisible: true,
            now: 5_000,
            idleHideAt: 0));
    }

    [Fact]
    public void ShouldAutoHide_NotWhileOpeningOrAlreadyHidden()
    {
        const long now = 5_000;

        Assert.False(RopeVisibility.ShouldAutoHide(opening: true, visuallyVisible: true, now, idleHideAt: 0));
        Assert.False(RopeVisibility.ShouldAutoHide(opening: false, visuallyVisible: false, now, idleHideAt: 0));
    }

    [Fact]
    public void HotkeyShow_AutoHidesAfterTheLinger()
    {
        const long now = 10_000;
        var until = RopeVisibility.ExtendIdle(idleHideAt: 0, now, RopeVisibility.ShowLingerMs);

        Assert.False(RopeVisibility.ShouldAutoHide(opening: false, visuallyVisible: true, now, until));
        Assert.True(RopeVisibility.ShouldAutoHide(opening: false, visuallyVisible: true, now: until, idleHideAt: until));
        // The show slide is still running. Hiding here parks the rope on the top edge.
        Assert.False(RopeVisibility.ShouldAutoHide(opening: true, visuallyVisible: true, now: until, idleHideAt: 0));
        // Auto-hide leaves the rope off screen. The next hotkey must show, not turn the switch off.
        Assert.True(RopeVisibility.ToggleShouldShow(visuallyShown: false, allowed: true, canShow: true));
    }

    [Fact]
    public void ShouldAutoHide_AfterTheLingerWhenThePointerHasLeft()
    {
        const long now = 20_000;

        Assert.True(RopeVisibility.ShouldAutoHide(
            opening: false,
            visuallyVisible: true,
            now,
            idleHideAt: now - 1));
        Assert.False(RopeVisibility.ShouldAutoHide(
            opening: false,
            visuallyVisible: true,
            now,
            idleHideAt: now + 1));
    }
}
