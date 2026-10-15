using PegLine.Core;

namespace PegLine.Core.Tests;

public class PopupZOrderTests
{
    [Fact]
    public void ClosedRope_StaysTopmost()
    {
        var session = new PopupZOrder.Session();

        Assert.False(session.IsOpen);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.Placement);
    }

    [Fact]
    public void OpenMenu_DropsTheRopeAndBlocksLaterTopmost()
    {
        var session = new PopupZOrder.Session();

        // Demote before the menu HWND exists, or Windows builds the menu under the cards.
        Assert.Equal(PopupZOrder.RopeBand.BelowMenu, session.Begin());
        Assert.True(session.IsOpen);
        // The 16ms timer repositions the rope inside TrackPopupMenu. That must not
        // call HWND_TOPMOST, or the photos cover the menu again.
        Assert.Equal(PopupZOrder.RopeBand.Unchanged, session.Placement);

        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.End());
        Assert.False(session.IsOpen);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.Placement);
    }

    [Fact]
    public void NestedMenu_RestoresTopmostOnlyWhenTheLastMenuCloses()
    {
        var session = new PopupZOrder.Session();

        session.Begin();
        Assert.Equal(PopupZOrder.RopeBand.BelowMenu, session.Begin());
        Assert.Equal(PopupZOrder.RopeBand.Unchanged, session.Placement);

        Assert.Equal(PopupZOrder.RopeBand.Unchanged, session.End());
        Assert.True(session.IsOpen);
        Assert.Equal(PopupZOrder.RopeBand.Unchanged, session.Placement);

        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.End());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void MenuMeetsRope_WhenTheCursorIsOnACard()
    {
        Assert.True(PopupZOrder.MenuMeetsRope(cursorX: 400, cursorY: 80, left: 0, top: 0, right: 1920, bottom: 230));
    }

    [Fact]
    public void MenuMeetsRope_NotWhenTheTrayIsFarBelowAShortRope()
    {
        // 1080p work area, rope band at the top, tray cursor at the clock.
        Assert.False(PopupZOrder.MenuMeetsRope(cursorX: 1880, cursorY: 1040, left: 0, top: 0, right: 1920, bottom: 230));
    }

    [Fact]
    public void MenuMeetsRope_WhenATallRopeReachesTheCursor()
    {
        Assert.True(PopupZOrder.MenuMeetsRope(cursorX: 1880, cursorY: 1000, left: 0, top: 0, right: 1920, bottom: 720));
    }

    [Fact]
    public void MenuMeetsRope_NotForAnEmptyBand()
    {
        Assert.False(PopupZOrder.MenuMeetsRope(10, 10, left: 0, top: 0, right: 0, bottom: 0));
    }

    [Fact]
    public void EndWithoutBegin_StaysTopmost()
    {
        var session = new PopupZOrder.Session();

        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.End());
        Assert.Equal(0, session.Depth);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, session.Placement);
    }

    [Fact]
    public void DecideShow_BeforeHwnd_DoesNotTouchZOrder()
    {
        var plan = PopupZOrder.DecideShow(hwndReady: false, hasBeenShown: false, menuOpen: false, loweredForMenu: false, alreadyVisible: false);

        Assert.False(plan.UseShowWindow);
        Assert.False(plan.MaySetWindowPos);
        Assert.Equal(PopupZOrder.RopeBand.Unchanged, plan.Band);
    }

    [Fact]
    public void DecideShow_FirstShow_UsesShowWindowEvenIfAMenuSessionIsOpen()
    {
        var closed = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: false, menuOpen: false, loweredForMenu: false, alreadyVisible: false);
        var open = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: false, menuOpen: true, loweredForMenu: true, alreadyVisible: false);

        Assert.True(closed.UseShowWindow);
        Assert.True(closed.MaySetWindowPos);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, closed.Band);

        Assert.True(open.UseShowWindow);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, open.Band);
    }

    [Fact]
    public void DecideShow_AfterShow_OpenMenuDoesNotRaiseTheRope()
    {
        var lowered = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: true, menuOpen: true, loweredForMenu: true, alreadyVisible: true);
        var holding = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: true, menuOpen: true, loweredForMenu: false, alreadyVisible: false);

        Assert.False(lowered.UseShowWindow);
        Assert.False(lowered.MaySetWindowPos);

        Assert.False(holding.UseShowWindow);
        Assert.True(holding.MaySetWindowPos);
        Assert.Equal(PopupZOrder.RopeBand.Unchanged, holding.Band);

        var visible = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: true, menuOpen: true, loweredForMenu: false, alreadyVisible: true);
        Assert.False(visible.UseShowWindow);
        Assert.False(visible.MaySetWindowPos);
    }

    [Fact]
    public void DecideShow_AfterShow_ClosedMenuPinsTopmost()
    {
        var plan = PopupZOrder.DecideShow(hwndReady: true, hasBeenShown: true, menuOpen: false, loweredForMenu: false, alreadyVisible: true);

        Assert.True(plan.UseShowWindow);
        Assert.True(plan.MaySetWindowPos);
        Assert.Equal(PopupZOrder.RopeBand.Topmost, plan.Band);
    }
}
