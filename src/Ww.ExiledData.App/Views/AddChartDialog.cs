using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.App.Views;

/// <summary>
/// Picks the currencies of an Analyze chart: a search box and the currencies of the used runs (by value), each with a
/// checkbox. Returns the ticked names in list order, or null when canceled.
/// </summary>
public sealed class AddChartDialog : Window
{
    private readonly IReadOnlyList<CurrencyChoice> _choices;
    private readonly TextBlock _summary = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _ok;

    internal AddChartDialog(string title, IReadOnlyList<CurrencyChoice> choices)
    {
        _choices = choices;
        Title = title;
        Width = 520;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Classes.Add("app");

        var rows = new StackPanel();
        var byChoice = new List<(CurrencyChoice Choice, Control Row)>();
        foreach (var choice in choices)
        {
            var check = new CheckBox { Classes = { "use" }, IsChecked = choice.IsChosen, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, choice.Name);
            check.IsCheckedChanged += (_, _) =>
            {
                choice.IsChosen = check.IsChecked == true;
                UpdateSummary();
            };
            var code = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { new TextBlock { Text = choice.Code, Classes = { "mono", "amber" } } },
            };
            if (choice.Badge is { } badge)
                code.Children.Add(new Border { Classes = { "badge" }, Child = new TextBlock { Text = badge } });
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("30,90,*,90"),
                ColumnSpacing = 8,
                Children =
                {
                    check,
                    code,
                    new TextBlock { Text = choice.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = choice.ValueText, Classes = { "mono", "secondary" }, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            Grid.SetColumn(grid.Children[1], 1);
            Grid.SetColumn(grid.Children[2], 2);
            Grid.SetColumn(grid.Children[3], 3);
            var row = new Border
            {
                BorderThickness = new(0, 1, 0, 0),
                Padding = new(0, 6),
                Background = Brushes.Transparent,
                Child = grid,
            };
            // A theme brush: resolved once the row is in the window (and again when the theme changes).
            row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("LineBrush"));
            // A click anywhere on the row toggles it.
            row.PointerReleased += (_, e) =>
            {
                if (e.Source is not CheckBox)
                    check.IsChecked = check.IsChecked != true;
            };
            rows.Children.Add(row);
            byChoice.Add((choice, row));
        }

        var search = new TextBox { Classes = { "search" }, PlaceholderText = "Search by code or name" };
        AutomationProperties.SetName(search, "Search currencies");
        search.TextChanged += (_, _) =>
        {
            var text = search.Text?.Trim() ?? "";
            foreach (var (choice, row) in byChoice)
                row.IsVisible = text.Length == 0
                    || choice.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || choice.Code.Contains(text, StringComparison.OrdinalIgnoreCase);
        };

        _ok = new Button { Content = title, IsDefault = true, Classes = { "ui", "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "ui" } };
        _ok.Click += (_, _) => Close(Chosen());
        cancel.Click += (_, _) => Close(null);

        var body = new DockPanel { Margin = new(20) };
        var top = new StackPanel
        {
            Spacing = 10,
            Margin = new(0, 0, 0, 10),
            Children =
            {
                new TextBlock { Text = "A Value chart (bars per run and the running total) of the currencies you tick.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                search,
            },
        };
        var bottom = new StackPanel
        {
            Spacing = 12,
            Margin = new(0, 12, 0, 0),
            Children =
            {
                _summary,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _ok, cancel } },
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(choices.Count == 0
            ? new TextBlock { Text = "No currency dropped in the used runs yet.", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = rows });
        Content = body;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
        Opened += (_, _) => search.Focus();
        UpdateSummary();
    }

    private List<string> Chosen() => _choices.Where(c => c.IsChosen).Select(c => c.Name).ToList();

    private void UpdateSummary()
    {
        var chosen = _choices.Where(c => c.IsChosen).ToList();
        _ok.IsEnabled = chosen.Count > 0;
        _summary.Text = chosen.Count == 0
            ? "Tick at least one currency."
            : $"{chosen.Count} ticked: {string.Join(", ", chosen.Select(c => c.Badge is { } b ? $"{c.Code}/{b}" : c.Code))}";
    }

    /// <summary>The ticked currency names, or null when canceled.</summary>
    public static async Task<IReadOnlyList<string>?> Ask(Window owner, string title, IReadOnlyList<CurrencyChoice> choices) =>
        await new AddChartDialog(title, choices).ShowDialog<List<string>?>(owner);
}
