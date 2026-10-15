using Avalonia.Threading;
using Ww.ExiledData.App.Overlay;

namespace Ww.ExiledData.App.Services;

/// <summary>
/// Whether the game is the foreground window, i.e. whether a Ctrl+C would be captured (the same title test capture
/// uses). Polled four times a second, only while something listens to <see cref="Changed"/>: the sweep grid overlays,
/// which frame the grid when the game is not in front.
/// </summary>
public static class GameFocus
{
    private static readonly DispatcherTimer Timer = CreateTimer();
    private static Action? s_changed;

    /// <summary>The game window title setting; null or empty means any window counts as the game.</summary>
    public static string? GameWindowTitle { get; set; }

    public static bool IsGameInFront { get; private set; } = true;

    public static event Action? Changed
    {
        add
        {
            s_changed += value;
            Update();
            Timer.Start();
        }
        remove
        {
            s_changed -= value;
            if (s_changed is null)
                Timer.Stop();
        }
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Update();
        return timer;
    }

    private static void Update()
    {
        var inFront = !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(GameWindowTitle)
                      || OverlayInterop.ForegroundTitle() is { } title && title.Contains(GameWindowTitle, StringComparison.OrdinalIgnoreCase);
        if (inFront == IsGameInFront)
            return;
        IsGameInFront = inFront;
        s_changed?.Invoke();
    }
}
