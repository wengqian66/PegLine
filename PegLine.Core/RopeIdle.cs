namespace PegLine.Core;

/// <summary>
/// How often the overlay wakes up. Show/hide uses the 8ms slide clock; sway and
/// falling cards use <see cref="MotionTickMs"/>. Idle only polls the cursor.
/// </summary>
public static class RopeIdle
{
    public const int MotionTickMs = 16;

    /// <summary>Cursor poll while the rope is up but nobody is touching it, or while it is hidden.</summary>
    public const int IdlePollMs = 100;

    public static bool NeedsMotionTick(bool revealAnimating, bool falling, bool pointerOnRope) =>
        revealAnimating || falling || pointerOnRope;

    /// <summary>
    /// Continuous repaint (sway / falling cards). The reveal animation paints itself;
    /// this is the extra full-width frame that used to run on every 16ms tick.
    /// </summary>
    public static bool ShouldPaintMotionFrame(bool visible, bool revealAnimating, bool falling, bool pointerOnRope)
    {
        if (!visible || revealAnimating)
        {
            return false;
        }

        return falling || pointerOnRope;
    }

    /// <param name="slideTickMs">Show/hide clock. Pass <c>RopeMotion.AnimTickMs</c> (8) so the slide stays snappy.</param>
    public static int TickIntervalMs(bool revealAnimating, bool falling, bool pointerOnRope, int slideTickMs) =>
        revealAnimating ? slideTickMs
        : falling || pointerOnRope ? MotionTickMs
        : IdlePollMs;
}
