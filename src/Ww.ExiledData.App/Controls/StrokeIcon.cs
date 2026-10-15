using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// A line icon (24 x 24 grid, 2 px round strokes), scaled to its size. The stroke takes
/// the inherited foreground, so an icon follows its button's text color (primary, hover, disabled) and the theme.
/// Geometries are in Theme/MappingControls.axaml (<c>Icon...</c>).
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<StrokeIcon>();
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeWidth), 2);

    static StrokeIcon() => AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeWidthProperty);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
            return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
    }
}
