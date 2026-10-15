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
}
