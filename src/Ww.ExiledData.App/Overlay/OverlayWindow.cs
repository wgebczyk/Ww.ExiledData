using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Core.Identity;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Small transparent, always-on-top, non-focusable, click-through window for capture feedback.
/// Visible over the game in borderless windowed mode (not exclusive fullscreen).
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double SizeDip = 39;
    private const int CornerMarginPx = 24;

    private readonly CaptureIndicator _indicator = new();
    private nint _hwnd;

    public OverlayWindow()
    {
        Title = "Ww.ExiledData overlay";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        Width = Height = SizeDip;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _indicator;
        _indicator.Idle += () => Opacity = 0;
        Opacity = 0;
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWindowStylesCallback(this, OverlayInterop.ClickThroughStyles);
    }

    public OverlayPlacement Placement { get; set; } = OverlayPlacement.NearCursor;

    public void SetDurations(TimeSpan sweep, TimeSpan result)
    {
        _indicator.SweepDuration = sweep;
        _indicator.ResultDuration = result;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
        {
            _hwnd = handle.Handle;
        }
    }

    /// <summary>True when the native window really lets clicks through (used by the self-check).</summary>
    public bool IsClickThrough => OperatingSystem.IsWindows() && _hwnd != 0 && OverlayInterop.IsClickThrough(_hwnd);

    /// <summary>Ctrl+C went down at <paramref name="cursor"/> (physical pixels): move there and start the sweep.</summary>
    public void ShowPress(ScreenPoint cursor)
    {
        MoveNear(new PixelPoint(cursor.X, cursor.Y));
        _indicator.StartSweep();
    }

    /// <summary>Shows an outcome. Without a preceding press (undo/redo hotkeys) it appears at the current spot.</summary>
    public void ShowResult(Feedback feedback, ScreenPoint? cursor = null)
    {
        if (Opacity == 0)
            MoveNear(cursor is { } c ? new PixelPoint(c.X, c.Y) : null);
        _indicator.ShowResult(feedback);
    }

    private void MoveNear(PixelPoint? cursor)
    {
        var scale = DesktopScaling;
        var sizePx = (int)Math.Ceiling(SizeDip * scale);
        var screen = cursor is { } c ? Screens.ScreenFromPoint(c) ?? Screens.Primary : Screens.Primary;
        if (screen is null)
            return;

        var area = screen.Bounds;
        var position = Placement switch
        {
            OverlayPlacement.TopLeft => new PixelPoint(area.X + CornerMarginPx, area.Y + CornerMarginPx),
            OverlayPlacement.TopRight => new PixelPoint(area.Right - sizePx - CornerMarginPx, area.Y + CornerMarginPx),
            OverlayPlacement.BottomLeft => new PixelPoint(area.X + CornerMarginPx, area.Bottom - sizePx - CornerMarginPx),
            OverlayPlacement.BottomRight => new PixelPoint(area.Right - sizePx - CornerMarginPx, area.Bottom - sizePx - CornerMarginPx),
            // Centered on the click point; clicks pass through, so it never gets in the way.
            _ when cursor is { } p => new PixelPoint(p.X - sizePx / 2, p.Y - sizePx / 2),
            _ => new PixelPoint(area.Center.X - sizePx / 2, area.Center.Y - sizePx / 2),
        };

        // Keep fully on screen.
        Position = new PixelPoint(Math.Clamp(position.X, area.X, area.Right - sizePx), Math.Clamp(position.Y, area.Y, area.Bottom - sizePx));
        Opacity = 1;
        if (OperatingSystem.IsWindows() && _hwnd != 0)
            OverlayInterop.BringToTop(_hwnd);
    }
}
