using PegLine.Core;

namespace PegLine.Core.Tests;

public class RopeIdleTests
{
    [Fact]
    public void HiddenRope_PollsSlowlyAndDoesNotRepaint()
    {
        Assert.Equal(RopeIdle.IdlePollMs, RopeIdle.TickIntervalMs(false, false, false, slideTickMs: 8));
        Assert.False(RopeIdle.ShouldPaintMotionFrame(visible: false, revealAnimating: false, falling: false, pointerOnRope: false));
        Assert.False(RopeIdle.ShouldPaintMotionFrame(visible: false, revealAnimating: false, falling: false, pointerOnRope: true));
    }

    [Fact]
    public void ShownButIdle_DoesNotRepaintAtMotionRate()
    {
        // Explicit show stays on screen. The pointer is elsewhere, so the sway loop must stop.
        Assert.Equal(RopeIdle.IdlePollMs, RopeIdle.TickIntervalMs(false, false, pointerOnRope: false, slideTickMs: 8));
        Assert.False(RopeIdle.ShouldPaintMotionFrame(visible: true, revealAnimating: false, falling: false, pointerOnRope: false));
    }

    [Fact]
    public void PointerFallOrReveal_UsesTheMotionTick()
    {
        Assert.Equal(RopeMotion.AnimTickMs, RopeIdle.TickIntervalMs(revealAnimating: true, falling: false, pointerOnRope: false, RopeMotion.AnimTickMs));
        Assert.Equal(8, RopeMotion.AnimTickMs);
        Assert.Equal(RopeIdle.MotionTickMs, RopeIdle.TickIntervalMs(false, falling: true, false, RopeMotion.AnimTickMs));
        Assert.Equal(RopeIdle.MotionTickMs, RopeIdle.TickIntervalMs(false, false, pointerOnRope: true, RopeMotion.AnimTickMs));
        Assert.Equal(16, RopeIdle.MotionTickMs);
    }

    [Fact]
    public void RevealAnimation_DoesNotRequestASecondMotionPaint()
    {
        Assert.False(RopeIdle.ShouldPaintMotionFrame(visible: true, revealAnimating: true, falling: false, pointerOnRope: true));
    }

    [Fact]
    public void FallsAndPointer_PaintWhileVisible()
    {
        Assert.True(RopeIdle.ShouldPaintMotionFrame(visible: true, revealAnimating: false, falling: true, pointerOnRope: false));
        Assert.True(RopeIdle.ShouldPaintMotionFrame(visible: true, revealAnimating: false, falling: false, pointerOnRope: true));
    }
}
