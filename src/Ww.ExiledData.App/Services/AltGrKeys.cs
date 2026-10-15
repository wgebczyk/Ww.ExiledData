using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ww.ExiledData.Capture;

namespace Ww.ExiledData.App.Services;

/// <summary>
/// Ctrl+Alt is AltGr on many layouts: a Ctrl+Alt+letter hotkey then stops that character from being typed anywhere
/// while it is registered. Asks every installed keyboard layout what the
/// combination types; without Windows, falls back to the Polish letters known to clash.
/// </summary>
public static partial class AltGrKeys
{
    private static readonly Dictionary<char, string> PolishFallback = new()
    {
        ['A'] = "ą", ['C'] = "ć", ['E'] = "ę", ['L'] = "ł", ['N'] = "ń", ['O'] = "ó", ['S'] = "ś", ['X'] = "ź", ['Z'] = "ż",
    };

    /// <summary>The character the hotkey types as AltGr on an installed layout, or null when it types nothing.</summary>
    public static string? CharacterOf(Hotkey hotkey)
    {
        if (!hotkey.Modifiers.HasFlag(KeyModifiers.Ctrl) || !hotkey.Modifiers.HasFlag(KeyModifiers.Alt) || hotkey.Modifiers.HasFlag(KeyModifiers.Win))
            return null;
        var shift = hotkey.Modifiers.HasFlag(KeyModifiers.Shift);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return FromLayouts(hotkey.VirtualKey, shift);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // fall through to the static list
            }
        }
        return PolishFallback.TryGetValue((char)hotkey.VirtualKey, out var c) ? shift ? c.ToUpperInvariant() : c : null;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? FromLayouts(int vk, bool shift)
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return null;
        var layouts = new nint[count];
        fixed (nint* list = layouts)
            count = GetKeyboardLayoutList(count, list);

        var state = new byte[256];
        state[VK_CONTROL] = state[VK_MENU] = 0x80;
        if (shift)
            state[VK_SHIFT] = 0x80;
        var buffer = stackalloc char[8];
        foreach (var layout in layouts.Take(count))
        {
            var scan = MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);
            fixed (byte* keys = state)
            {
                // Flag 4: do not change the keyboard state (dead keys) of this thread.
                var n = ToUnicodeEx((uint)vk, scan, keys, buffer, 8, 4, layout);
                if (n > 0 && !char.IsControl(buffer[0]))
                    return new string(buffer, 0, n);
            }
        }
        return null;
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const uint MAPVK_VK_TO_VSC = 0;

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetKeyboardLayoutList(int nBuff, nint* lpList);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte* lpKeyState, char* pwszBuff, int cchBuff, uint wFlags, nint dwhkl);
}
