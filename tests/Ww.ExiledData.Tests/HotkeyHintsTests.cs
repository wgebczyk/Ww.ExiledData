using Ww.ExiledData.App;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.Tests;

/// <summary>Key badges: only the key under the common prefix, the whole shortcut otherwise.</summary>
public class HotkeyHintsTests
{
    [Fact]
    public void Badges_show_only_the_key_for_the_common_prefix()
    {
        var hints = new HotkeyHints(new AppSettings());

        Assert.Equal(["Ctrl", "Shift"], hints.PrefixKeys);
        Assert.Equal(new HotkeyHint("X", "Ctrl+Shift+X"), hints.StashScan);
        Assert.Equal(new HotkeyHint("T", "Ctrl+Shift+T"), hints.EndRun);
    }

    [Fact]
    public void A_hotkey_with_other_modifiers_shows_the_whole_shortcut()
    {
        var hints = new HotkeyHints(new AppSettings { HotkeyMapFinalScan = "Alt+F5" });

        Assert.Equal(new HotkeyHint("Alt+F5", "Alt+F5"), hints.FinalScan);
        Assert.Equal("Z", hints.InitialScan.Badge);
    }

    [Fact]
    public void Follows_changed_settings()
    {
        var hints = new HotkeyHints(new AppSettings());

        hints.Update(new AppSettings { HotkeyMapSwipe = "Ctrl+Shift+B" });

        Assert.Equal("B", hints.Swipe.Badge);
    }
}
