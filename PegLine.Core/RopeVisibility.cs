namespace PegLine.Core;

/// <summary>
/// Decides when the top rope may retract. A show — including the hotkey — arms the
/// idle timer and then hides after the linger once the pointer has left. The hotkey
/// follows what is on screen, so an auto-hidden rope is shown again instead of
/// turning the switch off. Leaving the timer unarmed makes the next tick treat the
/// rope as already idle and slide it back to the top edge.
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

    /// <summary>
    /// Hide after the linger. A slide that is still opening must not retract, or the
    /// rope parks flush with the top edge. Already off screen is not another hide.
    /// </summary>
    public static bool ShouldAutoHide(bool opening, bool visuallyVisible, long now, long idleHideAt)
    {
        if (opening || !visuallyVisible)
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
