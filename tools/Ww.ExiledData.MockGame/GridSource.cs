using Microsoft.Data.Sqlite;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.MockGame;

/// <summary>
/// Finds where the real app expects the inventory: its saved calibration (read-only from the app's database), else the
/// measured default, else a centered grid. Mirrors <c>AppHost.StartingGrid</c>.
/// </summary>
internal static class GridSource
{
    public static readonly GridKind Inventory = GridKind.Presets[0];

    /// <summary>The grid relative to the screen's top-left corner, in physical pixels, and where it came from.</summary>
    public static (GridGeometry Grid, string Source) Find(int screenWidth, int screenHeight)
    {
        try
        {
            if (FindCalibration(screenWidth, screenHeight) is { } saved)
                return saved;
        }
        catch (SqliteException ex)
        {
            return Fallback(screenWidth, screenHeight, $"database unreadable ({ex.Message})");
        }
        return Fallback(screenWidth, screenHeight, "no saved calibration");
    }

    private static (GridGeometry, string) Fallback(int screenWidth, int screenHeight, string why) =>
        GridDefaults.For(Inventory, screenWidth, screenHeight) is { } known
            ? (known, $"measured default ({why})")
            : (GridGeometry.Centered(0, 0, screenWidth, screenHeight, Inventory.Columns, Inventory.Rows), $"centered ({why})");

    private static (GridGeometry, string)? FindCalibration(int screenWidth, int screenHeight)
    {
        var database = Path.Combine(DataDirectory(), "exileddata.db");
        if (!File.Exists(database))
            return null;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT screen_width, screen_height, origin_x, origin_y, cell_size
            FROM grid_calibrations WHERE grid_name = $name AND columns = $cols AND rows = $rows
            ORDER BY (screen_width = $w AND screen_height = $h) DESC, saved_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", Inventory.Name);
        cmd.Parameters.AddWithValue("$cols", Inventory.Columns);
        cmd.Parameters.AddWithValue("$rows", Inventory.Rows);
        cmd.Parameters.AddWithValue("$w", screenWidth);
        cmd.Parameters.AddWithValue("$h", screenHeight);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;

        var grid = new GridGeometry(r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), Inventory.Columns, Inventory.Rows);
        var (w, h) = (r.GetInt32(0), r.GetInt32(1));
        return (grid, w == screenWidth && h == screenHeight ? "saved calibration" : $"saved calibration for {w}x{h}");
    }

    /// <summary>Same rule as the app's <c>AppPaths.Default</c>.</summary>
    public static string DataDirectory() =>
        Environment.GetEnvironmentVariable("WW_EXILEDDATA_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ww.ExiledData");
}
