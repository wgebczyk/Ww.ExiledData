using CommunityToolkit.Mvvm.ComponentModel;
using Ww.ExiledData.Capture;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// One hotkey for a key badge: <see cref="Badge"/> is only the key when the hotkey uses the common prefix
/// (<c>X</c> for Ctrl+Shift+X), else the whole shortcut; <see cref="Full"/> is the whole shortcut, for the tooltip.
/// </summary>
public sealed record HotkeyHint(string Badge, string Full)
{
    public static readonly HotkeyHint None = new("", "");
}

/// <summary>The mapping hotkeys as key badges for the buttons they press; follows the saved settings.</summary>
public sealed partial class HotkeyHints : ObservableObject
{
    public HotkeyHints(AppSettings settings) => Update(settings);

    /// <summary>The modifiers most mapping hotkeys share, as keys (<c>Ctrl</c>, <c>Shift</c>); shown once next to the badges.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> PrefixKeys { get; set; }

    [ObservableProperty]
    public partial HotkeyHint InitialScan { get; set; }

    [ObservableProperty]
    public partial HotkeyHint StashScan { get; set; }

    [ObservableProperty]
    public partial HotkeyHint Swipe { get; set; }

    [ObservableProperty]
    public partial HotkeyHint FinalScan { get; set; }

    [ObservableProperty]
    public partial HotkeyHint StartRun { get; set; }

    [ObservableProperty]
    public partial HotkeyHint EndRun { get; set; }

    public void Update(AppSettings settings)
    {
        string[] texts = [settings.HotkeyMapInitialScan, settings.HotkeyMapStashScan, settings.HotkeyMapSwipe,
            settings.HotkeyMapFinalScan, settings.HotkeyMapStartRun, settings.HotkeyMapEndRun];
        var hotkeys = texts.Select(t => Hotkey.TryParse(t, out var k) ? k : null).ToArray();
        var prefix = hotkeys.OfType<Hotkey>().Where(k => k.Modifiers != KeyModifiers.None)
            .GroupBy(k => k.Modifiers).OrderByDescending(g => g.Count()).Select(g => (KeyModifiers?)g.Key).FirstOrDefault();

        PrefixKeys = prefix is { } p ? new Hotkey(p, 'A').ToString().Split('+')[..^1] : [];
        HotkeyHint Hint(Hotkey? k) => k is null ? HotkeyHint.None
            : new HotkeyHint(k.Modifiers == prefix ? k.ToString().Split('+')[^1] : k.ToString(), k.ToString());
        InitialScan = Hint(hotkeys[0]);
        StashScan = Hint(hotkeys[1]);
        Swipe = Hint(hotkeys[2]);
        FinalScan = Hint(hotkeys[3]);
        StartRun = Hint(hotkeys[4]);
        EndRun = Hint(hotkeys[5]);
    }
}
