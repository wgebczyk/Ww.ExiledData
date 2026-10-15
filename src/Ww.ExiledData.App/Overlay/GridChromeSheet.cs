using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// The sweep grid's three states side by side, drawn by the real painter over a stand-in for game imagery: positioning
/// (handles, one hovered), ready to copy (some cells done), and the game not in front (framed). For <c>--screenshot</c>.
/// </summary>
public sealed class GridChromeSheet : Window
{
    public GridChromeSheet()
    {
        Title = "Sweep grid";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Content = new Sheet { Width = 1180, Height = 300 };
    }

    private sealed class Sheet : Control
    {
        private static readonly IBrush Scene = new SolidColorBrush(Color.FromRgb(0x2A, 0x26, 0x22));
        private static readonly IBrush PositioningDim = new SolidColorBrush(Color.FromArgb(0x38, 0x3C, 0x22, 0x00));
        private static readonly Func<double, double, Point> Identity = (x, y) => new Point(x, y);

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Scene, new Rect(Bounds.Size));
            var sweep = Guid.Empty;
            SweepCell Cell(int column, int row, CellState state) => new(sweep, new GridCell(column, row), state, null);
            IReadOnlyList<SweepCell> done =
            [
                Cell(0, 0, CellState.Captured), Cell(1, 0, CellState.Captured), Cell(2, 0, CellState.Captured),
                Cell(0, 1, CellState.Captured), Cell(1, 1, CellState.Covered), Cell(3, 0, CellState.Failed), Cell(4, 2, CellState.Skipped),
            ];

            // 1 · Positioning, the right handle hovered.
            context.FillRectangle(PositioningDim, new Rect(0, 0, 380, Bounds.Height));
            var editing = new GridGeometry(50, 110, 32, 9, 4);
            GridPainter.Draw(context, editing, Identity, [], GridMode.Positioning);
            var detail = $"Inventory {editing.Columns}×{editing.Rows}";
            GridPainter.DrawChrome(context, GridPainter.Layout(editing, Identity, GridState.Positioning, detail), GridState.Positioning, detail,
                GridHandle.Right, framed: false);

            // 2 · Ready: the game is in front.
            var ready = editing.Translate(390, 0);
            GridPainter.Draw(context, ready, Identity, done, GridMode.Copying);
            detail = "Inventory 2 · 5 items";
            GridPainter.DrawChrome(context, GridPainter.Layout(ready, Identity, GridState.Ready, detail), GridState.Ready, detail, null, framed: false);

            // 3 · Another window is in front: Ctrl+C would not reach the game.
            var away = editing.Translate(780, 0);
            GridPainter.Draw(context, away, Identity, done, GridMode.Copying);
            GridPainter.DrawChrome(context, GridPainter.Layout(away, Identity, GridState.NotInGame, detail), GridState.NotInGame, detail, null,
                framed: true);
        }
    }
}
