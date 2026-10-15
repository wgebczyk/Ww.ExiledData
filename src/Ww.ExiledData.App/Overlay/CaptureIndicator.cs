using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Capture feedback: a disc filling like a clock hand for a fixed time, then a flash showing the outcome
/// with a distinct color and line icon. A new press restarts the sweep immediately.
/// </summary>
public sealed class CaptureIndicator : Control
{
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromArgb(0xC8, 0x18, 0x1B, 0x22));
    private static readonly IBrush SweepBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xE3));
    private static readonly IPen Rim = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.5);
    private static readonly IBrush IconBrush = Brushes.White;
    private static readonly IBrush HaloBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0x00, 0x00, 0x00));
    private static readonly Typeface CountFace = new("Segoe UI, Inter", FontStyle.Normal, FontWeight.Bold);

    /// <summary>
    /// Opacity of the sweep disc and of the outcome disc, so the game stays visible through them. The outcome's icon
    /// and number are drawn opaque (with a dark halo) so they read over any game scene.
    /// </summary>
    private const double Translucency = 0.7;

    /// <summary>When the sweep is over but the outcome is still unknown, give up after this long.</summary>
    private static readonly TimeSpan MaxPending = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Feedback? _result;
    private TimeSpan _resultShownAt;
    private bool _sweeping;

    public CaptureIndicator()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Tick());
        IsHitTestVisible = false;
    }

    public TimeSpan SweepDuration { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan ResultDuration { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Raised when the indicator has nothing left to show.</summary>
    public event Action? Idle;

    /// <summary>Ctrl+C went down: restart the clock-hand sweep.</summary>
    public void StartSweep()
    {
        _sweeping = true;
        _result = null;
        _clock.Restart();
        _timer.Start();
        InvalidateVisual();
    }

    /// <summary>Outcome known. Shown after the sweep completes, or right away when no sweep is running.</summary>
    public void ShowResult(Feedback feedback)
    {
        _result = feedback;
        if (!_sweeping)
        {
            _clock.Restart();
            _resultShownAt = TimeSpan.Zero;
        }
        else if (_clock.Elapsed >= SweepDuration)
        {
            _sweeping = false;
            _resultShownAt = _clock.Elapsed;
        }
        _timer.Start();
        InvalidateVisual();
    }

    /// <summary>Shows an outcome without animating or fading it (the screenshot sheet).</summary>
    public void ShowStill(Feedback feedback)
    {
        _timer.Stop();
        _clock.Reset();
        _sweeping = false;
        _result = feedback;
        _resultShownAt = TimeSpan.Zero;
        InvalidateVisual();
    }

    private void Tick()
    {
        var t = _clock.Elapsed;
        if (_sweeping && t >= SweepDuration)
        {
            if (_result is not null)
            {
                _sweeping = false;
                _resultShownAt = t;
            }
            else if (t >= SweepDuration + MaxPending)
            {
                _sweeping = false;
                _result = new Feedback(FeedbackKind.Nothing, "No result");
                _resultShownAt = t;
            }
        }

        if (!_sweeping && (_result is null || t - _resultShownAt >= ResultDuration))
        {
            _result = null;
            _timer.Stop();
            Idle?.Invoke();
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 2;

        if (_sweeping)
        {
            using (context.PushOpacity(Translucency))
            {
                context.DrawEllipse(Backdrop, Rim, center, radius, radius);
                var progress = Math.Clamp(_clock.Elapsed / SweepDuration, 0, 1);
                DrawPie(context, SweepBrush, center, radius - 2, progress);
            }
            return;
        }

        if (_result is not { } result)
            return;

        var fade = 1 - Math.Clamp((_clock.Elapsed - _resultShownAt - ResultDuration * 0.6) / (ResultDuration * 0.4), 0, 1);
        var style = Style(result);
        using (context.PushOpacity(Translucency * fade))
        {
            context.DrawEllipse(new SolidColorBrush(style.Color), Rim, center, radius, radius);
            if (result.OutsideGrid)
                context.DrawEllipse(null, OutsideGridRim, center, radius - 1.5, radius - 1.5);
        }
        using (context.PushOpacity(fade))
        {
            if (style.Count is { } count)
            {
                // A small icon above the number says what the number counts (a stack, a finished sweep).
                DrawIcon(context, style.Icon, new Point(center.X, center.Y - size * 0.21), size * 0.3, strokeWidth: 2.6);
                DrawCount(context, count, new Point(center.X, center.Y + size * 0.13), size);
            }
            else
            {
                DrawIcon(context, style.Icon, center, size * 0.5, strokeWidth: 2.4);
            }
        }
    }

    /// <summary>Marks a capture taken outside a running sweep's grid.</summary>
    private static readonly IPen OutsideGridRim = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x3C)), 3, new DashStyle([1.5, 1], 0));

    /// <summary>How an outcome is drawn: the disc color, a line icon on the 24 x 24 grid, and an optional number under it.</summary>
    public readonly record struct OutcomeStyle(Color Color, Geometry Icon, string? Count = null);

    private static readonly Color Green = Color.FromRgb(0x2E, 0x9E, 0x4F);
    private static readonly Color DarkGreen = Color.FromRgb(0x1F, 0x7A, 0x3E);
    private static readonly Color Amber = Color.FromRgb(0xC9, 0x8A, 0x1A);
    private static readonly Color Blue = Color.FromRgb(0x2F, 0x6F, 0xD6);
    private static readonly Color Slate = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color InfoSlate = Color.FromRgb(0x4B, 0x55, 0x68);
    private static readonly Color Purple = Color.FromRgb(0x7D, 0x4C, 0xDB);
    private static readonly Color Teal = Color.FromRgb(0x13, 0x8D, 0x90);
    private static readonly Color Red = Color.FromRgb(0xC0, 0x39, 0x2B);

    /// <summary>
    /// Distinct color and icon per outcome, so it reads without relying on color alone. Red is only for a copy that
    /// captured nothing; a hotkey with nothing to act on is information (<see cref="FeedbackKind.Info"/>).
    /// </summary>
    public static OutcomeStyle Style(Feedback f) => f.Kind switch
    {
        FeedbackKind.Added => new(Green, OverlayIcons.Check),
        FeedbackKind.SiblingAdded => new(Amber, OverlayIcons.Copy),
        FeedbackKind.StackableAdded or FeedbackKind.StackableUpdated =>
            f.Count is { } stack ? new(Blue, OverlayIcons.Stack, FormatCount(stack)) : new(Blue, OverlayIcons.Stack),
        FeedbackKind.AlreadyRecorded => new(Slate, OverlayIcons.DoubleCheck),
        FeedbackKind.Covered => new(DarkGreen, OverlayIcons.Link),
        FeedbackKind.Info => new(InfoSlate, OverlayIcons.Info),
        FeedbackKind.NotInGame => new(Red, OverlayIcons.Pointer),
        FeedbackKind.RunStarted => new(Teal, OverlayIcons.Flag),
        FeedbackKind.RunEnded => new(Teal, OverlayIcons.Stop),
        FeedbackKind.Undo => new(Purple, OverlayIcons.Undo),
        FeedbackKind.Redo => new(Purple, OverlayIcons.Redo),
        FeedbackKind.NewSession => new(Teal, OverlayIcons.Play),
        FeedbackKind.SweepStarted => new(Teal, OverlayIcons.Grid),
        FeedbackKind.SweepEnded => f.Count is { } items ? new(Teal, OverlayIcons.Flag, FormatCount(items)) : new(Teal, OverlayIcons.Flag),
        FeedbackKind.SweepCanceled => new(Slate, OverlayIcons.Ban),
        FeedbackKind.CellSkipped => new(Slate, OverlayIcons.Skip),
        _ => new(Red, OverlayIcons.Cross),
    };

    private static string FormatCount(int count) => count < 10_000
        ? count.ToString(CultureInfo.InvariantCulture)
        : $"{count / 1000}k";

    private static void DrawIcon(DrawingContext context, Geometry icon, Point center, double box, double strokeWidth) =>
        OverlayIcons.Draw(context, icon, center, box, strokeWidth, IconBrush, HaloBrush);

    /// <summary>Draws a number centered on its ink (not its line box), with the same halo as the icons.</summary>
    private static void DrawCount(DrawingContext context, string count, Point center, double size)
    {
        var fontSize = count.Length switch { <= 2 => size * 0.36, 3 => size * 0.31, _ => size * 0.25 };
        var text = new FormattedText(count, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CountFace, fontSize, IconBrush);
        if (text.BuildGeometry(default) is not { } geometry)
            return;
        var ink = geometry.Bounds;
        using (context.PushTransform(Matrix.CreateTranslation(center.X - ink.Center.X, center.Y - ink.Center.Y)))
        {
            context.DrawGeometry(null, OverlayIcons.RoundPen(HaloBrush, 2.4), geometry);
            context.DrawGeometry(IconBrush, null, geometry);
        }
    }

    private static void DrawPie(DrawingContext context, IBrush brush, Point center, double radius, double progress)
    {
        if (progress <= 0)
            return;
        if (progress >= 1)
        {
            context.DrawEllipse(brush, null, center, radius, radius);
            return;
        }

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
