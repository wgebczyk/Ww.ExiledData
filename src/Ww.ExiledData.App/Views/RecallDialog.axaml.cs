using Avalonia.Controls;
using Avalonia.Interactivity;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

public partial class RecallDialog : Window
{
    public RecallDialog() => InitializeComponent();

    /// <summary>True when the user assigned the choices (read them from <paramref name="dialog"/>).</summary>
    public static Task<bool> Ask(Window owner, RecallDialogViewModel dialog) =>
        new RecallDialog { DataContext = dialog }.ShowDialog<bool>(owner);

    private void OnAssign(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
