using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Ww.ExiledData.App.Views;

/// <summary>Minimal yes/no confirmation dialog.</summary>
public sealed class ConfirmDialog : Window
{
    internal ConfirmDialog(string message, string confirm, string cancel)
    {
        Title = "Confirm";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Classes.Add("app");

        var yes = new Button { Content = confirm, IsDefault = true, Classes = { "ui", "danger" } };
        var no = new Button { Content = cancel, IsCancel = true, Classes = { "ui" } };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yes, no } },
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(false);
        };
    }

    public static async Task<bool> Ask(Window owner, string message, string confirm = "Delete", string cancel = "Cancel") =>
        await new ConfirmDialog(message, confirm, cancel).ShowDialog<bool?>(owner) == true;
}
