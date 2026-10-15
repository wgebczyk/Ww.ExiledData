using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ww.ExiledData.App.Controls;

/// <summary>Small proportional drawing of a sweep area's grid, long side <see cref="LongSide"/>.</summary>
public sealed class GridPreview : Control
{
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<GridPreview, int>(nameof(Columns));
    public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<GridPreview, int>(nameof(Rows));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<GridPreview, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<GridPreview, IBrush?>(nameof(Fill));

    private const double LongSide = 52;
    private const double ShortMin = 8;

    static GridPreview() => AffectsRender<GridPreview>(ColumnsProperty, RowsProperty, LineBrushProperty, FillProperty);

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Columns <= 0 || Rows <= 0)
            return;
        var (w, h) = Columns >= Rows
            ? (LongSide, Math.Max(ShortMin, Math.Round(LongSide * Rows / Columns)))
            : (Math.Max(ShortMin, Math.Round(LongSide * Columns / Rows)), LongSide);
        var rect = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h).Deflate(0.5);
        var pen = new Pen(LineBrush, 1);
        context.DrawRectangle(Fill, pen, rect);

        // Dense grids (quad stash) would turn into a solid block: draw every n-th line.
        var everyCol = Math.Max(1, (int)Math.Ceiling(Columns / (rect.Width / 2.5)));
        var everyRow = Math.Max(1, (int)Math.Ceiling(Rows / (rect.Height / 2.5)));
        for (var c = everyCol; c < Columns; c += everyCol)
        {
            var x = Math.Round(rect.X + rect.Width * c / Columns) + 0.5;
            context.DrawLine(pen, new Point(x, rect.Y), new Point(x, rect.Bottom));
        }
        for (var r = everyRow; r < Rows; r += everyRow)
        {
            var y = Math.Round(rect.Y + rect.Height * r / Rows) + 0.5;
            context.DrawLine(pen, new Point(rect.X, y), new Point(rect.Right, y));
        }
    }
}
