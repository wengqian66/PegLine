namespace PegLine.Core;

/// <summary>
/// Decides when the top rope may retract. The hotkey follows what is on screen:
/// a show has to arm the idle timer, or the next tick treats the rope as already idle
/// and slides it back to the top edge.
/// </summary>
public static class RopeVisibility
{
    public const int ShowLingerMs = 4200;

    /// <summary>
    /// Hotkey / tray toggle. The saved "allowed" flag can stay true while the rope is
    /// auto-hidden, so a press while nothing is on screen shows the rope instead of
    /// turning the switch off. With nothing to hang and the switch already on, the
    /// press turns it off.
    /// </summary>
    public static bool ToggleShouldShow(bool visuallyShown, bool allowed, bool canShow)
    {
        if (visuallyShown)
        {
            return false;
        }

        if (!canShow && allowed)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldAutoHide(bool heldOpen, bool opening, bool visuallyVisible, long now, long idleHideAt)
    {
        if (heldOpen || opening || !visuallyVisible)
        {
            return false;
        }

        return now >= idleHideAt;
    }

    /// <summary>
    /// Push the idle deadline forward. Never pull it earlier — a longer linger
    /// (a falling peg, a fresh screenshot) must survive a later show.
    /// </summary>
    public static long ExtendIdle(long idleHideAt, long now, int lingerMs)
    {
        var until = now + lingerMs;
        return until > idleHideAt ? until : idleHideAt;
    }
}
