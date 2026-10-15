using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Scan mode: the live sweep grid over the game, tinting cells as they are captured. Click-through, so the game
/// receives the hover and the Ctrl+C. Its title says Ready, or, framed, Click the game while another window is in front.
/// </summary>
public sealed class GridOverlayWindow : ScreenOverlayWindow
{
    private readonly Surface _surface;

    public GridOverlayWindow() : base("Ww.ExiledData sweep grid")
    {
        _surface = new Surface(this);
        Content = _surface;
    }

    public ActiveSweepInfo? Sweep { get; private set; }

    public void Update(ActiveSweepInfo sweep)
    {
        Sweep = sweep;
        _surface.InvalidateVisual();
    }

    protected override void OnScreenChanged() => _surface.InvalidateVisual();

    protected override void OnGameFocusChanged() => _surface.InvalidateVisual();

    private sealed class Surface(GridOverlayWindow owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner.Sweep is not { } sweep)
                return;
            GridPainter.Draw(context, sweep.Grid, owner.ToLocal, sweep.Cells, GridMode.Copying);
            var inFront = GameFocus.IsGameInFront;
            var state = inFront ? GridState.Ready : GridState.NotInGame;
            var detail = $"{sweep.Label} · {(sweep.ItemCount == 1 ? "1 item" : $"{sweep.ItemCount} items")}";
            var chrome = GridPainter.Layout(sweep.Grid, owner.ToLocal, state, detail);
            GridPainter.DrawChrome(context, chrome, state, detail, hot: null, framed: !inFront);
        }
    }
}
