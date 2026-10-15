using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.MockGame;

/// <summary>
/// The mock game window. Its title contains "Path of Exile 2" so the app accepts its copies. The inventory sits at
/// the screen position the app has calibrated, so sweeps line up without re-aligning.
/// </summary>
internal sealed class MockGameWindow : Window
{
    public const int TabletSlots = 4;

    private readonly Random _rng = new();
    private readonly Scene _scene = new();
    private readonly Canvas _overlay = new();
    private readonly Button _randomizeInventory = new() { Content = "Randomize inventory" };
    private readonly Button _randomizeDevice = new() { Content = "Randomize waystone + tablets" };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 12 };
    private GridGeometry _screenGrid;

    public MockGameWindow()
    {
        Title = "Path of Exile 2 (mock)";
        WindowState = WindowState.Maximized;
        Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x06));

        _randomizeInventory.Click += (_, _) => RandomizeInventory();
        _randomizeDevice.Click += (_, _) => RandomizeDevice();
        // Buttons must not keep focus: Ctrl+C goes to the window, and Space/Enter must not re-randomize by accident.
        _randomizeInventory.Focusable = false;
        _randomizeDevice.Focusable = false;
        _overlay.Children.AddRange([_randomizeInventory, _randomizeDevice, _status]);
        Canvas.SetLeft(_status, 12);
        Canvas.SetTop(_status, 8);

        Content = new Panel { Children = { _scene, _overlay } };

        RandomizeInventory();
        RandomizeDevice();

        Opened += (_, _) => PlaceGrid();
        PositionChanged += (_, _) => PlaceGrid();
        ScalingChanged += (_, _) => PlaceGrid();
        SizeChanged += (_, _) => PlaceGrid();
    }

    private void RandomizeInventory()
    {
        _scene.Inventory = MockItems.RandomInventory(_rng, GridSource.Inventory.Columns, GridSource.Inventory.Rows);
        _scene.InvalidateVisual();
    }

    private void RandomizeDevice()
    {
        _scene.Waystone = MockItems.RandomWaystone(_rng);
        // Normally up to 3 tablets, sometimes a 4th; occasionally a slot stays empty.
        var count = _rng.NextDouble() < 0.25 ? TabletSlots : _rng.Next(0, 4);
        _scene.Tablets = Enumerable.Range(0, TabletSlots).Select(i => i < count ? MockItems.RandomTablet(_rng) : null).ToArray();
        _scene.InvalidateVisual();
    }

    /// <summary>Converts the calibrated screen grid into this window's client coordinates.</summary>
    private void PlaceGrid()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
            return;
        var bounds = screen.Bounds;
        var (grid, source) = GridSource.Find(bounds.Width, bounds.Height);
        _screenGrid = grid.Translate(bounds.X, bounds.Y);

        var topLeft = this.PointToClient(new PixelPoint((int)Math.Round(_screenGrid.X), (int)Math.Round(_screenGrid.Y)));
        var cell = _screenGrid.CellSize / RenderScaling;
        _scene.Grid = new Rect(topLeft.X, topLeft.Y, cell * _screenGrid.Columns, cell * _screenGrid.Rows);
        _scene.CellSize = cell;

        Canvas.SetLeft(_randomizeInventory, topLeft.X);
        Canvas.SetTop(_randomizeInventory, topLeft.Y - 44);

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _scene.DeviceCenter = center;
        Canvas.SetLeft(_randomizeDevice, center.X - 110);
        Canvas.SetTop(_randomizeDevice, center.Y - Scene.DeviceTop(cell) - 64);

        _status.Text = $"Inventory: {source}; screen {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}), "
            + $"grid ({_screenGrid.X:0.#}, {_screenGrid.Y:0.#}) cell {_screenGrid.CellSize:0.##} px. Hover an item and press Ctrl+C.";
        _scene.InvalidateVisual();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        e.Handled = true;
        // Like the game: nothing under the cursor leaves the clipboard unchanged.
        if (_scene.Hovered is { } item && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(item.Text);
    }
}

/// <summary>Draws the inventory, the map device and the hovered item's text, and tracks what is under the cursor.</summary>
internal sealed class Scene : Control
{
    private static readonly Typeface Font = new("Inter");
    private static readonly IBrush CellFill = new SolidColorBrush(Color.FromRgb(0x1c, 0x1f, 0x26));
    private static readonly IPen CellLine = new Pen(new SolidColorBrush(Color.FromRgb(0x3a, 0x3f, 0x4a)), 1);
    private static readonly IPen HoverLine = new Pen(Brushes.White, 2);
    private static readonly IBrush PanelFill = new SolidColorBrush(Color.FromArgb(0xe8, 0x0c, 0x0b, 0x0a));
    private static readonly IBrush Label = Brushes.White;
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0xb8, 0xb0, 0xa0));

    private Point? _pointer;

    public IReadOnlyList<Placed> Inventory { get; set; } = [];
    public MockItem? Waystone { get; set; }
    public IReadOnlyList<MockItem?> Tablets { get; set; } = [];
    public Rect Grid { get; set; }
    public double CellSize { get; set; }
    public Point DeviceCenter { get; set; }

    /// <summary>The item under the cursor, as of the last pointer move.</summary>
    public MockItem? Hovered => _pointer is { } p ? HitTest(p)?.Item : null;

    public static double DeviceSpacing(double cell) => cell * 1.9;

    private static double SlotSize(double cell) => cell * 1.25;

    /// <summary>Distance from the device center up to the top edge of the waystone slot.</summary>
    public static double DeviceTop(double cell) => DeviceSpacing(cell) / 2 + SlotSize(cell) / 2;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    private IEnumerable<(Rect Rect, MockItem? Item)> DeviceSlots()
    {
        var size = SlotSize(CellSize);
        var spacing = DeviceSpacing(CellSize);
        Rect At(double dx, double dy) =>
            new(DeviceCenter.X + dx - size / 2, DeviceCenter.Y + dy - size / 2, size, size);

        // Two rows: the waystone on top, the four tablets below it.
        yield return (At(0, -spacing / 2), Waystone);
        for (var i = 0; i < MockGameWindow.TabletSlots; i++)
            yield return (At((i - 1.5) * spacing, spacing / 2), i < Tablets.Count ? Tablets[i] : null);
    }

    private (Rect Rect, MockItem Item)? HitTest(Point p)
    {
        if (CellSize <= 0)
            return null;
        if (Grid.Contains(p))
        {
            var column = (int)((p.X - Grid.X) / CellSize);
            var row = (int)((p.Y - Grid.Y) / CellSize);
            if (Inventory.FirstOrDefault(i => i.Covers(column, row)) is { } placed)
                return (ItemRect(placed), placed.Item);
            return null;
        }
        foreach (var (rect, item) in DeviceSlots())
        {
            if (item is not null && rect.Contains(p))
                return (rect, item);
        }
        return null;
    }

    private Rect ItemRect(Placed p) =>
        new(Grid.X + p.Column * CellSize, Grid.Y + p.Row * CellSize, p.Item.Width * CellSize, p.Item.Height * CellSize);

    public override void Render(DrawingContext context)
    {
        // A background makes the whole area hit-testable; the window paints the actual color.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (CellSize <= 0)
            return;

        for (var column = 0; column < GridSource.Inventory.Columns; column++)
        {
            for (var row = 0; row < GridSource.Inventory.Rows; row++)
            {
                var cell = new Rect(Grid.X + column * CellSize, Grid.Y + row * CellSize, CellSize, CellSize);
                context.DrawRectangle(CellFill, CellLine, cell);
            }
        }
        foreach (var placed in Inventory)
            DrawItem(context, ItemRect(placed).Deflate(1.5), placed.Item);

        DrawText(context, "Map Device", new Point(DeviceCenter.X, DeviceCenter.Y - DeviceTop(CellSize) - 16), 14, Muted, center: true);
        var first = true;
        foreach (var (rect, item) in DeviceSlots())
        {
            context.DrawRectangle(CellFill, CellLine, rect);
            if (item is not null)
                DrawItem(context, rect.Deflate(2), item);
            else
                DrawText(context, first ? "waystone" : "tablet", rect.Center, 10, Muted, center: true);
            first = false;
        }

        if (_pointer is { } p && HitTest(p) is { } hit)
        {
            context.DrawRectangle(null, HoverLine, hit.Rect);
            DrawItemText(context, hit.Item);
        }
    }

    private static Color FillOf(MockItem item) => item.Kind switch
    {
        ItemKind.Currency => Color.FromRgb(0x5a, 0x4a, 0x2a),
        ItemKind.Waystone => Color.FromRgb(0x2c, 0x3e, 0x5c),
        ItemKind.Tablet => Color.FromRgb(0x2c, 0x50, 0x40),
        _ => item.Corner switch
        {
            "R" => Color.FromRgb(0x5c, 0x55, 0x22),
            "M" => Color.FromRgb(0x2e, 0x36, 0x66),
            _ => Color.FromRgb(0x44, 0x44, 0x44),
        },
    };

    private static void DrawItem(DrawingContext context, Rect rect, MockItem item)
    {
        context.FillRectangle(new SolidColorBrush(FillOf(item)), rect, 3);
        var small = Math.Max(8, Math.Min(rect.Width, rect.Height) / 5);
        if (MockIcons.Load(item.Image) is { } icon)
        {
            // Like the game: the icon fills the cell, the stack size sits on top of it; the code goes bottom-left.
            context.DrawImage(icon, rect.Deflate(Math.Min(rect.Width, rect.Height) * 0.08));
            DrawText(context, item.Corner, new Point(rect.Right - 3, rect.Top + 2), small * 1.2, Label, alignRight: true, shadow: true);
            DrawText(context, item.Label, new Point(rect.Left + 3, rect.Bottom - small * 1.5), small, Muted, shadow: true);
            return;
        }
        DrawText(context, item.Label, rect.Center, Math.Max(9, rect.Height > rect.Width ? rect.Width / 4 : rect.Height / 4), Label, center: true);
        DrawText(context, item.Corner, new Point(rect.Right - 3, rect.Top + 2), small, Muted, alignRight: true);
    }

    /// <summary>A tooltip-like panel at the top-left with the text Ctrl+C would copy.</summary>
    private static void DrawItemText(DrawingContext context, MockItem item)
    {
        var text = Formatted(item.Text, 12, Label);
        text.MaxTextWidth = 480;
        var panel = new Rect(12, 36, text.Width + 24, text.Height + 24);
        context.FillRectangle(PanelFill, panel, 4);
        context.DrawText(text, new Point(panel.X + 12, panel.Y + 12));
    }

    private static void DrawText(DrawingContext context, string s, Point at, double size, IBrush brush,
        bool center = false, bool alignRight = false, bool shadow = false)
    {
        var text = Formatted(s, size, brush);
        var x = center ? at.X - text.Width / 2 : alignRight ? at.X - text.Width : at.X;
        var y = center ? at.Y - text.Height / 2 : at.Y;
        // A dark outline keeps text readable on top of an icon.
        if (shadow)
        {
            var dark = Formatted(s, size, Brushes.Black);
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                context.DrawText(dark, new Point(x + dx, y + dy));
        }
        context.DrawText(text, new Point(x, y));
    }

    private static FormattedText Formatted(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
}
