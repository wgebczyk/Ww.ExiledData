using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Core.Sessions;

namespace Ww.ExiledData.App.Views;

/// <summary>New session (MAPPING_PLAN.md 3.1): a name and a kind. Creating it starts it.</summary>
public sealed class NewSessionDialog : Window
{
    internal NewSessionDialog(string defaultName, string? recordingName)
    {
        Title = "New session";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Classes.Add("app");

        var name = new TextBox { Classes = { "search" }, PlaceholderText = defaultName };
        AutomationProperties.SetName(name, "Session name");
        var free = new RadioButton { Content = "Free recording", GroupName = "kind", IsChecked = true, Classes = { "segment" } };
        var mapping = new RadioButton { Content = "Mapping", GroupName = "kind", Classes = { "segment" } };
        var hint = new TextBlock { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
        void UpdateHint() => hint.Text = mapping.IsChecked == true
            ? "Map runs: a waystone copy starts a run, inventory scans measure the loot. Runs and scans arrive in the next steps; until then it records like a free recording."
            : "Captures, stackables and sweeps, as before.";
        free.IsCheckedChanged += (_, _) => UpdateHint();
        UpdateHint();

        var create = new Button { Content = "Create and start", IsDefault = true, Classes = { "ui", "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "ui" } };
        create.Click += (_, _) => Close(new NewSessionRequest(
            string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim(),
            mapping.IsChecked == true ? SessionKind.Mapping : SessionKind.FreeRecording));
        cancel.Click += (_, _) => Close(null);

        var panel = new StackPanel
        {
            Margin = new(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "NAME", Classes = { "label" } },
                name,
                new TextBlock { Text = "KIND", Classes = { "label" }, Margin = new(0, 8, 0, 0) },
                new Border
                {
                    Classes = { "segmented" },
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { free, mapping } },
                },
                hint,
            },
        };
        if (recordingName is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"'{recordingName}' is recording now; it ends when this one starts.",
                Classes = { "secondary" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new(0, 8, 0, 0),
            });
        }
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new(0, 12, 0, 0),
            Children = { create, cancel },
        });
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
        Opened += (_, _) => name.Focus();
    }

    /// <summary>Returns the new session's name and kind, or null when canceled.</summary>
    /// <param name="recordingName">The session recording now, which starting a new one ends; null when none.</param>
    public static Task<NewSessionRequest?> Ask(Window owner, string defaultName, string? recordingName) =>
        new NewSessionDialog(defaultName, recordingName).ShowDialog<NewSessionRequest?>(owner);
}
