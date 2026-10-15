using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>A filter chip of the scope bar (or the compare split), with its remove command.</summary>
public sealed class FilterChipViewModel(RunFilter filter, Action<RunFilter> remove)
{
    public RunFilter Filter => filter;
    public string Text => filter.Text;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => remove(filter));
}

/// <summary>A row of the currencies table: code, name, a bar relative to the largest value, total, average, hit rate.</summary>
public sealed record CurrencyStatRow(string Code, string? Badge, string Name, string TotalText, string AverageText, string HitText, double Bar, string? Image = null);

/// <summary>A row of the compare table; a change shows ▲ or ▼ (never color alone).</summary>
public sealed record CompareRow(string Code, string? Badge, string Name, string AText, string BText, string ChangeText, bool IsUp, bool IsDown, string? Image = null);

/// <summary>A row of the Analyse runs table. Unchecking USE excludes the run from every statistic.</summary>
public sealed partial class AnalyseRunRow : ObservableObject
{
    private readonly Func<Guid, bool, Task>? _setIncluded;

    public AnalyseRunRow(ScopedRun run, string duration, IReadOnlyList<string> values, string tags, Func<Guid, bool, Task>? setIncluded)
    {
        _setIncluded = setIncluded;
        Id = run.Run.Id;
        NumberText = run.Run.Number.ToString(CultureInfo.InvariantCulture);
        DurationText = duration;
        SetupText = run.Run.SetupText;
        Values = values;
        FilteredOut = run.FilteredOut;
        _isIncludedByHand = !run.Excluded;
        TagsText = string.Join(" · ", new[]
        {
            tags,
            run.Excluded ? "excluded" : "",
            run.FilteredOut ? "filtered out" : "",
        }.Where(t => t.Length > 0));
        IncludeLabel = $"Include run {NumberText}";
    }

    public Guid Id { get; }
    public string NumberText { get; }
    public string DurationText { get; }
    public string SetupText { get; }
    /// <summary>Counts of the table's currency columns.</summary>
    public IReadOnlyList<string> Values { get; }
    public string Value0 => Values.ElementAtOrDefault(0) ?? "";
    public string Value1 => Values.ElementAtOrDefault(1) ?? "";
    public string Value2 => Values.ElementAtOrDefault(2) ?? "";
    public string TagsText { get; }
    public bool FilteredOut { get; }
    /// <summary>Accessible name of the checkbox.</summary>
    public string IncludeLabel { get; }
    public double RowOpacity => IsIncludedByHand && !FilteredOut ? 1 : 0.5;

    private bool _isIncludedByHand;

    /// <summary>The USE checkbox: not excluded by hand.</summary>
    public bool IsIncludedByHand
    {
        get => _isIncludedByHand;
        set
        {
            if (!SetProperty(ref _isIncludedByHand, value))
                return;
            OnPropertyChanged(nameof(RowOpacity));
            _ = _setIncluded?.Invoke(Id, value);
        }
    }
}

/// <summary>
/// The Analyse tab of a mapping session: the scope ("N of M runs included", filters,
/// manual include), per-currency statistics, a per-run chart of one currency, Compare A vs B and the runs table.
/// Filters live with the window; the manual exclusion is stored with the run.
/// </summary>
public sealed partial class AnalyseViewModel : ObservableObject
{
    private readonly MappingService _mapping;
    private CurrencyBook _book;
    private readonly ILogger _log;
    private readonly List<RunFilter> _filters = [];
    private MapSession _session;
    private bool _recording;
    private DateTimeOffset _now;
    private Analysis? _analysis;
    private RunFilter? _compareBy;
    private IReadOnlyList<CurrencyCellViewModel> _cells = [];

    public AnalyseViewModel(MappingService mapping, Guid sessionId, CurrencyBook book, ILogger logger)
    {
        _mapping = mapping;
        _book = book;
        _log = logger;
        _session = new MapSession(sessionId);
        SessionId = sessionId;
        BuildCells();
    }

    /// <summary>The settings changed the codes or the loot grid: rebuilds the grid and refills it.</summary>
    public void SetBook(CurrencyBook book)
    {
        _book = book;
        BuildCells();
        BuildCurrencies();
    }

    private void BuildCells()
    {
        LootRows = CurrencyCellViewModel.RowsOf(_book);
        _cells = LootRows.SelectMany(r => r.Cells).Where(c => !c.IsPlaceholder).ToList();
    }

    // Supplied by the window.
    /// <summary>(title, help, suggestions) → the typed filter text, or null when canceled.</summary>
    public Func<string, string, IReadOnlyList<string>, Task<string?>>? AskFilter { get; set; }
    public Action<string>? ReportError { get; set; }

    public Guid SessionId { get; }

    // --- scope -------------------------------------------------------------------------------------

    public ObservableCollection<FilterChipViewModel> Filters { get; } = [];

    [ObservableProperty]
    public partial string IncludedText { get; set; } = "0";

    [ObservableProperty]
    public partial string ScopeText { get; set; } = "of 0 runs included";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial bool HasRuns { get; set; }

    public bool HasNoRuns => !HasRuns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingIncluded))]
    public partial bool HasIncluded { get; set; }

    public bool HasNothingIncluded => HasRuns && !HasIncluded;

    /// <summary>Title bar summary: "23 runs · 2h 14m".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    // --- currencies --------------------------------------------------------------------------------

    public ObservableCollection<CurrencyStatRow> Currencies { get; } = [];
    /// <summary>The condensed grid: rows of nine cells in three groups; a placeholder is an empty place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLootGrid), nameof(HasNoLootGrid))]
    public partial IReadOnlyList<LootRowViewModel> LootRows { get; private set; } = [];

    public bool HasLootGrid => LootRows.Count > 0;
    public bool HasNoLootGrid => !HasLootGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid))]
    public partial bool ShowList { get; set; } = true;

    public bool ShowGrid { get => !ShowList; set => ShowList = !value; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortByTotal))]
    public partial bool SortByAverage { get; set; }

    public bool SortByTotal { get => !SortByAverage; set => SortByAverage = !value; }

    partial void OnSortByAverageChanged(bool value) => BuildCurrencies();

    // --- per run chart -----------------------------------------------------------------------------

    public ObservableCollection<string> ChartCurrencies { get; } = [];

    [ObservableProperty]
    public partial string? ChartCurrency { get; set; }

    partial void OnChartCurrencyChanged(string? value) => BuildChart();

    [ObservableProperty]
    public partial IReadOnlyList<double> ChartValues { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> ChartLabels { get; set; } = [];

    [ObservableProperty]
    public partial double ChartAverage { get; set; }

    [ObservableProperty]
    public partial string ChartCaption { get; set; } = "";

    // --- compare -----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCompare))]
    public partial bool HasCompare { get; set; }

    public bool HasNoCompare => !HasCompare;

    [ObservableProperty]
    public partial string CompareAText { get; set; } = "";

    [ObservableProperty]
    public partial string CompareBText { get; set; } = "";

    [ObservableProperty]
    public partial string CompareNote { get; set; } = "";

    public ObservableCollection<CompareRow> CompareRows { get; } = [];

    // --- runs table --------------------------------------------------------------------------------

    public ObservableCollection<AnalyseRunRow> Runs { get; } = [];

    /// <summary>Headers of the runs table's currency columns: the scope's top three by total.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ValueHeaders { get; set; } = ["", "", ""];

    public string ValueHeader0 => ValueHeaders.ElementAtOrDefault(0) ?? "";
    public string ValueHeader1 => ValueHeaders.ElementAtOrDefault(1) ?? "";
    public string ValueHeader2 => ValueHeaders.ElementAtOrDefault(2) ?? "";

    partial void OnValueHeadersChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(ValueHeader0));
        OnPropertyChanged(nameof(ValueHeader1));
        OnPropertyChanged(nameof(ValueHeader2));
    }

    // --- building ----------------------------------------------------------------------------------

    /// <summary>Shows a new copy of the session's mapping data.</summary>
    public void Update(MapSession session, bool recording, DateTimeOffset now)
    {
        _session = session;
        _recording = recording;
        _now = now;
        Build();
    }

    /// <summary>The scope's filters (a copy).</summary>
    public IReadOnlyList<RunFilter> ScopeFilters => [.. _filters];

    public void AddFilter(RunFilter filter)
    {
        if (_filters.Contains(filter))
            return;
        _filters.Add(filter);
        Build();
    }

    public void RemoveFilter(RunFilter filter)
    {
        if (_filters.Remove(filter))
            Build();
    }

    /// <summary>Compare A (runs matching <paramref name="filter"/>) with B (the rest of the scope); null hides the compare.</summary>
    public void CompareBy(RunFilter? filter)
    {
        _compareBy = filter;
        BuildCompare();
    }

    private void Build()
    {
        _analysis = Analysis.Of(_session, _filters);
        var all = _analysis.Runs.Count;
        var included = _analysis.Included.Count;
        HasRuns = all > 0;
        HasIncluded = included > 0;
        IncludedText = included.ToString(CultureInfo.InvariantCulture);
        ScopeText = $"of {all} run{(all == 1 ? "" : "s")} included";
        var span = _session.Runs.Count == 0 ? TimeSpan.Zero : _session.EndOf(_session.Runs[^1], _recording, _now) - _session.Runs[0].StartedAt;
        SummaryText = $"{all} run{(all == 1 ? "" : "s")} · {Format.HoursMinutes(span)}";

        Filters.Clear();
        foreach (var f in _filters)
            Filters.Add(new FilterChipViewModel(f, RemoveFilter));

        BuildCurrencies();
        BuildChartChoices();
        BuildCompare();
        BuildRuns();
    }

    private void BuildCurrencies()
    {
        Currencies.Clear();
        if (_analysis is null)
            return;
        var stats = _analysis.SortedBy(SortByAverage);
        var max = stats.Count == 0 ? 1 : Math.Max(1, stats.Max(s => SortByAverage ? s.Average : s.Total));
        foreach (var s in stats)
        {
            var display = _book.Describe(s.Name);
            Currencies.Add(new CurrencyStatRow(display.Code, display.Badge, s.Name, s.Total.ToString("#,0", CultureInfo.InvariantCulture),
                Average(s.Average), $"{s.Hits}/{s.Runs}", (SortByAverage ? s.Average : s.Total) / max, display.Image));
        }

        var byName = stats.ToDictionary(s => s.Name);
        foreach (var cell in _cells)
        {
            var stat = byName.GetValueOrDefault(cell.Name!);
            cell.Count = stat?.Total ?? 0;
            cell.DisplayText = SortByAverage && stat is not null ? Average(stat.Average) : null;
        }
    }

    private void BuildChartChoices()
    {
        var names = _analysis?.Currencies.Select(c => c.Name).ToList() ?? [];
        var keep = ChartCurrency is { } current && names.Contains(current) ? current : names.FirstOrDefault();
        ChartCurrencies.Clear();
        foreach (var n in names)
            ChartCurrencies.Add(n);
        if (ChartCurrency == keep)
            BuildChart();
        else
            ChartCurrency = keep; // builds the chart
    }

    private void BuildChart()
    {
        if (_analysis is null || ChartCurrency is not { } name)
        {
            ChartValues = [];
            ChartLabels = [];
            ChartAverage = 0;
            ChartCaption = "";
            return;
        }
        var (values, average) = _analysis.PerRun(name);
        ChartValues = values.Select(v => (double)v.Count).ToList();
        ChartLabels = values.Select(v => v.Run.ToString(CultureInfo.InvariantCulture)).ToList();
        ChartAverage = average;
        ChartCaption = $"dashed line = average ({Average(average)})";
    }

    private void BuildCompare()
    {
        CompareRows.Clear();
        HasCompare = _compareBy is not null && _analysis is not null;
        if (!HasCompare)
        {
            CompareAText = CompareBText = CompareNote = "";
            return;
        }

        var comparison = _analysis!.Compare(_compareBy!);
        CompareAText = $"A · {_compareBy!.Text} · {Runs(comparison.RunsA)}";
        CompareBText = $"B · the rest · {Runs(comparison.RunsB)}";
        CompareNote = comparison.IsSmallSample
            ? $"Small samples: differences between {comparison.RunsA} and {comparison.RunsB} runs can be noise."
            : $"A: {comparison.RunsA} runs, B: {comparison.RunsB} runs.";
        foreach (var c in comparison.Currencies.Take(12))
        {
            var display = _book.Describe(c.Name);
            var (text, up, down) = c.Change switch
            {
                null when c.AverageA > 0 => ("▲ new", true, false),
                null => ("–", false, false),
                > 0 => ($"▲ {Percent(c.Change.Value)}", true, false),
                < 0 => ($"▼ {Percent(-c.Change.Value)}", false, true),
                _ => ("= 0%", false, false),
            };
            CompareRows.Add(new CompareRow(display.Code, display.Badge, c.Name, Average(c.AverageA), Average(c.AverageB),
                text, up, down, display.Image));
        }

        static string Runs(int n) => n == 1 ? "1 run" : $"{n} runs";
    }

    private void BuildRuns()
    {
        Runs.Clear();
        if (_analysis is null)
            return;
        var columns = _analysis.Currencies.Take(3).Select(c => c.Name).ToList();
        ValueHeaders = columns.Select(n => _book.Describe(n) is var d && d.Badge is { } b ? $"{d.Code}/{b}" : d.Code)
            .Concat(Enumerable.Repeat("", 3)).Take(3).ToList();
        foreach (var run in Enumerable.Reverse(_analysis.Runs))
        {
            Runs.Add(new AnalyseRunRow(run, Format.Duration(_session.EndOf(run.Run, _recording, _now) - run.Run.StartedAt),
                columns.Select(c => run.CountOf(c).ToString(CultureInfo.InvariantCulture)).ToList(),
                string.Join(", ", _session.TagsOf(run.Run.Id)), SetIncluded));
        }
    }

    private static string Average(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Percent(double fraction) => $"{Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture)}%";

    // --- commands ----------------------------------------------------------------------------------

    private Task SetIncluded(Guid runId, bool included) =>
        Do("Changing the scope", () => _mapping.SetRunExcluded(SessionId, runId, !included));

    [RelayCommand]
    private async Task AddFilterAsk()
    {
        if (await AskFor("Add filter") is { } filter)
            AddFilter(filter);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _filters.Clear();
        Build();
    }

    [RelayCommand]
    private async Task CompareAsk()
    {
        if (await AskFor("Compare A vs B: A is the runs that match") is { } filter)
            CompareBy(filter);
    }

    [RelayCommand]
    private void ClearCompare() => CompareBy(null);

    /// <summary>Asks until the text is a filter (or the dialog is canceled).</summary>
    private async Task<RunFilter?> AskFor(string title)
    {
        if (AskFilter is null)
            return null;
        var suggestions = Suggestions(await _mapping.KnownTags());
        var help = "\"died\", \"!died\", \"rituals >= 5\", \"rituals: 6\", \"runs 5-23\"";
        while (await AskFilter(title, help, suggestions) is { Length: > 0 } text)
        {
            if (RunFilter.Parse(text, out var error) is { } filter)
                return filter;
            help = error!;
        }
        return null;
    }

    /// <summary>Filters made from the tags used before: the name, its negation, and a comparison for numeric values.</summary>
    private static List<string> Suggestions(IReadOnlyList<MapTag> known)
    {
        var result = new List<string>();
        foreach (var tag in known)
        {
            if (tag.Number is { } n)
                result.Add($"{tag.Name} >= {n.ToString(CultureInfo.InvariantCulture)}");
            else if (tag.Value is { } v)
                result.Add($"{tag.Name}: {v}");
            result.Add(tag.Name);
            result.Add($"!{tag.Name}");
        }
        return result.Distinct().ToList();
    }

    private async Task Do(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{What} failed", what);
            ReportError?.Invoke($"{what} failed: {ex.Message}");
        }
    }
}
