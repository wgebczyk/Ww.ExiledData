using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// A transparent, topmost, borderless window covering one whole screen, for drawing in screen pixel space.
/// Never takes focus and lets the mouse reach the game underneath (a window that takes the mouse ends up behind a
/// borderless game or loses the clicks to it).
/// </summary>
public abstract class ScreenOverlayWindow : Window
{
    private nint _hwnd;

    protected ScreenOverlayWindow(string title)
    {
        Title = title;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWindowStylesCallback(this, OverlayInterop.ClickThroughStyles);
    }

    // Follow the game's focus only while shown: the foreground is polled while anyone listens.
    private bool _followsGameFocus;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || IsVisible == _followsGameFocus)
            return;
        _followsGameFocus = IsVisible;
        if (IsVisible)
            GameFocus.Changed += OnGameFocusChanged;
        else
            GameFocus.Changed -= OnGameFocusChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_followsGameFocus)
            GameFocus.Changed -= OnGameFocusChanged;
        _followsGameFocus = false;
    }

    /// <summary>Top-left of the covered screen, physical pixels.</summary>
    public PixelPoint ScreenOrigin { get; private set; }

    /// <summary>Physical pixels per DIP on the covered screen.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Covers <paramref name="screen"/> and shows the window (without activating it).</summary>
    public void ShowOn(Screen screen)
    {
        ScreenBounds = screen.Bounds;
        ScreenOrigin = screen.Bounds.Position;
        Scale = screen.Scaling;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
        if (!IsVisible)
            Show();
        BringToTop();
        OnScreenChanged();
    }

    /// <summary>The covered screen, physical pixels.</summary>
    public PixelRect ScreenBounds { get; private set; }

    /// <summary>Moves to the screen containing the point (physical pixels) when it is on another one.</summary>
    public void FollowToScreenOf(double x, double y)
    {
        var p = new PixelPoint((int)x, (int)y);
        if (!ScreenBounds.Contains(p) && Screens.ScreenFromPoint(p) is { } screen)
            ShowOn(screen);
    }

    /// <summary>Re-asserts topmost (the game can push us down) without activating.</summary>
    public void BringToTop()
    {
        if (OperatingSystem.IsWindows() && _hwnd != 0)
            OverlayInterop.BringToTop(_hwnd);
    }

    protected virtual void OnScreenChanged()
    {
    }

    /// <summary>The game came to the front or left it (<see cref="GameFocus"/>).</summary>
    protected virtual void OnGameFocusChanged()
    {
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
        {
            _hwnd = handle.Handle;
            BringToTop();
        }
    }

    /// <summary>Screen pixels to DIPs inside this window.</summary>
    public Point ToLocal(double screenX, double screenY) => new((screenX - ScreenOrigin.X) / Scale, (screenY - ScreenOrigin.Y) / Scale);

    /// <summary>DIPs inside this window to screen pixels.</summary>
    public (double X, double Y) ToScreen(Point local) => (ScreenOrigin.X + local.X * Scale, ScreenOrigin.Y + local.Y * Scale);
}
