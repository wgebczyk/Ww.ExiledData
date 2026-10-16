using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// A keycap on the top right corner of the button it presses: only the key under the common hotkey prefix, else
/// the whole shortcut (<see cref="HotkeyHint.Badge"/>). Hidden without a hotkey; never takes the pointer, so the
/// button underneath keeps its clicks and tooltip. Style: <c>c|KeyBadge</c> in Theme/MappingControls.axaml.
/// </summary>
public sealed class KeyBadge : Border
{
    public static readonly StyledProperty<HotkeyHint?> HintProperty = AvaloniaProperty.Register<KeyBadge, HotkeyHint?>(nameof(Hint));

    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public KeyBadge()
    {
        Child = _text;
        IsHitTestVisible = false;
        IsVisible = false;
    }

    public HotkeyHint? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HintProperty)
        {
            _text.Text = Hint?.Badge;
            IsVisible = !string.IsNullOrEmpty(Hint?.Badge);
        }
    }
}
