using System.Globalization;
using Ww.ExiledData.Core.Identity;

namespace Ww.ExiledData.Core.Sweeps;

/// <summary>An area kind: a named grid of columns x rows (inventory, stash tab, ...).</summary>
public sealed record GridKind(string Name, int Columns, int Rows)
{
    /// <summary>Recollected sizes; verify in game (PLAN §11).</summary>
    public static IReadOnlyList<GridKind> Presets { get; } =
    [
        new("Inventory", 12, 5),
        new("Stash", 12, 12),
        new("Quad stash", 24, 24),
    ];

    public const int MaxCells = 64;

    public override string ToString() => $"{Name} ({Columns}x{Rows})";

    /// <summary>Formats kinds as <c>Inventory=12x5; Stash=12x12</c> (the settings representation).</summary>
    public static string FormatList(IEnumerable<GridKind> kinds) =>
        string.Join("; ", kinds.Select(k => $"{k.Name}={k.Columns}x{k.Rows}"));

    /// <summary>Parses <c>Name=CxR</c> entries separated by <c>;</c>. Names must be unique.</summary>
    public static bool TryParseList(string? text, out IReadOnlyList<GridKind> kinds, out string? error)
    {
        kinds = [];
        error = null;
        var result = new List<GridKind>();
        foreach (var entry in (text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.LastIndexOf('=');
            var name = eq > 0 ? entry[..eq].Trim() : "";
            var size = eq > 0 ? entry[(eq + 1)..].Split(['x', 'X'], StringSplitOptions.TrimEntries) : [];
            if (name.Length == 0 || size.Length != 2
                || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out var columns)
                || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rows)
                || columns is < 1 or > MaxCells || rows is < 1 or > MaxCells)
            {
                error = $"Grid kind '{entry}' is not valid (expected e.g. Stash=12x12, 1-{MaxCells} per side).";
                return false;
            }
            if (result.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Grid kind '{name}' is listed twice.";
                return false;
            }
            result.Add(new GridKind(name, columns, rows));
        }
        if (result.Count == 0)
        {
            error = "At least one grid kind is needed.";
            return false;
        }
        kinds = result;
        return true;
    }
}

/// <summary>A cell of a grid; zero-based.</summary>
public readonly record struct GridCell(int Column, int Row)
{
    /// <summary>Touching horizontally, vertically or diagonally (a multi-cell item can be hovered in any order).</summary>
    public bool IsAdjacent(GridCell other) =>
        other != this && Math.Abs(Column - other.Column) <= 1 && Math.Abs(Row - other.Row) <= 1;

    /// <summary>One-based, as a person counts cells.</summary>
    public override string ToString() => $"c{Column + 1} r{Row + 1}";
}

/// <summary>What a drag on the grid editor's chrome changes: the title moves the grid, the eight handles resize it.</summary>
public enum GridHandle
{
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// Where a grid sits on screen, in physical pixels (the same space as cursor positions). Cells are square,
/// so the aspect ratio is always columns:rows.
/// </summary>
public readonly record struct GridGeometry(double X, double Y, double CellSize, int Columns, int Rows)
{
    public const double MinCellSize = 4;

    public double Width => CellSize * Columns;
    public double Height => CellSize * Rows;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>The cell under <paramref name="p"/>, or null outside the grid.</summary>
    public GridCell? CellAt(ScreenPoint p)
    {
        if (CellSize <= 0)
            return null;
        var column = (int)Math.Floor((p.X - X) / CellSize);
        var row = (int)Math.Floor((p.Y - Y) / CellSize);
        return column >= 0 && column < Columns && row >= 0 && row < Rows ? new GridCell(column, row) : null;
    }

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public GridGeometry Translate(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Changes the cell size keeping the top-left corner fixed.</summary>
    public GridGeometry Resize(double cellDelta) => this with { CellSize = Math.Max(MinCellSize, CellSize + cellDelta) };

    /// <summary>
    /// The grid spanned from a fixed corner (<paramref name="anchorX"/>, <paramref name="anchorY"/>) towards a dragged
    /// point, aspect locked: the cell size follows whichever axis was dragged further.
    /// </summary>
    public static GridGeometry FromCorners(double anchorX, double anchorY, double toX, double toY, int columns, int rows)
    {
        var cell = Math.Max(MinCellSize, Math.Max(Math.Abs(toX - anchorX) / columns, Math.Abs(toY - anchorY) / rows));
        var x = toX >= anchorX ? anchorX : anchorX - cell * columns;
        var y = toY >= anchorY ? anchorY : anchorY - cell * rows;
        return new GridGeometry(x, y, cell, columns, rows);
    }

    /// <summary>
    /// The grid after dragging <paramref name="handle"/> by (<paramref name="dx"/>, <paramref name="dy"/>) from this one.
    /// <see cref="GridHandle.Move"/> translates; the others resize, aspect locked, keeping the opposite edge or corner
    /// in place (an edge handle also keeps the center of the other axis). A corner follows whichever axis was dragged
    /// further. The cell size never drops below <paramref name="minCellSize"/>.
    /// </summary>
    public GridGeometry Drag(GridHandle handle, double dx, double dy, double minCellSize = MinCellSize)
    {
        if (handle == GridHandle.Move)
            return Translate(dx, dy);
        var (sx, sy) = handle switch
        {
            GridHandle.TopLeft => (-1, -1),
            GridHandle.Top => (0, -1),
            GridHandle.TopRight => (1, -1),
            GridHandle.Right => (1, 0),
            GridHandle.BottomRight => (1, 1),
            GridHandle.Bottom => (0, 1),
            GridHandle.BottomLeft => (-1, 1),
            _ => (-1, 0),
        };
        var byWidth = (Width + sx * dx) / Columns;
        var byHeight = (Height + sy * dy) / Rows;
        var cell = Math.Max(Math.Max(MinCellSize, minCellSize), sx == 0 ? byHeight : sy == 0 ? byWidth : Math.Max(byWidth, byHeight));
        var (width, height) = (cell * Columns, cell * Rows);
        var x = sx > 0 ? X : sx < 0 ? Right - width : X + (Width - width) / 2;
        var y = sy > 0 ? Y : sy < 0 ? Bottom - height : Y + (Height - height) / 2;
        return this with { X = x, Y = y, CellSize = cell };
    }

    /// <summary>A starting grid centered in an area, filling about 60% of it.</summary>
    public static GridGeometry Centered(double areaX, double areaY, double areaWidth, double areaHeight, int columns, int rows)
    {
        var cell = Math.Max(MinCellSize, Math.Min(areaWidth * 0.6 / columns, areaHeight * 0.6 / rows));
        return new GridGeometry(areaX + (areaWidth - cell * columns) / 2, areaY + (areaHeight - cell * rows) / 2, cell, columns, rows);
    }
}
