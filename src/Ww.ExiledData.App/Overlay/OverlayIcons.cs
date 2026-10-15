using Avalonia;
using Avalonia.Media;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Line icons for the overlays over the game, on a 24 x 24 grid drawn with round strokes (the app's icon style; undo,
/// redo and play match its buttons). Fixed geometry, so they look the same whatever fonts are installed.
/// </summary>
internal static class OverlayIcons
{
    public static readonly Geometry Check = Parse("M5 12.5L10 17.5L19 7");
    public static readonly Geometry DoubleCheck = Parse("M2 12.5L7 17.5 M7 12.5L12 17.5L22 7 M12 12.5L17 7");
    public static readonly Geometry Copy = Parse("M9 9H19V19H9Z M15 9V5H5V15H9");
    public static readonly Geometry Stack = Parse("M12 3L21 8L12 13L3 8Z M3 12.5L12 17.5L21 12.5 M3 17L12 22L21 17");
    public static readonly Geometry Link = Parse("M9.2 14.8L14.8 9.2 M7.1 11.3L3.6 14.8A4.2 4.2 0 0 0 9.2 20.4L12.7 16.7 M11.3 7.1L14.8 3.6A4.2 4.2 0 0 1 20.4 9.2L16.7 12.7");
    public static readonly Geometry Info = Parse("M12 11V18 M12 6.5V6.6");
    public static readonly Geometry Flag = Parse("M5 21V4 M5 4H17L15 8L17 12H5");
    public static readonly Geometry Stop = Parse("M7 5H17A2 2 0 0 1 19 7V17A2 2 0 0 1 17 19H7A2 2 0 0 1 5 17V7A2 2 0 0 1 7 5Z");
    public static readonly Geometry Undo = Parse("M9 14L4 9L9 4 M4 9H14A6 6 0 0 1 14 21H11");
    public static readonly Geometry Redo = Parse("M15 14L20 9L15 4 M20 9H10A6 6 0 0 0 10 21H13");
    public static readonly Geometry Play = Parse("M8 5.7V18.3A1 1 0 0 0 9.5 19.2L19.3 12.9A1 1 0 0 0 19.3 11.1L9.5 4.8A1 1 0 0 0 8 5.7Z");
    public static readonly Geometry Grid = Parse("M4 4H10V10H4Z M14 4H20V10H14Z M4 14H10V20H4Z M14 14H20V20H14Z");
    public static readonly Geometry Ban = Parse("M3 12A9 9 0 1 0 21 12A9 9 0 1 0 3 12Z M5.6 5.6L18.4 18.4");
    public static readonly Geometry Skip = Parse("M5 5V19L15 12Z M19 5V19");
    public static readonly Geometry Cross = Parse("M18 6L6 18 M6 6L18 18");
    /// <summary>A mouse pointer: "click here" (into the game).</summary>
    public static readonly Geometry Pointer = Parse("M6 3V19L10.5 14.5L13.5 21L16 20L13 13.5H19Z");
    public static readonly Geometry Move = Parse("M12 3V21 M3 12H21 M9 6L12 3L15 6 M9 18L12 21L15 18 M6 9L3 12L6 15 M18 9L21 12L18 15");

    private static readonly Geometry ResizeHorizontal = Parse("M3 12H21 M7 8L3 12L7 16 M17 8L21 12L17 16");
    private static readonly Geometry ResizeVertical = Parse("M12 3V21 M8 7L12 3L16 7 M8 17L12 21L16 17");
    private static readonly Geometry ResizeDiagonal = Parse("M5 5L19 19 M5 11V5H11 M19 13V19H13");
    private static readonly Geometry ResizeAntiDiagonal = Parse("M19 5L5 19 M13 5H19V11 M5 13V19H11");

    /// <summary>The double arrow shown on a hovered grid handle: the direction it resizes in.</summary>
    public static Geometry ResizeArrow(GridHandle handle) => handle switch
    {
        GridHandle.Left or GridHandle.Right => ResizeHorizontal,
        GridHandle.Top or GridHandle.Bottom => ResizeVertical,
        GridHandle.TopLeft or GridHandle.BottomRight => ResizeDiagonal,
        GridHandle.TopRight or GridHandle.BottomLeft => ResizeAntiDiagonal,
        _ => Move,
    };

    /// <summary>
    /// Draws <paramref name="icon"/> centered on <paramref name="center"/>, <paramref name="box"/> wide, with a
    /// <paramref name="halo"/> outline under the stroke when one is given. Stroke widths are on the 24 x 24 grid.
    /// </summary>
    public static void Draw(DrawingContext context, Geometry icon, Point center, double box, double strokeWidth, IBrush stroke, IBrush? halo = null)
    {
        var scale = box / 24;
        var transform = Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(transform))
        {
            if (halo is not null)
                context.DrawGeometry(null, RoundPen(halo, strokeWidth + 2.6), icon);
            context.DrawGeometry(null, RoundPen(stroke, strokeWidth), icon);
        }
    }

    public static Pen RoundPen(IBrush brush, double thickness) =>
        new(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
