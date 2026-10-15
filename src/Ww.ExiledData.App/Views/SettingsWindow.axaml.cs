using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? _vm;
    private bool _closeConfirmed;
    private bool _saved;

    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.Saved = () =>
        {
            _saved = _closeConfirmed = true;
            Close();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            if (!_saved)
                viewModel.Discard();
            else
                viewModel.CancelAllRecording();
        };
    }

    /// <summary>Esc, Cancel or the window's close button: stop a recording first, ask before dropping changes.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_vm is null || _closeConfirmed)
            return;
        if (_vm.AnyRecording)
        {
            e.Cancel = true;
            _vm.CancelAllRecording();
            return;
        }
        if (!_vm.IsDirty)
            return;

        e.Cancel = true;
        var what = _vm.ChangeCount == 1 ? "1 unsaved change" : $"{_vm.ChangeCount} unsaved changes";
        if (await ConfirmDialog.Ask(this, $"Discard changes? You have {what}.", "Discard", "Keep editing"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Closes without asking about unsaved changes (screenshot mode).</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        _vm?.CancelAllRecording();
        Close();
    }

    private void OnPlayPreview(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;
        var sweep = _vm.FeedbackSweep.Value ?? _vm.FeedbackSweep.Saved;
        var result = _vm.FeedbackResult.Value ?? _vm.FeedbackResult.Saved;
        Preview.Play(TimeSpan.FromMilliseconds(sweep), TimeSpan.FromMilliseconds(result));
    }

    /// <summary>The loot grid picker opens with its search box focused.</summary>
    private void OnLootPickerOpened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: { } child })
            Dispatcher.UIThread.Post(() => child.FindDescendantOfType<TextBox>()?.Focus());
    }
}
