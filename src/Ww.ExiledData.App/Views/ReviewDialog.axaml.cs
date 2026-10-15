using Avalonia.Controls;
using Avalonia.Interactivity;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

public partial class ReviewDialog : Window
{
    public ReviewDialog() => InitializeComponent();

    /// <summary>The user's choice (the edited amounts are in <paramref name="dialog"/>), or null when canceled.</summary>
    public static Task<ReviewChoice?> Ask(Window owner, ReviewDialogViewModel dialog) =>
        new ReviewDialog { DataContext = dialog }.ShowDialog<ReviewChoice?>(owner);

    private void OnApply(object? sender, RoutedEventArgs e) => Close(ReviewChoice.Apply);

    private void OnDismiss(object? sender, RoutedEventArgs e) => Close(ReviewChoice.Dismiss);

    private void OnRestore(object? sender, RoutedEventArgs e) => Close(ReviewChoice.Restore);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
