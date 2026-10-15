using PegLine.Core;

namespace PegLine.Core.Tests;

public class RopeMotionTests
{
    [Fact]
    public void Durations_AreShorterThanTheOldStepAnimation()
    {
        // Previous motion: show step 0.14 and hide step 0.17 on a 16ms timer
        // (~120ms show, ~100ms hide), and that clock slipped whenever a frame ran long.
        Assert.True(RopeMotion.ShowDurationMs <= 70);
        Assert.True(RopeMotion.HideDurationMs <= 50);
        Assert.True(RopeMotion.HideDurationMs < RopeMotion.ShowDurationMs);
        Assert.True(RopeMotion.AnimTickMs < RopeMotion.IdleTickMs);
    }

    [Fact]
    public void RevealAt_ShowReachesOneAndHideReachesZeroOnTime()
    {
        Assert.Equal(1f, RopeMotion.RevealAt(direction: 1, from: 0.05f, RopeMotion.ShowDurationMs));
        Assert.Equal(1f, RopeMotion.RevealAt(direction: 1, from: 0.05f, RopeMotion.ShowDurationMs + 30));
        Assert.Equal(0f, RopeMotion.RevealAt(direction: -1, from: 1f, RopeMotion.HideDurationMs));
        Assert.Equal(0f, RopeMotion.RevealAt(direction: -1, from: 0.4f, RopeMotion.HideDurationMs));
    }

    [Fact]
    public void RevealAt_DoesNotMoveBeforeTimePasses()
    {
        Assert.Equal(0.4f, RopeMotion.RevealAt(1, 0.4f, 0));
        Assert.Equal(0.4f, RopeMotion.RevealAt(-1, 0.4f, 0));
    }

    [Fact]
    public void RevealAt_EaseOutIsAheadOfLinearHalfwayThrough()
    {
        var show = RopeMotion.RevealAt(1, 0f, RopeMotion.ShowDurationMs / 2);
        var hide = RopeMotion.RevealAt(-1, 1f, RopeMotion.HideDurationMs / 2);

        Assert.True(show > 0.5f);
        Assert.True(show < 1f);
        Assert.True(hide < 0.5f);
        Assert.True(hide > 0f);
    }
}
