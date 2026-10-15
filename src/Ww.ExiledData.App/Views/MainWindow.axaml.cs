using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

/// <summary>
/// The single app window. Owns every dialog of its pages (<see cref="Attach(SessionListViewModel)"/>,
/// <see cref="Attach(SessionViewModel)"/>). Back: Alt+Left, the mouse back button, Escape on a session page.
/// Ctrl+K opens the session switcher.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public void Attach(SessionListViewModel list)
    {
        list.Confirm = (message, confirm) => ConfirmDialog.Ask(this, message, confirm);
        list.Prompt = (title, initial) => PromptDialog.Ask(this, title, "Session name", initial);
        list.AskNewSession = (defaultName, recording) => NewSessionDialog.Ask(this, defaultName, recording);
        list.AskTag = (title, suggestions) => PromptDialog.AskWithSuggestions(this, title,
            "A tag, optionally with a value: \"rituals: 6\", \"died\". Tags used before are suggested.", suggestions);
    }

    public SessionViewModel Attach(SessionViewModel session)
    {
        session.Confirm = (message, confirm) => ConfirmDialog.Ask(this, message, confirm);
        session.Prompt = (title, initial) => PromptDialog.Ask(this, title, "Session name", initial);
        session.AskTag = (title, suggestions) => PromptDialog.AskWithSuggestions(this, title,
            "A tag, optionally with a value: \"rituals: 6\", \"died\". Tags used before are suggested.", suggestions);
        session.AskNote = (title, initial) => PromptDialog.AskMultiline(this, title, "Ctrl+Enter saves; an empty note is removed.", initial);
        session.AskRecall = dialog => RecallDialog.Ask(this, dialog);
        session.AskReview = dialog => ReviewDialog.Ask(this, dialog);
        session.AskFilter = (title, help, suggestions) => PromptDialog.AskWithSuggestions(this, title, help, suggestions);
        session.SaveFile = SaveFile;
        return session;
    }

    private async Task<Stream?> SaveFile(string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export session as {extension.ToUpperInvariant()}",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    // --- back and switcher keys -----------------------------------------------------------------

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed
            && ViewModel?.BackCommand.CanExecute(null) == true)
        {
            ViewModel.BackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            SwitcherButton.Flyout?.ShowAt(SwitcherButton);
            e.Handled = true;
        }
    }

    /// <summary>Escape on a session page goes back, unless something focused used it (a text box, a menu).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || ViewModel is not { IsSessionPage: true } vm)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (vm.BackCommand.CanExecute(null))
            vm.BackCommand.Execute(null);
        else
            vm.ShowListCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSwitcherOpening(object? sender, EventArgs e) => ViewModel?.RefreshSwitcher();

    private void OnSwitcherOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        SwitcherList.SelectedItem = vm.SwitcherItems.FirstOrDefault(i => !i.IsCurrent);
        SwitcherList.Focus(NavigationMethod.Tab);
        if (SwitcherList.SelectedItem is { } selected)
            SwitcherList.ScrollIntoView(selected);
    }

    private void OnSwitcherTapped(object? sender, TappedEventArgs e) => OpenSwitcherSelection();

    private void OnSwitcherKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSwitcherSelection();
            e.Handled = true;
        }
    }

    private void OpenSwitcherSelection()
    {
        if (SwitcherList.SelectedItem is not SwitcherItem item || ViewModel is not { } vm)
            return;
        SwitcherButton.Flyout?.Hide();
        vm.OpenSwitcherItem(item);
    }
}
