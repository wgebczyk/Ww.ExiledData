using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.Overlay;

/// <summary>Which overlay draws the grid: the editor (positioning it) or the running sweep (copying from it).</summary>
internal enum GridMode
{
    Positioning,
    Copying,
}

/// <summary>What the grid's title row says: positioning, ready to copy, or the game is not in front (Ctrl+C goes elsewhere).</summary>
public enum GridState
{
    Positioning,
    Ready,
    NotInGame,
}

/// <summary>Draws a sweep grid and its cell states. Each state differs by shape as well as color.</summary>
internal static class GridPainter
{
    public static readonly Color PositioningColor = Color.FromRgb(0xF0, 0xB4, 0x3C);
    private static readonly Color CopyingColor = Color.FromRgb(0x2E, 0x9E, 0x4F);

    // Light lines over dark shadows, so the grid shows on both the game's dark panels and light backgrounds.
    // Positioning draws them orange, so it is never mistaken for a sweep that takes copies.
    private static readonly IPen FrameShadow = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00)), 4);
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 2);
    private static readonly IPen PositioningFrame = new Pen(new SolidColorBrush(PositioningColor), 2);
    private static readonly IPen LinesShadow = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)), 3);
    private static readonly IPen Lines = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen PositioningLines = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xF0, 0xB4, 0x3C)), 1);
    // Title row: one indicator in the state's color. The frame shows only while the game is not in front.
    private static readonly IBrush PositioningTag = new SolidColorBrush(PositioningColor);
    private static readonly IBrush CopyingTag = new SolidColorBrush(CopyingColor);
    private static readonly IBrush NotInGameTag = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush PositioningTagText = new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x08));
    private static readonly IPen NotInGameFrame = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x4A, 0x3A)), 3);
    private static readonly IPen HotTitleRim = new Pen(Brushes.White, 1.5);
    private static readonly IPen HandleRim = new Pen(Brushes.Black, 1);
    private static readonly IBrush CapturedFill = new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0x9E, 0x4F));
    private static readonly IBrush CoveredFill = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0x9E, 0x4F));
    private static readonly IPen CoveredHatch = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x2E, 0x9E, 0x4F)), 2);
    private static readonly IPen FailedOutline = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xE0, 0x4A, 0x3A)), 3);
    private static readonly IBrush SkippedFill = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
    private static readonly IPen SkippedCross = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xC0, 0xC0, 0xC0)), 1.5);
    private static readonly Typeface LabelFace = new("Segoe UI, Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface DetailFace = new(new FontFamily("avares://Ww.ExiledData/Assets/Fonts#IBM Plex Mono"));

    /// <summary>
    /// <paramref name="grid"/> is in screen pixels; <paramref name="toLocal"/> maps screen pixels to the drawing's DIPs.
    /// </summary>
    public static void Draw(DrawingContext context, GridGeometry grid, Func<double, double, Point> toLocal, IReadOnlyList<SweepCell> cells,
        GridMode mode)
    {
        var (frame, lines) = mode == GridMode.Positioning ? (PositioningFrame, PositioningLines) : (Frame, Lines);
        foreach (var cell in cells)
        {
            var rect = CellRect(grid, cell.Cell, toLocal);
            switch (cell.State)
            {
                case CellState.Captured:
                    context.FillRectangle(CapturedFill, rect);
                    break;
                case CellState.Covered:
                    context.FillRectangle(CoveredFill, rect);
                    context.DrawLine(CoveredHatch, rect.BottomLeft, rect.TopRight);
                    break;
                case CellState.Failed:
                    context.DrawRectangle(null, FailedOutline, rect.Deflate(2.5));
                    break;
                case CellState.Skipped:
                    context.FillRectangle(SkippedFill, rect);
                    context.DrawLine(SkippedCross, rect.TopLeft, rect.BottomRight);
                    context.DrawLine(SkippedCross, rect.BottomLeft, rect.TopRight);
                    break;
            }
        }

        var topLeft = toLocal(grid.X, grid.Y);
        var bottomRight = toLocal(grid.Right, grid.Bottom);
        foreach (var pen in new[] { LinesShadow, lines })
        {
            for (var c = 1; c < grid.Columns; c++)
            {
                var x = toLocal(grid.X + c * grid.CellSize, grid.Y).X;
                context.DrawLine(pen, new Point(x, topLeft.Y), new Point(x, bottomRight.Y));
            }
            for (var r = 1; r < grid.Rows; r++)
            {
                var y = toLocal(grid.X, grid.Y + r * grid.CellSize).Y;
                context.DrawLine(pen, new Point(topLeft.X, y), new Point(bottomRight.X, y));
            }
        }
        context.DrawRectangle(null, FrameShadow, new Rect(topLeft, bottomRight));
        context.DrawRectangle(null, frame, new Rect(topLeft, bottomRight));
    }

    /// <summary>
    /// The chrome around <paramref name="grid"/> for a title row saying <paramref name="state"/> and
    /// <paramref name="detail"/>; handles while positioning.
    /// </summary>
    public static GridChrome Layout(GridGeometry grid, Func<double, double, Point> toLocal, GridState state, string detail)
    {
        var (word, detailText) = (Word(state, Brushes.White), Detail(detail, Brushes.White));
        var width = TitlePadding + TitleIcon + TitleGap + word.Width + (detail.Length > 0 ? TitleGap + 2 + detailText.Width : 0) + TitlePadding;
        return GridChrome.Of(GridRect(grid, toLocal), Math.Ceiling(width), withHandles: state == GridState.Positioning);
    }

    /// <summary>
    /// Draws the title row, the handles (<paramref name="hot"/> grown, with its arrow) and, when
    /// <paramref name="framed"/>, the frame that says keys and Ctrl+C go elsewhere.
    /// </summary>
    public static void DrawChrome(DrawingContext context, GridChrome chrome, GridState state, string detail, GridHandle? hot, bool framed)
    {
        if (framed)
        {
            context.DrawRectangle(null, FrameShadow, chrome.Frame, 4, 4);
            context.DrawRectangle(null, NotInGameFrame, chrome.Frame, 4, 4);
        }

        var (fill, ink, icon) = state switch
        {
            GridState.Positioning => (PositioningTag, PositioningTagText, OverlayIcons.Move),
            GridState.Ready => (CopyingTag, Brushes.White, OverlayIcons.Copy),
            _ => (NotInGameTag, Brushes.White, OverlayIcons.Pointer),
        };
        var title = chrome.Title;
        context.DrawRectangle(fill, hot == GridHandle.Move ? HotTitleRim : null, title, 4, 4);
        var x = title.X + TitlePadding;
        OverlayIcons.Draw(context, icon, new Point(x + TitleIcon / 2, title.Center.Y), TitleIcon, 2.2, ink);
        x += TitleIcon + TitleGap;
        var word = Word(state, ink);
        context.DrawText(word, new Point(x, title.Center.Y - word.Height / 2));
        if (detail.Length > 0)
        {
            var text = Detail(detail, ink);
            using (context.PushOpacity(0.85))
                context.DrawText(text, new Point(x + word.Width + TitleGap + 2, title.Center.Y - text.Height / 2));
        }

        foreach (var (handle, center) in chrome.Handles)
        {
            if (handle == hot)
            {
                context.DrawEllipse(PositioningTag, HandleRim, center, GridChrome.HoverSize / 2, GridChrome.HoverSize / 2);
                OverlayIcons.Draw(context, OverlayIcons.ResizeArrow(handle), center, 14, 2.6, PositioningTagText);
            }
            else
            {
                var half = GridChrome.HandleSize / 2;
                context.DrawRectangle(PositioningTag, HandleRim, new Rect(center.X - half, center.Y - half, GridChrome.HandleSize, GridChrome.HandleSize));
            }
        }
    }

    private const double TitlePadding = 8;
    private const double TitleIcon = 16;
    private const double TitleGap = 6;

    private static FormattedText Word(GridState state, IBrush brush) => new(state switch
    {
        GridState.Positioning => "Position",
        GridState.Ready => "Ready",
        _ => "Click the game",
    }, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 12, brush);

    private static FormattedText Detail(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, DetailFace, 11, brush);

    private static Rect GridRect(GridGeometry grid, Func<double, double, Point> toLocal) => new(toLocal(grid.X, grid.Y), toLocal(grid.Right, grid.Bottom));

    private static Rect CellRect(GridGeometry grid, GridCell cell, Func<double, double, Point> toLocal)
    {
        var a = toLocal(grid.X + cell.Column * grid.CellSize, grid.Y + cell.Row * grid.CellSize);
        var b = toLocal(grid.X + (cell.Column + 1) * grid.CellSize, grid.Y + (cell.Row + 1) * grid.CellSize);
        return new Rect(a, b);
    }
}
