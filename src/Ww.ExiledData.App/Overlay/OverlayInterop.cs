using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ww.ExiledData.App.Overlay;

[SupportedOSPlatform("windows")]
internal static partial class OverlayInterop
{
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint ClickThrough = WS_EX_TRANSPARENT | WS_EX_LAYERED;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Window-creation styles: mouse clicks pass through (transparent + layered), the window never takes focus
    /// and stays off Alt+Tab. Must be applied at creation through Avalonia's style callback: adding them later
    /// (with SetLayeredWindowAttributes) stops the DirectComposition content from being drawn.
    /// </summary>
    public static (uint Style, uint ExStyle) ClickThroughStyles(uint style, uint exStyle) =>
        (style, exStyle | ClickThrough | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    /// <summary>Re-asserts topmost (the game can push us down) without activating.</summary>
    public static void BringToTop(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public static bool IsClickThrough(nint hwnd) =>
        ((uint)GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & ClickThrough) == ClickThrough;

    /// <summary>Title of the foreground window, or null when there is none or it has no title.</summary>
    public static unsafe string? ForegroundTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return null;
        var buffer = stackalloc char[256];
        var length = GetWindowTextW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>The cursor in physical pixels (the same space as captures and grids).</summary>
    public static (int X, int Y)? CursorPosition() => GetPhysicalCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>
    /// Center (physical pixels) of the first visible, non-minimized top-level window whose title contains
    /// <paramref name="titlePart"/>, e.g. the game; null when there is none.
    /// </summary>
    public static unsafe (int X, int Y)? FindWindowCenter(string titlePart)
    {
        var buffer = stackalloc char[256];
        nint hwnd = 0;
        while ((hwnd = FindWindowExW(0, hwnd, null, null)) != 0)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                continue;
            var length = GetWindowTextW(hwnd, buffer, 256);
            if (length > 0 && new string(buffer, 0, length).Contains(titlePart, StringComparison.OrdinalIgnoreCase)
                && GetWindowRect(hwnd, out var r))
                return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        }
        return null;
    }

    private struct POINT
    {
        public int X;
        public int Y;
    }

    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicalCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
