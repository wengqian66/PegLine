namespace PegLine.Core;

/// <summary>
/// The rope is one topmost layered window. Peg photos are pixels in that window,
/// so they cover any popup that sits behind it. TrackPopupMenu runs its own loop,
/// and the animation timer keeps calling SetWindowPos(HWND_TOPMOST) from inside
/// that loop, which pulls the cards back above the menu.
/// </summary>
public static class PopupZOrder
{
    /// <summary>
    /// A popup is a few hundred pixels around the cursor. The tray menu sits on the
    /// clock; the peg menu sits on a card. Only the second one (and a very tall rope)
    /// can be covered by the image surface.
    /// </summary>
    public const int MenuReachPx = 400;

    public static bool MenuMeetsRope(int cursorX, int cursorY, int left, int top, int right, int bottom, int reach = MenuReachPx)
    {
        if (right <= left || bottom <= top || reach < 0)
        {
            return false;
        }

        return cursorX >= left - reach
            && cursorX < right + reach
            && cursorY >= top - reach
            && cursorY < bottom + reach;
    }

    public enum RopeBand
    {
        /// <summary>Pin the rope with HWND_TOPMOST.</summary>
        Topmost,

        /// <summary>Leave the topmost band so the menu can stack above the cards.</summary>
        BelowMenu,

        /// <summary>Move or resize only. Do not change z-order.</summary>
        Unchanged,
    }

    public sealed class Session
    {
        public int Depth { get; private set; }

        public bool IsOpen => Depth > 0;

        /// <summary>Where a later reposition of the rope must go. Never topmost while a menu is open.</summary>
        public RopeBand Placement => IsOpen ? RopeBand.Unchanged : RopeBand.Topmost;

        /// <summary>Call before TrackPopupMenu. Apply <see cref="RopeBand.BelowMenu"/> once, before the menu window is created.</summary>
        public RopeBand Begin()
        {
            Depth++;
            return RopeBand.BelowMenu;
        }

        /// <summary>
        /// Call after TrackPopupMenu returns. A nested menu keeps the rope where it is;
        /// only the outermost end puts it back on HWND_TOPMOST.
        /// </summary>
        public RopeBand End()
        {
            if (Depth == 0)
            {
                return RopeBand.Topmost;
            }

            Depth--;
            return Depth == 0 ? RopeBand.Topmost : RopeBand.Unchanged;
        }
    }

    /// <summary>
    /// How to put the rope on screen. The first presentation must call ShowWindow
    /// before any SetWindowPos. SetWindowPos (and skipping ShowWindow) while the
    /// HWND has never been shown reenters the WinUI island before XamlRoot exists.
    /// </summary>
    public readonly record struct ShowPlan(bool UseShowWindow, bool MaySetWindowPos, RopeBand Band);

    public static ShowPlan DecideShow(bool hwndReady, bool hasBeenShown, bool menuOpen, bool loweredForMenu, bool alreadyVisible)
    {
        if (!hwndReady)
        {
            return new ShowPlan(false, false, RopeBand.Unchanged);
        }

        // A menu session must not change the first show. ShowWindow is what
        // realizes the layered HWND; SWP_SHOWWINDOW is not a substitute then.
        if (!hasBeenShown || !menuOpen)
        {
            return new ShowPlan(true, true, RopeBand.Topmost);
        }

        // Visible, or demoted for this menu: leave z-order alone. ShowWindow
        // would insert HWND_TOP and cover the popup.
        if (loweredForMenu || alreadyVisible)
        {
            return new ShowPlan(false, false, RopeBand.Unchanged);
        }

        return new ShowPlan(false, true, RopeBand.Unchanged);
    }
}
