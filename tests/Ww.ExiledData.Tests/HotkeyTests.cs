using Ww.ExiledData.Capture;

namespace Ww.ExiledData.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Z", KeyModifiers.Ctrl | KeyModifiers.Alt, 'Z')]
    [InlineData("shift + win + 7", KeyModifiers.Shift | KeyModifiers.Win, '7')]
    [InlineData("F1", KeyModifiers.None, 0x70)]
    [InlineData("Control+F24", KeyModifiers.Ctrl, 0x87)]
    public void Parses(string text, KeyModifiers modifiers, int vk) =>
        Assert.Equal(new Hotkey(modifiers, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+Z")]
    [InlineData("Hyper+Z")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Space")]
    public void Rejects_invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+N")]
    [InlineData("Ctrl+Shift+F12")]
    public void Round_trips_to_string(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());
}
