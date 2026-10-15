using System.Globalization;

namespace Ww.ExiledData.Capture;

/// <summary>Modifier flags; values match Win32 <c>MOD_*</c>.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public enum HotkeyAction
{
    NewSession,
    Undo,
    Redo,
    /// <summary>Show the grid editor; while it is open, confirm the grid.</summary>
    SweepStart,
    /// <summary>Finish the running sweep (or cancel the grid editor).</summary>
    SweepFinish,
    /// <summary>Toggle "skipped" on the sweep cell under the cursor.</summary>
    SkipCell,
    /// <summary>Mapping: a stash scan of the inventory (counts toward loot).</summary>
    MapStashScan,
    /// <summary>Mapping: a swipe of the inventory (a snapshot; the first of a run is its Initial).</summary>
    MapSwipe,
    /// <summary>Mapping: a final scan (compared with the Initial swipe).</summary>
    MapFinalScan,
    MapStartRun,
    MapEndRun,
}

/// <summary>A global hotkey such as <c>Ctrl+Alt+Z</c>. Keys: A-Z, 0-9, F1-F24.</summary>
public sealed record Hotkey(KeyModifiers Modifiers, int VirtualKey)
{
    public static Hotkey Parse(string text) =>
        TryParse(text, out var hotkey) ? hotkey : throw new FormatException($"Invalid hotkey '{text}'.");

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var m = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Ctrl,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                "WIN" => KeyModifiers.Win,
                _ => (KeyModifiers?)null,
            };
            if (m is null || modifiers.HasFlag(m.Value))
                return false;
            modifiers |= m.Value;
        }

        if (parts.Length == 0 || !TryParseKey(parts[^1].ToUpperInvariant(), out var vk))
            return false;
        hotkey = new Hotkey(modifiers, vk);
        return true;
    }

    private static bool TryParseKey(string key, out int vk)
    {
        vk = 0;
        if (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            vk = key[0];
        else if (key.Length is 2 or 3 && key[0] == 'F'
                 && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
            vk = 0x70 + n - 1; // VK_F1
        return vk != 0;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKey is >= 0x70 and <= 0x87 ? $"F{VirtualKey - 0x70 + 1}" : ((char)VirtualKey).ToString());
        return string.Join('+', parts);
    }
}
