using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// An Analyze tab chart (LiveCharts2), drawn from a <see cref="ChartData"/>: bars per run with their running total on
/// a right axis, one running total, or A vs B (A filled / solid, B outlined / dashed, never color alone). Brushes come
/// from styles (Theme/MappingControls.axaml) and are turned into Skia paints, so the theme switch recolors it.
/// </summary>
public sealed class AnalyzeChart : ContentControl
{
    public static readonly StyledProperty<ChartData?> DataProperty = AvaloniaProperty.Register<AnalyzeChart, ChartData?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<AnalyzeChart, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<AnalyzeChart, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> BBrushProperty = AvaloniaProperty.Register<AnalyzeChart, IBrush?>(nameof(BBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<AnalyzeChart, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<AnalyzeChart, IBrush?>(nameof(GridBrush));

    private const float TextSize = 11;

    private readonly CartesianChart _chart = new()
    {
        LegendPosition = LegendPosition.Hidden,
        TooltipPosition = TooltipPosition.Top,
        AnimationsSpeed = TimeSpan.Zero,
    };

    public AnalyzeChart() => Content = _chart;

    public ChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>Bars per run and group A.</summary>
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    /// <summary>The running total of ALL.</summary>
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    /// <summary>Group B.</summary>
    public IBrush? BBrush { get => GetValue(BBrushProperty); set => SetValue(BBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty || change.Property == BarBrushProperty || change.Property == LineBrushProperty
            || change.Property == BBrushProperty || change.Property == LabelBrushProperty || change.Property == GridBrushProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        if (Data is not { } data)
        {
            _chart.Series = [];
            return;
        }

        var bar = ColorOf(BarBrush);
        var line = ColorOf(LineBrush);
        var b = ColorOf(BBrush);
        var label = ColorOf(LabelBrush);
        var unit = data.Unit;

        switch (data.Kind)
        {
            case ChartKind.BarsAndCumulative:
                _chart.Series =
                [
                    Columns("Per run", data.Values.Select(v => (double?)v), bar, null, unit),
                    Line("Running total", data.Values2, line, dashed: false, unit, scalesAt: 1),
                ];
                _chart.YAxes = [ValueAxis(bar), ValueAxis(line, right: true)];
                break;
            case ChartKind.Cumulative:
                _chart.Series = [Line("Running total", data.Values, line, dashed: false, unit)];
                _chart.YAxes = [ValueAxis(label)];
                break;
            case ChartKind.GroupBars:
                _chart.Series =
                [
                    Columns("A", data.Values.Select((v, i) => data.IsA[i] ? v : (double?)null), bar, null, unit),
                    Columns("B", data.Values.Select((v, i) => data.IsA[i] ? (double?)null : v), b.WithAlpha(0x30), b, unit),
                ];
                _chart.YAxes = [ValueAxis(label)];
                break;
            default: // GroupCumulative
                _chart.Series =
                [
                    Line("B", data.Values2, b, dashed: true, unit),
                    Line("A", data.Values, bar, dashed: false, unit),
                ];
                _chart.YAxes = [ValueAxis(label)];
                break;
        }

        _chart.XAxes =
        [
            new Axis
            {
                Labels = [.. data.Labels],
                LabelsPaint = new SolidColorPaint(label),
                TextSize = TextSize,
                SeparatorsPaint = null,
                MinStep = 1,
            },
        ];
    }

    private static ColumnSeries<double?> Columns(string name, IEnumerable<double?> values, SKColor fill, SKColor? stroke, string unit) => new()
    {
        Name = name,
        Values = values.ToList(),
        Fill = new SolidColorPaint(fill),
        Stroke = stroke is { } s ? new SolidColorPaint(s, 1.5f) : null,
        MaxBarWidth = 28,
        Rx = 3,
        Ry = 3,
        // A and B bars share the run's slot: each run has only one of them.
        IgnoresBarPosition = true,
        YToolTipLabelFormatter = p => $"{Format.Amount(p.Coordinate.PrimaryValue)} {unit}",
    };

    private static LineSeries<double> Line(string name, IReadOnlyList<double> values, SKColor color, bool dashed, string unit, int scalesAt = 0) => new()
    {
        Name = name,
        Values = [.. values],
        Fill = null,
        Stroke = new SolidColorPaint(color, 2) { PathEffect = dashed ? new DashEffect([6, 4]) : null },
        GeometrySize = 0,
        LineSmoothness = 0,
        ScalesYAt = scalesAt,
        YToolTipLabelFormatter = p => $"{Format.Amount(p.Coordinate.PrimaryValue)} {unit}",
    };

    private Axis ValueAxis(SKColor labels, bool right = false) => new()
    {
        MinLimit = 0,
        Position = right ? AxisPosition.End : AxisPosition.Start,
        LabelsPaint = new SolidColorPaint(labels),
        TextSize = TextSize,
        SeparatorsPaint = right ? null : new SolidColorPaint(ColorOf(GridBrush), 1),
        ShowSeparatorLines = !right,
        // One format for the whole axis ("2.5", "10", "12.5"), not three significant digits per label.
        Labeler = v => v.ToString(v >= 100 ? "#,0" : "#,0.##", CultureInfo.InvariantCulture),
    };

    private static SKColor ColorOf(IBrush? brush) =>
        brush is ISolidColorBrush { Color: var c } ? new SKColor(c.R, c.G, c.B, (byte)(c.A * brush.Opacity)) : SKColors.Gray;
}
