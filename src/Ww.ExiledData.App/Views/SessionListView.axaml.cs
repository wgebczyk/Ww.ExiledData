using Avalonia.Controls;
using Avalonia.Input;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

/// <summary>The sessions page: Enter or a double click opens the selected session in place.</summary>
public partial class SessionListView : UserControl
{
    public SessionListView() => InitializeComponent();

    private SessionListViewModel? ViewModel => DataContext as SessionListViewModel;

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.OpenCommand.Execute(null);

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }
}
