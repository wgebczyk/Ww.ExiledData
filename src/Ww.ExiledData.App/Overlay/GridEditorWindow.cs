using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Positioning: a preview grid over the game that the user aligns by eye. The mouse drags the title (move) or one of
/// the eight handles (resize, aspect locked); keys fine-tune: Space at one corner then the opposite one, arrows nudge,
/// Ctrl+arrows resize, Enter confirms, Escape cancels. Keys and presses come from the global hooks, which keep them
/// from the game. The window itself stays click-through like the other overlays: a window that takes the mouse ends
/// up behind a borderless game or loses the clicks to it, and the game must keep focus anyway.
/// </summary>
public sealed class GridEditorWindow : ScreenOverlayWindow
{
    private const double NudgePx = 1;
    private const double NudgeFastPx = 10;
    private const double ResizePx = 0.1;
    private const double ResizeFastPx = 1;
    /// <summary>Smallest cell a drag makes (physical pixels): small enough for any inventory, big enough to grab.</summary>
    private const double MinDragCellSize = 12;

    // A dark amber dim: positioning reads as its own mode even out of the corner of the eye.
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromArgb(0x38, 0x3C, 0x22, 0x00));
    private static readonly IBrush AnchorFill = new SolidColorBrush(GridPainter.PositioningColor);
    private static readonly IPen AnchorRim = new Pen(Brushes.Black, 1);

    private readonly TaskCompletionSource<GridGeometry?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Surface _surface;
    // Follows the cursor: hover over the chrome, a drag in progress, the rubber band between two Space corners.
    private readonly DispatcherTimer _tracker;
    private (double X, double Y)? _anchor;
    private (GridHandle Handle, ScreenPoint From, GridGeometry Start)? _drag;
    private GridHandle? _hot;
    private GridGeometry _grid;
    private GridChrome? _chrome;

    public GridEditorWindow(GridKind kind, GridGeometry start)
        : base("Ww.ExiledData grid editor")
    {
        Kind = kind;
        _grid = start;
        _surface = new Surface(this);
        _tracker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _tracker.Tick += (_, _) => Track();
        Content = _surface;
        Opened += (_, _) => _tracker.Start();
        Closed += (_, _) =>
        {
            _tracker.Stop();
            _result.TrySetResult(null);
        };
    }

    public GridKind Kind { get; }

    /// <summary>Where a press grabs the grid (title and handles), physical pixels; raised whenever they move.</summary>
    public event Action<IReadOnlyList<ScreenRect>>? GrabAreasChanged;

    public GridGeometry Grid
    {
        get => _grid;
        private set
        {
            _grid = value;
            UpdateChrome();
        }
    }

    /// <summary>The confirmed grid (screen pixels), or null when canceled.</summary>
    public Task<GridGeometry?> Result => _result.Task;

    private string Detail => $"{Kind.Name} {_grid.Columns}×{_grid.Rows}";

    public void Confirm()
    {
        _result.TrySetResult(_grid);
        Close();
    }

    public void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }

    public void HandleKey(GridKeyPress key)
    {
        BringToTop();
        HandleKeyCore(key);
    }

    /// <summary>A press on the chrome starts a drag (the hook only reports presses on <see cref="GrabAreasChanged"/>); the release ends it.</summary>
    public void HandleMouse(GridMousePress press)
    {
        BringToTop();
        if (!press.Down)
        {
            if (_drag is { } drag)
                Grid = drag.Start.Drag(drag.Handle, press.Cursor.X - drag.From.X, press.Cursor.Y - drag.From.Y, MinDragCellSize);
            _drag = null;
            return;
        }
        if (_chrome?.HitTest(ToLocal(press.Cursor.X, press.Cursor.Y)) is not { } handle)
            return;
        _anchor = null; // a drag ends a half-set corner pair
        _drag = (handle, press.Cursor, _grid);
        SetHot(handle);
    }

    private void HandleKeyCore(GridKeyPress key)
    {
        switch (key.Key)
        {
            case GridKey.Confirm:
                Confirm();
                return;
            case GridKey.Cancel:
                Cancel();
                return;
            case GridKey.Corner:
                SetCorner(key.Cursor.X, key.Cursor.Y);
                return;
        }

        _anchor = null; // fine-tuning ends a half-set corner pair
        if (key.Ctrl)
        {
            var step = key.Shift ? ResizeFastPx : ResizePx;
            Grid = Grid.Resize(key.Key is GridKey.Right or GridKey.Down ? step : -step);
            return;
        }
        var move = key.Shift ? NudgeFastPx : NudgePx;
        Grid = key.Key switch
        {
            GridKey.Left => Grid.Translate(-move, 0),
            GridKey.Right => Grid.Translate(move, 0),
            GridKey.Up => Grid.Translate(0, -move),
            _ => Grid.Translate(0, move),
        };
    }

    /// <summary>First call fixes one corner; the second spans the grid from it to here.</summary>
    private void SetCorner(double x, double y)
    {
        // The game may be on another screen than the one the editor opened on: go where the user points.
        FollowToScreenOf(x, y);
        if (_anchor is not { } anchor)
        {
            _anchor = (x, y);
            if (!ScreenBounds.Contains(new PixelPoint((int)(_grid.X + _grid.Width / 2), (int)(_grid.Y + _grid.Height / 2))))
                Grid = _grid with { X = x, Y = y }; // bring the preview along
        }
        else
        {
            Grid = GridGeometry.FromCorners(anchor.X, anchor.Y, x, y, _grid.Columns, _grid.Rows);
            _anchor = null;
        }
        _surface.InvalidateVisual();
    }

    private void Track()
    {
        if (!OperatingSystem.IsWindows() || OverlayInterop.CursorPosition() is not { } at)
            return;
        if (_drag is { } drag)
        {
            FollowToScreenOf(at.X, at.Y);
            var next = drag.Start.Drag(drag.Handle, at.X - drag.From.X, at.Y - drag.From.Y, MinDragCellSize);
            if (next != _grid)
                Grid = next;
            return;
        }
        if (_anchor is { } anchor && (Math.Abs(at.X - anchor.X) > 2 || Math.Abs(at.Y - anchor.Y) > 2))
        {
            FollowToScreenOf(at.X, at.Y);
            var next = GridGeometry.FromCorners(anchor.X, anchor.Y, at.X, at.Y, _grid.Columns, _grid.Rows);
            if (next != _grid)
                Grid = next;
        }
        SetHot(_chrome?.HitTest(ToLocal(at.X, at.Y)));
    }

    private void SetHot(GridHandle? hot)
    {
        if (hot == _hot)
            return;
        _hot = hot;
        _surface.InvalidateVisual();
    }

    /// <summary>Lays out the chrome for the current grid and screen, and tells the hook where presses grab it.</summary>
    private void UpdateChrome()
    {
        _chrome = GridPainter.Layout(_grid, ToLocal, GridState.Positioning, Detail);
        _surface.InvalidateVisual();
        GrabAreasChanged?.Invoke(_chrome.GrabAreas().Select(r =>
        {
            var (x, y) = ToScreen(r.TopLeft);
            return new ScreenRect(x, y, r.Width * Scale, r.Height * Scale);
        }).ToList());
    }

    protected override void OnScreenChanged() => UpdateChrome();

    private sealed class Surface(GridEditorWindow owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Backdrop, new Rect(Bounds.Size));
            if (owner._chrome is not { } chrome)
                return;
            GridPainter.Draw(context, owner.Grid, owner.ToLocal, [], GridMode.Positioning);
            if (owner._anchor is { } anchor)
            {
                var p = owner.ToLocal(anchor.X, anchor.Y);
                context.DrawEllipse(AnchorFill, AnchorRim, p, 5, 5);
            }
            GridPainter.DrawChrome(context, chrome, GridState.Positioning, owner.Detail, owner._drag?.Handle ?? owner._hot, framed: false);
        }
    }
}
