using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Tests;

public class GridTests
{
    private static readonly GridGeometry Stash = new(100, 200, 50, 12, 12);

    [Theory]
    [InlineData(100, 200, 0, 0)]
    [InlineData(149, 249, 0, 0)]
    [InlineData(150, 249, 1, 0)]
    [InlineData(699, 799, 11, 11)]
    public void Cursor_maps_to_cell_by_floor(int x, int y, int column, int row) =>
        Assert.Equal(new GridCell(column, row), Stash.CellAt(new ScreenPoint(x, y)));

    [Theory]
    [InlineData(99, 300)]
    [InlineData(300, 199)]
    [InlineData(700, 300)]
    [InlineData(300, 800)]
    public void Cursor_outside_grid_has_no_cell(int x, int y) =>
        Assert.Null(Stash.CellAt(new ScreenPoint(x, y)));

    [Fact]
    public void Fractional_cell_size_is_supported()
    {
        var grid = new GridGeometry(10.5, 0, 52.7, 12, 5);

        Assert.Equal(new GridCell(11, 4), grid.CellAt(new ScreenPoint((int)(10.5 + 52.7 * 12) - 1, (int)(52.7 * 5) - 1)));
        Assert.Equal(new GridCell(0, 0), grid.CellAt(new ScreenPoint(11, 0)));
    }

    [Fact]
    public void Adjacency_includes_diagonals_but_not_self()
    {
        var c = new GridCell(5, 5);

        Assert.True(c.IsAdjacent(new GridCell(6, 6)));
        Assert.True(c.IsAdjacent(new GridCell(5, 4)));
        Assert.False(c.IsAdjacent(c));
        Assert.False(c.IsAdjacent(new GridCell(7, 5)));
        Assert.Equal("c6 r6", c.ToString());
    }

    [Theory]
    [InlineData(400, 300, 100, 200)] // dragged up-left: the anchor stays the bottom-right corner
    [InlineData(100, 200, 400, 300)]
    public void Spanning_corners_locks_aspect_to_columns_and_rows(double ax, double ay, double bx, double by)
    {
        var grid = GridGeometry.FromCorners(ax, ay, bx, by, 12, 5);

        // dx 300 / 12 = 25, dy 100 / 5 = 20: the larger wins and both sides follow it.
        Assert.Equal(25, grid.CellSize);
        Assert.Equal((300, 125), (grid.Width, grid.Height));
        Assert.True(grid.Contains(Math.Min(ax, bx) + 1, Math.Min(ay, by) + 1));
        Assert.Equal(ax < bx ? ax : ax - 300, grid.X);
        Assert.Equal(ay < by ? ay : ay - 125, grid.Y);
    }

    [Fact]
    public void Resize_keeps_top_left_and_has_a_minimum()
    {
        Assert.Equal(Stash with { CellSize = 51 }, Stash.Resize(1));
        Assert.Equal(GridGeometry.MinCellSize, Stash.Resize(-1000).CellSize);
        Assert.Equal(Stash with { X = 90, Y = 205 }, Stash.Translate(-10, 5));
    }

    [Theory]
    // Stash: 100,200, cell 50, 12 x 12 (600 x 600).
    [InlineData(GridHandle.Move, 10, -5, 110, 195, 50)]
    [InlineData(GridHandle.Right, 120, 999, 100, 140, 60)] // left edge fixed, vertical center kept, dy ignored
    [InlineData(GridHandle.Left, 120, 0, 220, 260, 40)] // right edge fixed
    [InlineData(GridHandle.Bottom, 0, 60, 70, 200, 55)] // top edge fixed, horizontal center kept
    [InlineData(GridHandle.Top, 0, -60, 70, 140, 55)] // bottom edge fixed
    [InlineData(GridHandle.TopLeft, -60, -12, 40, 140, 55)] // bottom-right fixed, the further axis wins
    [InlineData(GridHandle.BottomRight, 12, 60, 100, 200, 55)] // top-left fixed
    public void Dragging_a_handle_moves_or_resizes_aspect_locked(GridHandle handle, double dx, double dy, double x, double y, double cell) =>
        Assert.Equal(Stash with { X = x, Y = y, CellSize = cell }, Stash.Drag(handle, dx, dy));

    [Fact]
    public void Dragging_never_goes_below_the_minimum_cell()
    {
        var shrunk = Stash.Drag(GridHandle.Right, -10_000, 0, minCellSize: 12);
        Assert.Equal(Stash with { Y = 428, CellSize = 12 }, shrunk);
        Assert.Equal(GridGeometry.MinCellSize, Stash.Drag(GridHandle.Left, 10_000, 0, minCellSize: 0).CellSize);
    }

    [Fact]
    public void Centered_fits_the_area()
    {
        var grid = GridGeometry.Centered(1920, 0, 1920, 1080, 12, 12);

        Assert.Equal(1080 * 0.6 / 12, grid.CellSize);
        Assert.Equal(1920 + 1920 / 2.0, grid.X + grid.Width / 2, 6);
    }

    [Fact]
    public void Inventory_default_matches_the_measured_position_at_1440p()
    {
        var grid = GridDefaults.For(GridKind.Presets[0], 2560, 1440)!.Value;

        // Square cells from the measured width (70.33 px) put the bottom within a pixel of the measured 1137.
        Assert.Equal((1693, 786, 2537), ((int)Math.Round(grid.X), (int)Math.Round(grid.Y), (int)Math.Round(grid.Right)));
        Assert.Equal(1137, grid.Bottom, 1.0);
        // From the in-game screenshot: the stack of 40 sits in column 2 of row 1, the "2" stack in row 5.
        Assert.Equal(new GridCell(1, 0), grid.CellAt(new ScreenPoint(1800, 820)));
        Assert.Equal(new GridCell(0, 4), grid.CellAt(new ScreenPoint(1730, 1100)));
    }

    [Fact]
    public void Inventory_default_scales_with_height_and_stays_right_anchored()
    {
        var grid = GridDefaults.For(new GridKind("inventory", 12, 5), 1920, 1080)!.Value;

        Assert.Equal(70.333 * 0.75, grid.CellSize, 2);
        Assert.Equal(1920 - 23 * 0.75, grid.Right, 2);
        Assert.Equal(786 * 0.75, grid.Y, 2);
        Assert.Null(GridDefaults.For(new GridKind("Inventory", 10, 5), 1920, 1080)); // custom size: unknown
        Assert.Null(GridDefaults.For(GridKind.Presets[1], 2560, 1440)); // stash not measured yet
    }

    [Fact]
    public void Grid_kinds_round_trip_through_settings_text()
    {
        Assert.True(GridKind.TryParseList(GridKind.FormatList(GridKind.Presets), out var kinds, out _));
        Assert.Equal(GridKind.Presets, kinds);

        Assert.True(GridKind.TryParseList(" Map tab = 8x8 ;Quad=24X24;", out kinds, out _));
        Assert.Equal([new GridKind("Map tab", 8, 8), new GridKind("Quad", 24, 24)], kinds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Stash")]
    [InlineData("Stash=12")]
    [InlineData("=12x12")]
    [InlineData("Stash=0x12")]
    [InlineData("Stash=12x65")]
    [InlineData("Stash=12x12; stash=24x24")]
    public void Invalid_grid_kinds_are_rejected(string text)
    {
        Assert.False(GridKind.TryParseList(text, out _, out var error));
        Assert.NotNull(error);
    }
}
