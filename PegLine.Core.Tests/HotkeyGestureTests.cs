using PegLine.Core;

namespace PegLine.Core.Tests;

public class HotkeyGestureTests
{
    [Fact]
    public void Default_IsCtrlAltT()
    {
        var gesture = HotkeyGesture.Default;

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal('T', gesture.VirtualKey);
        Assert.Equal("Ctrl+Alt+T", gesture.ToStorageString());
        Assert.Equal("Ctrl + Alt + T", gesture.ToDisplayString());
    }

    [Theory]
    [InlineData("ctrl + alt + t")]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("ALT+CTRL+T")]
    public void Parse_AcceptsLooseText(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal("Ctrl+Alt+T", gesture.ToStorageString());
    }

    [Theory]
    [InlineData("T")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Nope")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsIncompleteGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void TryFromVirtualKey_RoundTripsFunctionKey()
    {
        Assert.True(HotkeyGesture.TryParse("Win+Shift+F5", out var parsed));
        Assert.True(HotkeyGesture.TryFromVirtualKey(parsed.Modifiers, parsed.VirtualKey, out var again));
        Assert.Equal(parsed, again);
        Assert.Equal(0x74, parsed.VirtualKey);
    }
}
