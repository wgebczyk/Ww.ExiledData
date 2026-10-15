using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Ww.ExiledData.App.Views;

/// <summary>
/// Minimal text input dialog: one line, one line with suggestions (tags used before), or several lines (notes).
/// </summary>
public sealed class PromptDialog : Window
{
    internal PromptDialog(string title, string label, string initial, IReadOnlyList<string>? suggestions, bool multiline)
    {
        Title = title;
        Width = multiline ? 520 : 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Classes.Add("app");

        Control input;
        Func<string?> read;
        if (suggestions is not null)
        {
            var box = new AutoCompleteBox
            {
                Text = initial,
                ItemsSource = suggestions,
                FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
                MinimumPrefixLength = 0,
                MaxDropDownHeight = 240,
            };
            input = box;
            read = () => box.Text;
        }
        else
        {
            var box = new TextBox
            {
                Text = initial,
                Classes = { "search" },
                AcceptsReturn = multiline,
                TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
                Height = multiline ? 120 : double.NaN,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            };
            input = box;
            read = () => box.Text;
            Opened += (_, _) => box.SelectAll();
        }

        var ok = new Button { Content = "Save", IsDefault = !multiline, Classes = { "ui", "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "ui" } };
        ok.Click += (_, _) => Close(read()?.Trim() ?? "");
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 4, 0, 0), Children = { ok, cancel } },
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
            else if (multiline && e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                Close(read()?.Trim() ?? "");
        };
        Opened += (_, _) => input.Focus();
    }

    /// <summary>Returns the trimmed text, or null when canceled.</summary>
    public static Task<string?> Ask(Window owner, string title, string label, string initial = "") =>
        new PromptDialog(title, label, initial, null, multiline: false).ShowDialog<string?>(owner);

    /// <summary>One line with a drop-down of <paramref name="suggestions"/>; any other text is fine too.</summary>
    public static Task<string?> AskWithSuggestions(Window owner, string title, string label, IReadOnlyList<string> suggestions) =>
        new PromptDialog(title, label, "", suggestions, multiline: false).ShowDialog<string?>(owner);

    /// <summary>Several lines (Ctrl+Enter saves); empty text means "remove".</summary>
    public static Task<string?> AskMultiline(Window owner, string title, string label, string initial = "") =>
        new PromptDialog(title, label, initial, null, multiline: true).ShowDialog<string?>(owner);
}
