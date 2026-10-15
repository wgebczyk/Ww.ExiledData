using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Ww.ExiledData.App.Controls;

/// <summary>The feedback timeline split proportionally into Sweep and Result, with their values.</summary>
public sealed class TimingBar : Control
{
    public static readonly StyledProperty<int> SweepMsProperty = AvaloniaProperty.Register<TimingBar, int>(nameof(SweepMs));
    public static readonly StyledProperty<int> ResultMsProperty = AvaloniaProperty.Register<TimingBar, int>(nameof(ResultMs));
    public static readonly StyledProperty<IBrush?> SweepBrushProperty = AvaloniaProperty.Register<TimingBar, IBrush?>(nameof(SweepBrush));
    public static readonly StyledProperty<IBrush?> ResultBrushProperty = AvaloniaProperty.Register<TimingBar, IBrush?>(nameof(ResultBrush));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<TimingBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<TimingBar>();
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<TimingBar>();

    private const double BarHeight = 12;
    private const double LabelGap = 6;
    private const double FontSize = 12;

    static TimingBar() => AffectsRender<TimingBar>(SweepMsProperty, ResultMsProperty, SweepBrushProperty, ResultBrushProperty,
        TrackBrushProperty, ForegroundProperty, FontFamilyProperty);

    public int SweepMs { get => GetValue(SweepMsProperty); set => SetValue(SweepMsProperty, value); }
    public int ResultMs { get => GetValue(ResultMsProperty); set => SetValue(ResultMsProperty, value); }
    public IBrush? SweepBrush { get => GetValue(SweepBrushProperty); set => SetValue(SweepBrushProperty, value); }
    public IBrush? ResultBrush { get => GetValue(ResultBrushProperty); set => SetValue(ResultBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, BarHeight + LabelGap + FontSize * 1.4);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var track = new RoundedRect(new Rect(0, 0, width, BarHeight), BarHeight / 2);
        context.DrawRectangle(TrackBrush, null, track);

        var total = Math.Max(0, SweepMs) + Math.Max(0, ResultMs);
        var split = total > 0 ? width * Math.Max(0, SweepMs) / total : width / 2;
        using (context.PushClip(track))
        {
            context.DrawRectangle(SweepBrush, null, new Rect(0, 0, split, BarHeight));
            context.DrawRectangle(ResultBrush, null, new Rect(split, 0, width - split, BarHeight));
        }

        var typeface = new Typeface(FontFamily);
        var y = BarHeight + LabelGap;
        Label($"Sweep · {Ms(SweepMs)}", 0, Math.Max(split, 0));
        Label($"Result · {Ms(ResultMs)}", split, width - split);

        void Label(string text, double x, double room)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, Foreground)
            {
                MaxTextWidth = Math.Max(room - 4, 1),
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1,
            };
            context.DrawText(formatted, new Point(x, y));
        }
    }

    private static string Ms(int ms) => ms > 0 ? $"{ms} ms" : "–";
}
