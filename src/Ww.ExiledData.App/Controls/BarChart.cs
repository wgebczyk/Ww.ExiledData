using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// The Analyse tab's per-run chart: one bar per run (3 px top radius), a dashed average line, the
/// run numbers below. Brushes come from styles (Theme/MappingControls.axaml), so the theme switch recolors it.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>> ValuesProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<double>>(nameof(Values), []);
    public static readonly StyledProperty<IReadOnlyList<string>> LabelsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<string>>(nameof(Labels), []);
    public static readonly StyledProperty<double> AverageProperty = AvaloniaProperty.Register<BarChart, double>(nameof(Average));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<BarChart, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<IBrush?> AverageBrushProperty = AvaloniaProperty.Register<BarChart, IBrush?>(nameof(AverageBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<BarChart, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<BarChart, FontFamily>(nameof(FontFamily), FontFamily.Default);

    private const double LabelHeight = 18;
    private const double MaxBarWidth = 28;

    static BarChart() =>
        AffectsRender<BarChart>(ValuesProperty, LabelsProperty, AverageProperty, BarBrushProperty, AverageBrushProperty, LabelBrushProperty);

    public IReadOnlyList<double> Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<string> Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public double Average { get => GetValue(AverageProperty); set => SetValue(AverageProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? AverageBrush { get => GetValue(AverageBrushProperty); set => SetValue(AverageBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values.Count == 0)
            return;

        var width = Bounds.Width;
        var plot = Math.Max(0, Bounds.Height - LabelHeight);
        var max = Math.Max(values.Max(), Average);
        if (max <= 0)
            max = 1;
        var slot = width / values.Count;
        var barWidth = Math.Min(MaxBarWidth, slot * 0.6);
        var typeface = new Typeface(FontFamily);
        // Labels only every n-th run when they would overlap.
        var labelStep = Math.Max(1, (int)Math.Ceiling(24 / slot));

        for (var i = 0; i < values.Count; i++)
        {
            var x = slot * i + (slot - barWidth) / 2;
            var h = plot * values[i] / max;
            if (h > 0 && BarBrush is { } bar)
                context.DrawRectangle(bar, null, new RoundedRect(new Rect(x, plot - h, barWidth, h), 3, 3, 0, 0));

            if (i % labelStep == 0 && i < Labels.Count && LabelBrush is { } labelBrush)
            {
                var text = new FormattedText(Labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, labelBrush);
                context.DrawText(text, new Point(slot * i + (slot - text.Width) / 2, plot + 3));
            }
        }

        if (AverageBrush is { } line && Average > 0)
        {
            var y = plot - plot * Average / max;
            var pen = new Pen(line, 1.5, new DashStyle([4, 3], 0));
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }
}
