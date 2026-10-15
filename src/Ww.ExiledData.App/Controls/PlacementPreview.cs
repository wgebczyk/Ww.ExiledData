using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Ww.ExiledData.App.Overlay;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// A small "game window" showing where the capture feedback circle appears for the chosen placement, with the other
/// corners as dashed ghosts. The still circle is drawn partway through its sweep;
/// <see cref="Play"/> runs the real overlay animation in its place.
/// </summary>
public sealed class PlacementPreview : Control
{
    public static readonly StyledProperty<object?> PlacementProperty = AvaloniaProperty.Register<PlacementPreview, object?>(nameof(Placement));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<PlacementPreview, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<PlacementPreview, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<PlacementPreview, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> CursorBrushProperty = AvaloniaProperty.Register<PlacementPreview, IBrush?>(nameof(CursorBrush));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<PlacementPreview>();
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<PlacementPreview>();

    private const double Inset = 14;
    private const double TopOffset = 34; // below the "GAME WINDOW" caption
    private const double Ghost = 30;
    private const double Circle = 36;
    private const double StillProgress = 0.62;

    private readonly CaptureIndicator _indicator = new();
    private bool _playing;

    static PlacementPreview()
    {
        AffectsRender<PlacementPreview>(PlacementProperty, FillProperty, LineBrushProperty, AccentBrushProperty, CursorBrushProperty,
            ForegroundProperty, FontFamilyProperty);
        AffectsArrange<PlacementPreview>(PlacementProperty);
    }

    public PlacementPreview()
    {
        ClipToBounds = true;
        _indicator.IsVisible = false;
        _indicator.Idle += () =>
        {
            _playing = false;
            _indicator.IsVisible = false;
            InvalidateVisual();
        };
        VisualChildren.Add(_indicator);
        LogicalChildren.Add(_indicator);
    }

    /// <summary>An <see cref="OverlayPlacement"/> (object, so it binds straight to the choice field's value).</summary>
    public object? Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? CursorBrush { get => GetValue(CursorBrushProperty); set => SetValue(CursorBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    private OverlayPlacement Chosen => Placement as OverlayPlacement? ?? OverlayPlacement.NearCursor;

    /// <summary>Runs the real feedback animation (sweep, then an "added" result) where the circle sits.</summary>
    public void Play(TimeSpan sweep, TimeSpan result)
    {
        _indicator.SweepDuration = sweep;
        _indicator.ResultDuration = result;
        _playing = true;
        _indicator.IsVisible = true;
        InvalidateVisual();
        _indicator.StartSweep();
        DispatcherTimer.RunOnce(() => _indicator.ShowResult(new Feedback(FeedbackKind.Added, "Added")), sweep / 2);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _indicator.Measure(new Size(Circle, Circle));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _indicator.Arrange(CircleRect(finalSize));
        return finalSize;
    }

    private Point CursorTip(Size size) => new(Math.Round(size.Width * 0.47), Math.Round(size.Height * 0.48));

    private Rect CornerRect(Size size, OverlayPlacement corner, double d)
    {
        var inset = (Ghost - d) / 2;
        var x = corner is OverlayPlacement.TopLeft or OverlayPlacement.BottomLeft ? Inset + inset : size.Width - Inset - Ghost + inset;
        var y = corner is OverlayPlacement.TopLeft or OverlayPlacement.TopRight ? TopOffset + inset : size.Height - Inset - Ghost + inset;
        return new Rect(x, y, d, d);
    }

    private Rect CircleRect(Size size)
    {
        if (Chosen == OverlayPlacement.NearCursor)
        {
            var tip = CursorTip(size);
            return new Rect(tip.X + 18, tip.Y + 8, Circle, Circle);
        }
        return CornerRect(size, Chosen, Circle);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var frame = new RoundedRect(new Rect(size).Deflate(0.5), 8);
        context.DrawRectangle(Fill, new Pen(LineBrush, 1), frame);

        var caption = new FormattedText("GAME WINDOW", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), 11, Foreground);
        context.DrawText(caption, new Point(10, 8));

        var ghostPen = new Pen(LineBrush, 2, new DashStyle([2, 2], 0));
        foreach (var corner in new[] { OverlayPlacement.TopLeft, OverlayPlacement.TopRight, OverlayPlacement.BottomLeft, OverlayPlacement.BottomRight })
        {
            if (corner == Chosen)
                continue;
            var r = CornerRect(size, corner, Ghost).Deflate(1);
            context.DrawEllipse(null, ghostPen, r.Center, r.Width / 2, r.Height / 2);
        }

        DrawCursor(context, CursorTip(size));

        if (_playing)
            return;
        var circle = CircleRect(size);
        var center = circle.Center;
        var radius = Circle / 2;
        context.DrawEllipse(LineBrush, null, center, radius, radius);
        DrawPie(context, AccentBrush, center, radius, StillProgress);
        context.DrawEllipse(Fill, null, center, radius * 2 / 3, radius * 2 / 3);
    }

    private void DrawCursor(DrawingContext context, Point tip)
    {
        // Arrow pointer, 14 x 20, tip at the top-left.
        Point P(double x, double y) => new(tip.X + x * 14, tip.Y + y * 20);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(0, 0), isFilled: true);
            g.LineTo(P(0, 1));
            g.LineTo(P(0.30, 0.72));
            g.LineTo(P(0.52, 1));
            g.LineTo(P(0.66, 0.92));
            g.LineTo(P(0.45, 0.66));
            g.LineTo(P(0.80, 0.66));
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(CursorBrush, new Pen(Fill, 1), geometry);
    }

    private static void DrawPie(DrawingContext context, IBrush? brush, Point center, double radius, double progress)
    {
        var angle = progress * 2 * Math.PI;
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(center, isFilled: true);
            g.LineTo(new Point(center.X, center.Y - radius));
            g.ArcTo(end, new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise);
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(brush, null, geometry);
    }
}
