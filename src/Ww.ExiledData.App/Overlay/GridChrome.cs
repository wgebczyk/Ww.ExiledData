using Avalonia;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Where the parts around a sweep grid sit, in an overlay's DIPs: the title row above the grid (below it when there is
/// no room), the eight resize handles on its corners and edge centers (positioning only), and the frame that wraps
/// both while the game is not in front.
/// </summary>
public sealed record GridChrome(Rect Grid, Rect Title, Rect Frame, IReadOnlyList<(GridHandle Handle, Point Center)> Handles)
{
    public const double TitleHeight = 24;
    /// <summary>Drawn size of a handle; a hovered or dragged one grows to <see cref="HoverSize"/> and shows its arrow.</summary>
    public const double HandleSize = 9;
    public const double HoverSize = 20;
    /// <summary>Size of the square around a handle's center that grabs it: bigger than the drawing, easy to hit.</summary>
    public const double HandleGrip = 20;

    /// <summary>
    /// The chrome around <paramref name="grid"/> with a title <paramref name="titleWidth"/> wide. With handles, the
    /// title keeps clear of the top handles and the frame of the grown ones.
    /// </summary>
    public static GridChrome Of(Rect grid, double titleWidth, bool withHandles)
    {
        var gap = withHandles ? HoverSize / 2 + 2 : 6;
        var title = new Rect(grid.X, grid.Y - gap - TitleHeight, titleWidth, TitleHeight);
        if (title.Y < 0)
            title = title.WithY(grid.Bottom + gap);
        var frame = grid.Union(title).Inflate(withHandles ? HoverSize / 2 + 2 : 6);
        IReadOnlyList<(GridHandle, Point)> handles = withHandles
            ?
            [
                (GridHandle.TopLeft, grid.TopLeft),
                (GridHandle.Top, new Point(grid.Center.X, grid.Top)),
                (GridHandle.TopRight, grid.TopRight),
                (GridHandle.Right, new Point(grid.Right, grid.Center.Y)),
                (GridHandle.BottomRight, grid.BottomRight),
                (GridHandle.Bottom, new Point(grid.Center.X, grid.Bottom)),
                (GridHandle.BottomLeft, grid.BottomLeft),
                (GridHandle.Left, new Point(grid.Left, grid.Center.Y)),
            ]
            : [];
        return new GridChrome(grid, title, frame, handles);
    }

    /// <summary>What a press at <paramref name="p"/> would drag: a handle (they win over the title), the title (move), or nothing.</summary>
    public GridHandle? HitTest(Point p)
    {
        foreach (var (handle, center) in Handles)
        {
            if (Grip(center).Contains(p))
                return handle;
        }
        return Handles.Count > 0 && Title.Contains(p) ? GridHandle.Move : null;
    }

    /// <summary>Everything a press can grab (the title and the handle grips); empty without handles.</summary>
    public IReadOnlyList<Rect> GrabAreas() => Handles.Count == 0 ? [] : [Title, .. Handles.Select(h => Grip(h.Center))];

    private static Rect Grip(Point center) => new(center.X - HandleGrip / 2, center.Y - HandleGrip / 2, HandleGrip, HandleGrip);
}
