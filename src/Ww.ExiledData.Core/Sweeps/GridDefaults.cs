namespace Ww.ExiledData.Core.Sweeps;

/// <summary>
/// Measured in-game positions of known grids, used as the starting grid before the user has saved a calibration.
/// The game scales its UI with the screen height and anchors these panels to the right edge.
/// </summary>
public static class GridDefaults
{
    private sealed record Measured(int Columns, int Rows, double ScreenHeight, double RightMargin, double Top, double CellSize);

    private static readonly Dictionary<string, Measured> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // 2560x1440, 100% UI scale: inventory cells span (1693, 786) to (2537, 1137).
        ["Inventory"] = new(12, 5, 1440, 2560 - 2537, 786, (2537 - 1693) / 12.0),
    };

    /// <summary>
    /// The default grid for <paramref name="kind"/> on a screen of the given size, relative to the screen's top-left
    /// corner, or null when its position is not known.
    /// </summary>
    public static GridGeometry? For(GridKind kind, int screenWidth, int screenHeight)
    {
        if (!Known.TryGetValue(kind.Name, out var m) || m.Columns != kind.Columns || m.Rows != kind.Rows)
            return null;

        var scale = screenHeight / m.ScreenHeight;
        var cell = m.CellSize * scale;
        var x = screenWidth - m.RightMargin * scale - cell * kind.Columns;
        return x < 0 ? null : new GridGeometry(x, m.Top * scale, cell, kind.Columns, kind.Rows);
    }
}
