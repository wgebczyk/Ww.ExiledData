using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ww.ExiledData.App.ViewModels.Settings;
using Ww.ExiledData.Capture;
using KeyModifiers = Avalonia.Input.KeyModifiers;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// The keycaps box of a hotkey row. While its <see cref="HotkeyField"/> records, it takes
/// focus and turns the first key press with a modifier and an allowed key (A-Z, 0-9, F1-F24) into the hotkey;
/// Esc cancels. Modifier keys on their own and other keys are swallowed, so they don't trigger the dialog.
/// </summary>
public sealed class KeyRecorderBox : Border
{
    private HotkeyField? _field;

    public KeyRecorderBox()
    {
        Focusable = true;
        // Clicking the box while recording keeps the keys coming here.
        AddHandler(PointerPressedEvent, (_, _) => { if (_field?.IsRecording == true) Focus(); }, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _field?.PropertyChanged -= OnFieldChanged;
        _field = DataContext as HotkeyField;
        _field?.PropertyChanged += OnFieldChanged;
        UpdateName();
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HotkeyField.IsRecording) or nameof(HotkeyField.Text))
            UpdateName();
        if (e.PropertyName == nameof(HotkeyField.IsRecording) && _field?.IsRecording == true)
            Dispatcher.UIThread.Post(() => Focus(NavigationMethod.Tab));
    }

    /// <summary>Screen readers announce the state and the resulting combination.</summary>
    private void UpdateName()
    {
        if (_field is null)
            return;
        AutomationProperties.SetName(this, _field.IsRecording
            ? $"{_field.Label}: recording, press the new combination, Escape cancels"
            : $"{_field.Label}: {_field.Text}");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_field is not { IsRecording: true } field)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            field.CancelRecording();
            return;
        }
        if (ToHotkey(e.Key, e.KeyModifiers) is { } hotkey)
            field.CompleteRecording(hotkey);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_field?.IsRecording == true)
            e.Handled = true;
        else
            base.OnKeyUp(e);
    }

    /// <summary>A complete combination: at least one modifier and an allowed key; null otherwise (keep recording).</summary>
    public static Hotkey? ToHotkey(Key key, KeyModifiers modifiers)
    {
        var vk = key switch
        {
            >= Key.A and <= Key.Z => 'A' + (key - Key.A),
            >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
            >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1), // VK_F1
            _ => 0,
        };
        var mods = Capture.KeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= Capture.KeyModifiers.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= Capture.KeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= Capture.KeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= Capture.KeyModifiers.Win;
        return vk != 0 && mods != Capture.KeyModifiers.None ? new Hotkey(mods, vk) : null;
    }
}
