using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>A filter chip of the A group, with its remove command.</summary>
public sealed class FilterChipViewModel(RunFilter filter, Action<RunFilter> remove)
{
    public RunFilter Filter => filter;
    public string Text => filter.Text;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => remove(filter));
}

/// <summary>What an Analyze chart draws (Controls/AnalyzeChart).</summary>
public enum ChartKind
{
    /// <summary>Bars per run (<see cref="ChartData.Values"/>, left axis) and their running total (<see cref="ChartData.Values2"/>, right axis).</summary>
    BarsAndCumulative,
    /// <summary>One running total (<see cref="ChartData.Values"/>).</summary>
    Cumulative,
    /// <summary>Bars per run in run order, A filled and B outlined (<see cref="ChartData.IsA"/>).</summary>
    GroupBars,
    /// <summary>Running totals of A (<see cref="ChartData.Values"/>) and B (<see cref="ChartData.Values2"/>) by nth run of the group.</summary>
    GroupCumulative,
}

/// <summary>The series of one Analyze chart; <see cref="Labels"/> name the x positions.</summary>
public sealed record ChartData(ChartKind Kind, IReadOnlyList<string> Labels, IReadOnlyList<double> Values, IReadOnlyList<double> Values2,
    IReadOnlyList<bool> IsA, string Unit);

/// <summary>A chart card: a title, a summary line and one chart, or two in A vs B (per run, then cumulative).</summary>
public sealed class AnalyzeChartViewModel
{
    public required string Title { get; init; }
    public string SummaryText { get; init; } = "";
    /// <summary>A vs B: "▲ 13%"; never color alone.</summary>
    public string ChangeText { get; init; } = "";
    public bool IsUp { get; init; }
    public bool IsDown { get; init; }
    public string LegendText { get; init; } = "";
    public required ChartData Chart { get; init; }
    /// <summary>The cumulative chart below the per run one (A vs B value charts).</summary>
    public ChartData? Chart2 { get; init; }
    public bool HasChart2 => Chart2 is not null;
    public string ChartCaption { get; init; } = "";
    public string Chart2Caption { get; init; } = "";
    public bool HasCaptions => HasChart2;
    /// <summary>The custom chart behind a card added by hand; null for the built-in ones.</summary>
    public CustomChart? Custom { get; init; }
    public bool IsCustom => Custom is not null;
    public IRelayCommand? EditCommand { get; init; }
    public IRelayCommand? RemoveCommand { get; init; }
}

/// <summary>A row of the currencies table; unticking Use leaves the currency out of the values (saved per session).</summary>
public sealed partial class AnalyzeCurrencyRow : ObservableObject
{
    private readonly Action<string, bool> _setUsed;

    public AnalyzeCurrencyRow(string name, CurrencyDisplay display, bool used, Action<string, bool> setUsed)
    {
        _setUsed = setUsed;
        Name = name;
        Code = display.Code;
        Badge = display.Badge;
        Image = display.Image;
        _isUsed = used;
        UseLabel = $"Use {name}";
    }

    public string Name { get; }
    public string Code { get; }
    public string? Badge { get; }
    public string? Image { get; }
    public string UseLabel { get; }
    public string CountText { get; init; } = "";
    public string ValueText { get; init; } = "";
    public string CountPerRunText { get; init; } = "";
    public string ValuePerRunText { get; init; } = "";
    public string ChangeText { get; init; } = "";
    public bool IsUp { get; init; }
    public bool IsDown { get; init; }
    public double RowOpacity => IsUsed ? 1 : 0.5;

    private bool _isUsed;

    public bool IsUsed
    {
        get => _isUsed;
        set
        {
            if (!SetProperty(ref _isUsed, value))
                return;
            OnPropertyChanged(nameof(RowOpacity));
            _setUsed(Name, value);
        }
    }
}

/// <summary>A row of the runs table. Unticking Use excludes the run from every statistic (stored with the run).</summary>
public sealed partial class AnalyzeRunRow : ObservableObject
{
    private readonly Func<Guid, bool, Task>? _setIncluded;

    public AnalyzeRunRow(ScopedRun run, string tags, Func<Guid, bool, Task>? setIncluded)
    {
        _setIncluded = setIncluded;
        Id = run.Run.Id;
        NumberText = run.Run.Number.ToString(CultureInfo.InvariantCulture);
        SetupText = run.Run.SetupText;
        TagsText = tags;
        _isIncludedByHand = !run.Excluded;
        IncludeLabel = $"Use run {NumberText}";
    }

    public Guid Id { get; }
    public string NumberText { get; }
    public string SetupText { get; }
    public string CountText { get; init; } = "";
    public string ValueText { get; init; } = "";
    public string TagsText { get; }
    /// <summary>"A", "B" in A vs B (empty otherwise or when not used).</summary>
    public string Group { get; init; } = "";
    public bool IsA => Group == "A";
    public bool IsB => Group == "B";
    public string GroupLabel => Group.Length == 0 ? "" : $"In group {Group}";
    /// <summary>Accessible name of the checkbox.</summary>
    public string IncludeLabel { get; }
    public double RowOpacity => IsIncludedByHand ? 1 : 0.5;

    private bool _isIncludedByHand;

    /// <summary>The Use checkbox: not excluded by hand.</summary>
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

/// <summary>A currency offered by the Add chart dialog.</summary>
public sealed partial class CurrencyChoice(string name, CurrencyDisplay display, string valueText, bool chosen) : ObservableObject
{
    public string Name => name;
    public string Code => display.Code;
    public string? Badge => display.Badge;
    public string? Image => display.Image;
    public string ValueText => valueText;

    [ObservableProperty]
    public partial bool IsChosen { get; set; } = chosen;
}

/// <summary>
/// The Analyze tab of a mapping session. ALL shows the used runs; A vs B splits them by filters (A = the runs matching
/// every filter, B = the rest; no filter = first half vs second half). Left: the currencies (Use, saved per session)
/// and the runs (Use, stored with the run). Right: the value charts, the top three currencies by value, and charts
/// added by hand (saved per session). Values are in Divine, Exalted or Chaos Orbs from the poe.ninja prices.
/// </summary>
public sealed partial class AnalyzeViewModel : ObservableObject
{
    public const int TopCount = 3;

    private readonly MappingService _mapping;
    private CurrencyBook _book;
    private readonly ILogger _log;
    private readonly List<RunFilter> _aFilters = [];
    private readonly HashSet<string> _unused = new(StringComparer.Ordinal);
    private readonly List<CustomChart> _custom = [];
    private MapSession _session;
    private bool _recording;
    private DateTimeOffset _now;
    private Analysis? _analysis;

    public AnalyzeViewModel(MappingService mapping, Guid sessionId, CurrencyBook book, ILogger logger)
    {
        _mapping = mapping;
        _book = book;
        _log = logger;
        _session = new MapSession(sessionId);
        SessionId = sessionId;
        Unit = book.Unit is PriceUnit.Adaptive ? PriceUnit.Divine : book.Unit;
    }

    // Supplied by the window.
    /// <summary>(title, help, suggestions) → the typed filter text, or null when canceled.</summary>
    public Func<string, string, IReadOnlyList<string>, Task<string?>>? AskFilter { get; set; }
    /// <summary>(title, currencies) → the chosen currency names, or null when canceled.</summary>
    public Func<string, IReadOnlyList<CurrencyChoice>, Task<IReadOnlyList<string>?>>? AskCurrencies { get; set; }
    public Action<string>? ReportError { get; set; }

    public Guid SessionId { get; }

    /// <summary>Reads the session's saved layout (unused currencies, charts added by hand) and shows it.</summary>
    public async Task LoadLayout()
    {
        try
        {
            var layout = await _mapping.LoadAnalyzeLayout(SessionId);
            _unused.Clear();
            _unused.UnionWith(layout.UnusedCurrencies);
            _custom.Clear();
            _custom.AddRange(layout.Charts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the Analyze layout failed");
        }
        Build();
    }

    /// <summary>The settings changed the codes or the prices: rebuilds.</summary>
    public void SetBook(CurrencyBook book)
    {
        _book = book;
        Build();
    }

    /// <summary>Shows a new copy of the session's mapping data.</summary>
    public void Update(MapSession session, bool recording, DateTimeOffset now)
    {
        _session = session;
        _recording = recording;
        _now = now;
        Build();
    }

    // --- top row -----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(ShowHalves))]
    public partial bool IsCompare { get; set; }

    public bool IsAll { get => !IsCompare; set => IsCompare = !value; }

    partial void OnIsCompareChanged(bool value) => Build();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDivine), nameof(IsExalted), nameof(IsChaos), nameof(UnitText))]
    public partial PriceUnit Unit { get; set; }

    partial void OnUnitChanged(PriceUnit value) => Build();

    public bool IsDivine { get => Unit == PriceUnit.Divine; set { if (value) Unit = PriceUnit.Divine; } }
    public bool IsExalted { get => Unit == PriceUnit.Exalted; set { if (value) Unit = PriceUnit.Exalted; } }
    public bool IsChaos { get => Unit == PriceUnit.Chaos; set { if (value) Unit = PriceUnit.Chaos; } }
    public string UnitText => Format.UnitCode(Unit);

    [ObservableProperty]
    public partial string UsedText { get; set; } = "0";

    [ObservableProperty]
    public partial string ScopeText { get; set; } = "of 0 runs used";

    /// <summary>Title bar summary: "23 runs · 2h 14m".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial bool HasRuns { get; set; }

    public bool HasNoRuns => !HasRuns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingUsed))]
    public partial bool HasUsedRuns { get; set; }

    public bool HasNothingUsed => HasRuns && !HasUsedRuns;

    [ObservableProperty]
    public partial bool HasNoPrices { get; set; }

    // --- A vs B ------------------------------------------------------------------------------------

    /// <summary>The filters that define A (they combine with AND).</summary>
    public ObservableCollection<FilterChipViewModel> AFilters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHalves))]
    public partial bool HasAFilters { get; set; }

    /// <summary>No filter: the dashed "first half" / "second half" chips.</summary>
    public bool ShowHalves => IsCompare && !HasAFilters;

    [ObservableProperty]
    public partial string FirstHalfText { get; set; } = "";

    [ObservableProperty]
    public partial string SecondHalfText { get; set; } = "";

    [ObservableProperty]
    public partial string SplitAText { get; set; } = "";

    [ObservableProperty]
    public partial string SplitBText { get; set; } = "";

    /// <summary>"small samples", or why there is nothing to compare.</summary>
    [ObservableProperty]
    public partial string SplitNote { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSplitSmall { get; set; }

    [ObservableProperty]
    public partial bool IsSplitEmpty { get; set; }

    public void AddAFilter(RunFilter filter)
    {
        if (_aFilters.Contains(filter))
            return;
        _aFilters.Add(filter);
        Build();
    }

    public void RemoveAFilter(RunFilter filter)
    {
        if (_aFilters.Remove(filter))
            Build();
    }

    // --- currencies --------------------------------------------------------------------------------

    public ObservableCollection<AnalyzeCurrencyRow> Currencies { get; } = [];

    [ObservableProperty]
    public partial string CurrencyCountText { get; set; } = "";

    [ObservableProperty]
    public partial string TotalValueText { get; set; } = "";

    /// <summary>The header checkbox: all used (true), none (false) or some (null). Ticking it from "some" uses all.</summary>
    public bool? AllCurrenciesUsed
    {
        get => Currencies.Count == 0 || Currencies.All(c => c.IsUsed) ? true : Currencies.Any(c => c.IsUsed) ? null : false;
        set
        {
            var useAll = value == true || AllCurrenciesUsed is null;
            if (useAll)
                _unused.Clear();
            else
                _unused.UnionWith(Currencies.Select(c => c.Name));
            SaveLayout();
            Build();
        }
    }

    // --- runs --------------------------------------------------------------------------------------

    public ObservableCollection<AnalyzeRunRow> Runs { get; } = [];

    // --- charts ------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial AnalyzeChartViewModel? ValueChart { get; set; }

    public ObservableCollection<AnalyzeChartViewModel> TopCharts { get; } = [];

    public ObservableCollection<AnalyzeChartViewModel> CustomCharts { get; } = [];

    [ObservableProperty]
    public partial bool HasCustomCharts { get; set; }

    // --- building ----------------------------------------------------------------------------------

    private bool IsUsedCurrency(string name) => !_unused.Contains(name);

    private double? Price(string name, int count) => _book.ValueOf(name, count)?.In(Unit).Amount;

    private void Build()
    {
        _analysis = Analysis.Of(_session, []);
        var all = _analysis.Runs.Count;
        var used = _analysis.Included.Count;
        HasRuns = all > 0;
        HasUsedRuns = used > 0;
        HasNoPrices = !_book.Prices.HasPrices;
        UsedText = used.ToString(CultureInfo.InvariantCulture);
        ScopeText = $"of {all} run{(all == 1 ? "" : "s")} used";
        var span = _session.Runs.Count == 0 ? TimeSpan.Zero : _session.EndOf(_session.Runs[^1], _recording, _now) - _session.Runs[0].StartedAt;
        SummaryText = $"{all} run{(all == 1 ? "" : "s")} · {Format.HoursMinutes(span)}";

        AFilters.Clear();
        foreach (var f in _aFilters)
            AFilters.Add(new FilterChipViewModel(f, RemoveAFilter));
        HasAFilters = _aFilters.Count > 0;

        var split = IsCompare ? _analysis.Split(_aFilters) : null;
        BuildSplit(split);
        BuildCurrencies(split);
        BuildRuns(split);
        BuildCharts(split);
    }

    private void BuildSplit(RunSplit? split)
    {
        if (split is null)
        {
            SplitAText = SplitBText = SplitNote = FirstHalfText = SecondHalfText = "";
            IsSplitSmall = IsSplitEmpty = false;
            return;
        }
        SplitAText = $"A: {RunsText(split.A.Count)}";
        SplitBText = $"B: {RunsText(split.B.Count)}";
        FirstHalfText = $"first half · {RangeText(split.A)}";
        SecondHalfText = $"second half · {RangeText(split.B)}";
        IsSplitEmpty = split.HasEmptySide;
        IsSplitSmall = !split.HasEmptySide && split.IsSmallSample;
        SplitNote = split.A.Count == 0 ? "no used run matches A"
            : split.B.Count == 0 ? "every used run is in A"
            : split.IsSmallSample ? "small samples" : "";

        static string RangeText(IReadOnlyList<ScopedRun> runs) => runs.Count switch
        {
            0 => "no runs",
            1 => $"run {runs[0].Run.Number}",
            _ => $"runs {runs[0].Run.Number}–{runs[^1].Run.Number}",
        };
    }

    private void BuildCurrencies(RunSplit? split)
    {
        Currencies.Clear();
        if (_analysis is null)
            return;
        var runs = _analysis.Included;
        var statsA = split is null ? null : Analysis.Statistics(split.A).ToDictionary(s => s.Name);
        var statsB = split is null ? null : Analysis.Statistics(split.B).ToDictionary(s => s.Name);
        var rows = Analysis.Statistics(runs)
            .Select(s => (Stat: s, Value: Price(s.Name, s.Total)))
            .OrderByDescending(r => r.Value.HasValue).ThenByDescending(r => r.Value ?? 0).ThenByDescending(r => r.Stat.Total)
            .ThenBy(r => r.Stat.Name, StringComparer.Ordinal);

        double total = 0;
        foreach (var (stat, value) in rows)
        {
            var isUsed = IsUsedCurrency(stat.Name);
            if (isUsed)
                total += value ?? 0;
            var row = split is null
                ? new AnalyzeCurrencyRow(stat.Name, _book.Describe(stat.Name), isUsed, SetCurrencyUsed)
                {
                    CountText = Count(stat.Total),
                    ValueText = Format.Amount(value),
                    CountPerRunText = PerRun(stat.Average),
                    ValuePerRunText = Format.Amount(value / runs.Count),
                }
                : CompareRow(stat, value, isUsed, split, statsA!, statsB!);
            Currencies.Add(row);
        }
        CurrencyCountText = Currencies.Count == 0 ? "" : $"{Currencies.Count} · {Currencies.Count(c => c.IsUsed)} used";
        TotalValueText = Format.Amount(total);
        OnPropertyChanged(nameof(AllCurrenciesUsed));
    }

    private AnalyzeCurrencyRow CompareRow(CurrencyStat stat, double? value, bool isUsed, RunSplit split,
        Dictionary<string, CurrencyStat> statsA, Dictionary<string, CurrencyStat> statsB)
    {
        var countA = statsA.GetValueOrDefault(stat.Name)?.Total ?? 0;
        var countB = statsB.GetValueOrDefault(stat.Name)?.Total ?? 0;
        double? perRunA = split.A.Count == 0 ? null : (double)countA / split.A.Count;
        double? perRunB = split.B.Count == 0 ? null : (double)countB / split.B.Count;
        var valueA = perRunA is { } a && Price(stat.Name, 1) is { } unit ? a * unit : (double?)null;
        var valueB = perRunB is { } b && Price(stat.Name, 1) is { } unit2 ? b * unit2 : (double?)null;
        var (change, up, down) = Change(valueA, valueB);
        return new AnalyzeCurrencyRow(stat.Name, _book.Describe(stat.Name), isUsed, SetCurrencyUsed)
        {
            CountText = Count(stat.Total),
            ValueText = Format.Amount(value),
            CountPerRunText = $"{PerRunOrDash(perRunA)} · {PerRunOrDash(perRunB)}",
            ValuePerRunText = value is null ? "–" : $"{Format.Amount(valueA)} · {Format.Amount(valueB)}",
            ChangeText = change,
            IsUp = up,
            IsDown = down,
        };
    }

    private void BuildRuns(RunSplit? split)
    {
        Runs.Clear();
        if (_analysis is null)
            return;
        foreach (var run in Enumerable.Reverse(_analysis.Runs))
        {
            var group = split is null || !run.Included ? "" : split.IsA(run) ? "A" : "B";
            Runs.Add(new AnalyzeRunRow(run, string.Join(", ", _session.TagsOf(run.Run.Id)), SetIncluded)
            {
                CountText = Count(run.CountOf(IsUsedCurrency)),
                ValueText = Format.Amount(run.ValueOf(IsUsedCurrency, Price)),
                Group = group,
            });
        }
    }

    private void BuildCharts(RunSplit? split)
    {
        TopCharts.Clear();
        CustomCharts.Clear();
        if (_analysis is null || _analysis.Included.Count == 0)
        {
            ValueChart = null;
            HasCustomCharts = false;
            return;
        }

        ValueChart = Chart("Value", IsUsedCurrency, split, withBars: true, custom: null);

        var top = Analysis.Statistics(_analysis.Included)
            .Where(s => IsUsedCurrency(s.Name))
            .Select(s => (s.Name, Value: Price(s.Name, s.Total) ?? 0))
            .Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(TopCount);
        foreach (var (name, _) in top)
            TopCharts.Add(Chart(name, n => n == name, split, withBars: false, custom: null));

        foreach (var custom in _custom)
        {
            var names = custom.Currencies.ToHashSet(StringComparer.Ordinal);
            CustomCharts.Add(Chart(TitleOf(custom), names.Contains, split, withBars: true, custom));
        }
        HasCustomCharts = CustomCharts.Count > 0;
    }

    /// <summary>A chart of the value of the currencies <paramref name="use"/> accepts.</summary>
    private AnalyzeChartViewModel Chart(string title, Func<string, bool> use, RunSplit? split, bool withBars, CustomChart? custom)
    {
        var runs = _analysis!.Included;
        var unit = UnitText;
        var perRun = runs.Select(r => r.ValueOf(use, Price)).ToList();
        var labels = runs.Select(r => r.Run.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        var edit = custom is null ? null : new AsyncRelayCommand(() => EditChart(custom));
        var remove = custom is null ? null : new RelayCommand(() => RemoveChart(custom));

        if (split is null)
        {
            var total = perRun.Sum();
            return new AnalyzeChartViewModel
            {
                Title = title,
                SummaryText = $"{Format.Amount(total)} {unit} · {Format.Amount(total / runs.Count)} {unit}/run",
                LegendText = withBars ? "bars: per run (left) · line: running total (right)" : "running total",
                Chart = withBars
                    ? new ChartData(ChartKind.BarsAndCumulative, labels, perRun, Analysis.Cumulative(perRun), [], unit)
                    : new ChartData(ChartKind.Cumulative, labels, Analysis.Cumulative(perRun), [], [], unit),
                Custom = custom,
                EditCommand = edit,
                RemoveCommand = remove,
            };
        }

        var isA = runs.Select(split.IsA).ToList();
        var a = perRun.Where((_, i) => isA[i]).ToList();
        var b = perRun.Where((_, i) => !isA[i]).ToList();
        double? avgA = a.Count == 0 ? null : a.Average();
        double? avgB = b.Count == 0 ? null : b.Average();
        var (change, up, down) = Change(avgA, avgB);
        var cumulative = new ChartData(ChartKind.GroupCumulative,
            Enumerable.Range(1, Math.Max(a.Count, b.Count)).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList(),
            Analysis.Cumulative(a), Analysis.Cumulative(b), [], unit);
        return new AnalyzeChartViewModel
        {
            Title = title,
            SummaryText = $"A {Format.Amount(avgA)} vs B {Format.Amount(avgB)} {unit}/run",
            ChangeText = change,
            IsUp = up,
            IsDown = down,
            LegendText = withBars ? "A filled · B outlined; A solid · B dashed" : "running total: A solid · B dashed",
            Chart = withBars ? new ChartData(ChartKind.GroupBars, labels, perRun, [], isA, unit) : cumulative,
            Chart2 = withBars ? cumulative : null,
            ChartCaption = "Per run",
            Chart2Caption = "Running total · by nth run of the group",
            Custom = custom,
            EditCommand = edit,
            RemoveCommand = remove,
        };
    }

    /// <summary>"ChO, DIV +2": the codes of the chart's first two currencies, then how many more.</summary>
    private string TitleOf(CustomChart chart)
    {
        var codes = chart.Currencies.Select(n => _book.Describe(n) is var d && d.Badge is { } b ? $"{d.Code}/{b}" : d.Code).ToList();
        return codes.Count <= 2 ? string.Join(", ", codes) : $"{string.Join(", ", codes.Take(2))} +{codes.Count - 2}";
    }

    private static (string Text, bool Up, bool Down) Change(double? a, double? b) => (a, b) switch
    {
        (null, _) or (_, null) => ("", false, false),
        ({ } x, 0) when x > 0 => ("▲ new", true, false),
        (_, 0) => ("", false, false),
        ({ } x, { } y) when x > y => ($"▲ {Percent((x - y) / y)}", true, false),
        ({ } x, { } y) when x < y => ($"▼ {Percent((y - x) / y)}", false, true),
        _ => ("= 0%", false, false),
    };

    private static string RunsText(int n) => n == 1 ? "1 run" : $"{n} runs";
    private static string Count(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);
    private static string PerRun(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string PerRunOrDash(double? value) => value is { } v ? PerRun(v) : "–";
    private static string Percent(double fraction) => $"{Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture)}%";

    // --- commands ----------------------------------------------------------------------------------

    private void SetCurrencyUsed(string name, bool used)
    {
        if (used ? !_unused.Remove(name) : !_unused.Add(name))
            return;
        SaveLayout();
        Build();
    }

    private Task SetIncluded(Guid runId, bool included) =>
        Do("Changing the used runs", () => _mapping.SetRunExcluded(SessionId, runId, !included));

    [RelayCommand]
    private async Task AddAFilterAsk()
    {
        if (await AskFor("A vs B: A is the used runs that match") is { } filter)
            AddAFilter(filter);
    }

    [RelayCommand]
    private async Task AddChart()
    {
        if (await AskForCurrencies("Add chart", []) is { Count: > 0 } names)
            AddCustomChart(names);
    }

    /// <summary>Adds a chart of the value of <paramref name="names"/> (saved with the session).</summary>
    public void AddCustomChart(IReadOnlyList<string> names)
    {
        _custom.Add(new CustomChart { Currencies = [.. names] });
        SaveLayout();
        Build();
    }

    private async Task EditChart(CustomChart chart)
    {
        if (await AskForCurrencies("Edit chart", chart.Currencies) is not { Count: > 0 } names)
            return;
        var index = _custom.IndexOf(chart);
        if (index < 0)
            return;
        _custom[index] = chart with { Currencies = names };
        SaveLayout();
        Build();
    }

    private void RemoveChart(CustomChart chart)
    {
        if (!_custom.Remove(chart))
            return;
        SaveLayout();
        Build();
    }

    /// <summary>The currencies of the used runs, by value, with the given ones ticked.</summary>
    private Task<IReadOnlyList<string>?> AskForCurrencies(string title, IReadOnlyList<string> chosen)
    {
        if (AskCurrencies is null || _analysis is null)
            return Task.FromResult<IReadOnlyList<string>?>(null);
        var choices = Analysis.Statistics(_analysis.Included).Select(s => s.Name)
            .Concat(chosen.Where(n => _analysis.Included.All(r => r.CountOf(n) == 0)))
            .Select(n => (Name: n, Value: Price(n, _analysis.Included.Sum(r => r.CountOf(n)))))
            .OrderByDescending(c => c.Value.HasValue).ThenByDescending(c => c.Value ?? 0).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new CurrencyChoice(c.Name, _book.Describe(c.Name), c.Value is { } v ? $"{Format.Amount(v)} {UnitText}" : "–", chosen.Contains(c.Name)))
            .ToList();
        return AskCurrencies(title, choices);
    }

    private void SaveLayout()
    {
        var layout = new AnalyzeLayout { UnusedCurrencies = [.. _unused.Order(StringComparer.Ordinal)], Charts = [.. _custom] };
        _ = Do("Saving the Analyze layout", () => _mapping.SaveAnalyzeLayout(SessionId, layout));
    }

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
