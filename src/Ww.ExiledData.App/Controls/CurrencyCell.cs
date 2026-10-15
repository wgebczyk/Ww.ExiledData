using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// A cell of the condensed loot grid: the icon (a box with the code's letter when there
/// is none), the count top-right, the code and an optional L / G / P badge below. A placeholder is a dashed empty cell;
/// a zero count is dimmed. When the count changes the cell is briefly highlighted. Look in
/// Theme/MappingControls.axaml.
/// </summary>
public sealed class CurrencyCell : TemplatedControl
{
    public static readonly StyledProperty<string> CodeProperty = AvaloniaProperty.Register<CurrencyCell, string>(nameof(Code), "");
    public static readonly StyledProperty<string> LetterProperty = AvaloniaProperty.Register<CurrencyCell, string>(nameof(Letter), "");
    public static readonly StyledProperty<IImage?> IconProperty = AvaloniaProperty.Register<CurrencyCell, IImage?>(nameof(Icon));
    public static readonly StyledProperty<string?> BadgeProperty = AvaloniaProperty.Register<CurrencyCell, string?>(nameof(Badge));
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<CurrencyCell, int>(nameof(Count));
    public static readonly StyledProperty<string> CountTextProperty = AvaloniaProperty.Register<CurrencyCell, string>(nameof(CountText), "");
    public static readonly StyledProperty<bool> IsPlaceholderProperty = AvaloniaProperty.Register<CurrencyCell, bool>(nameof(IsPlaceholder));
    public static readonly StyledProperty<bool> IsZeroProperty = AvaloniaProperty.Register<CurrencyCell, bool>(nameof(IsZero));

    private static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(1.5);
    private IDisposable? _highlight;

    public string Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public string Letter { get => GetValue(LetterProperty); set => SetValue(LetterProperty, value); }
    /// <summary>The currency icon; null shows the letter tile instead.</summary>
    public IImage? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Badge { get => GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public int Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public string CountText { get => GetValue(CountTextProperty); set => SetValue(CountTextProperty, value); }
    public bool IsPlaceholder { get => GetValue(IsPlaceholderProperty); set => SetValue(IsPlaceholderProperty, value); }
    public bool IsZero { get => GetValue(IsZeroProperty); set => SetValue(IsZeroProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlaceholderProperty)
            PseudoClasses.Set(":placeholder", IsPlaceholder);
        else if (change.Property == IsZeroProperty)
            PseudoClasses.Set(":zero", IsZero);
        else if (change.Property == IconProperty)
            PseudoClasses.Set(":icon", Icon is not null);
        else if (change.Property == BadgeProperty)
            PseudoClasses.Set(":badge", !string.IsNullOrEmpty(Badge));
        else if (change.Property == CountProperty && IsLoaded && !IsPlaceholder)
            Highlight();
    }

    private void Highlight()
    {
        PseudoClasses.Set(":changed", true);
        _highlight?.Dispose();
        _highlight = DispatcherTimer.RunOnce(() => PseudoClasses.Set(":changed", false), HighlightTime);
    }
}
