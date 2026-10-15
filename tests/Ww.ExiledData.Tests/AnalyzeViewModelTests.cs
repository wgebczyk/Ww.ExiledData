using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

/// <summary>The Analyze tab built from a session's mapping data, with prices.</summary>
public sealed class AnalyzeViewModelTests : IDisposable
{
    private const string Chaos = "Chaos Orb";
    private const string Trans = "Orb of Transmutation";
    private const string GreaterChaos = "Greater Chaos Orb";

    private readonly string _dir = Directory.CreateTempSubdirectory("ww-analyze-").FullName;
    private readonly SqliteStore _core;
    private readonly MappingStore _store;
    private readonly MappingService _service;
    private readonly MapFeed _feed;
    private readonly AnalyzeViewModel _vm;

    /// <summary>Chaos 0.1 div, Transmutation 0.01 div; Greater Chaos Orb has no price.</summary>
    private static readonly CurrencyBook Book = GatherViewModelTests.TestBook.WithPrices(new PriceBook
    {
        League = "Test",
        FetchedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
        ExaltedPerDivine = 100,
        ChaosPerDivine = 10,
        Divine = new Dictionary<string, double> { [Chaos] = 0.1, [Trans] = 0.01 },
    });

    public AnalyzeViewModelTests()
    {
        var path = Path.Combine(_dir, "data.db");
        _core = SqliteStore.Open(path);
        _store = MappingStore.Open(path);
        var session = SessionLifecycle.Start(null, new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero), "farming", SessionKind.Mapping);
        _core.SaveSession(session);
        _feed = new MapFeed { Session = new MapSession(session.Id) };
        _service = new MappingService(_store, new NoStream());
        _vm = NewViewModel();
    }

    public void Dispose()
    {
        _store.Dispose();
        _core.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class NoStream : ICaptureStream
    {
        public event Action<ProcessedCapture>? CaptureProcessed { add { } remove { } }
        public event Action<SweepChanged>? SweepChanged { add { } remove { } }
    }

    private AnalyzeViewModel NewViewModel() => new(_service, _feed.Session.SessionId, Book, NullLogger.Instance);

    private void Show(AnalyzeViewModel? vm = null) => (vm ?? _vm).Update(_feed.Session, recording: false, _feed.Now);

    /// <summary>Run values in div: 1.4, 5.0, 1.2.</summary>
    private void Runs()
    {
        foreach (var (chaos, trans, rituals) in new[] { (10, 40, 6), (50, 0, 7), (10, 20, 2) })
        {
            _feed.Copy(MapItems.Waystone(15, $"of Run {_feed.Session.Runs.Count + 1}"));
            _feed.Stash((Chaos, chaos), (Trans, trans), (GreaterChaos, 1));
            _feed.Session.AddTag(_feed.Session.Runs[^1].Id, $"rituals: {rituals}");
        }
    }

    private static IReadOnlyList<double> Rounded(IEnumerable<double> values) => values.Select(v => Math.Round(v, 6)).ToList();

    private AnalyzeCurrencyRow Currency(string name) => _vm.Currencies.Single(c => c.Name == name);

    [Fact]
    public void Without_runs_there_is_nothing_to_analyze()
    {
        Show();

        Assert.False(_vm.HasRuns);
        Assert.Equal(("0", "of 0 runs used"), (_vm.UsedText, _vm.ScopeText));
        Assert.Empty(_vm.Currencies);
        Assert.Null(_vm.ValueChart);
        Assert.Empty(_vm.TopCharts);
    }

    [Fact]
    public void All_shows_currencies_by_value_runs_and_charts()
    {
        Runs();
        Show();

        Assert.Equal(("3", "of 3 runs used"), (_vm.UsedText, _vm.ScopeText));
        Assert.StartsWith("3 runs · ", _vm.SummaryText);
        Assert.Equal([Chaos, Trans, GreaterChaos], _vm.Currencies.Select(c => c.Name)); // by value; no price last
        var chaos = Currency(Chaos);
        Assert.Equal(("ChO", "70", "7.00", "23.3", "2.33"), (chaos.Code, chaos.CountText, chaos.ValueText, chaos.CountPerRunText, chaos.ValuePerRunText));
        Assert.Equal(("0.600", "–", "–"), (Currency(Trans).ValueText, Currency(GreaterChaos).ValueText, Currency(GreaterChaos).ValuePerRunText));
        Assert.Equal(("7.60", "DIV", true), (_vm.TotalValueText, _vm.UnitText, _vm.AllCurrenciesUsed));

        Assert.Equal(["3", "2", "1"], _vm.Runs.Select(r => r.NumberText)); // newest first
        Assert.Equal(("31", "1.20", "rituals: 2", ""), (_vm.Runs[0].CountText, _vm.Runs[0].ValueText, _vm.Runs[0].TagsText, _vm.Runs[0].Group));

        var value = _vm.ValueChart!;
        Assert.Equal(ChartKind.BarsAndCumulative, value.Chart.Kind);
        Assert.Equal(["1", "2", "3"], value.Chart.Labels);
        Assert.Equal([1.4, 5.0, 1.2], Rounded(value.Chart.Values));
        Assert.Equal([1.4, 6.4, 7.6], Rounded(value.Chart.Values2));
        Assert.Equal("7.60 DIV · 2.53 DIV/run", value.SummaryText);
        Assert.False(value.HasChart2);

        // Top value: the priced currencies only, by value.
        Assert.Equal([Chaos, Trans], _vm.TopCharts.Select(c => c.Title));
        Assert.Equal(ChartKind.Cumulative, _vm.TopCharts[0].Chart.Kind);
        Assert.Equal([1.0, 6.0, 7.0], Rounded(_vm.TopCharts[0].Chart.Values));
    }

    [Fact]
    public void Value_in_switches_the_unit()
    {
        Runs();
        Show();

        _vm.IsChaos = true;

        Assert.Equal(("ChO", "70.0", "76.0"), (_vm.UnitText, Currency(Chaos).ValueText, _vm.TotalValueText));
        Assert.Equal("76.0 ChO · 25.3 ChO/run", _vm.ValueChart!.SummaryText);
    }

    [Fact]
    public void An_unticked_currency_leaves_the_values_and_ticking_all_brings_it_back()
    {
        Runs();
        Show();

        Currency(Chaos).IsUsed = false;

        Assert.Equal(("0.600", (bool?)null, 0.5), (_vm.TotalValueText, _vm.AllCurrenciesUsed, Currency(Chaos).RowOpacity));
        Assert.Equal("7.00", Currency(Chaos).ValueText); // its own row still shows what it is worth
        Assert.Equal(("21", "0.200"), (_vm.Runs[0].CountText, _vm.Runs[0].ValueText));
        Assert.Equal([0.4, 0.0, 0.2], Rounded(_vm.ValueChart!.Chart.Values));
        Assert.Equal([Trans], _vm.TopCharts.Select(c => c.Title));

        _vm.AllCurrenciesUsed = false; // the header box from "some": use all
        Assert.Equal((true, "7.60"), (_vm.AllCurrenciesUsed, _vm.TotalValueText));
        _vm.AllCurrenciesUsed = false; // from "all": none
        Assert.Equal((false, "0"), (_vm.AllCurrenciesUsed, _vm.TotalValueText));
    }

    [Fact]
    public void A_vs_b_without_a_filter_splits_in_halves()
    {
        Runs();
        Show();

        _vm.IsCompare = true;

        Assert.True(_vm.ShowHalves);
        Assert.Equal(("first half · runs 1–2", "second half · run 3"), (_vm.FirstHalfText, _vm.SecondHalfText));
        Assert.Equal(("A: 2 runs", "B: 1 run", "small samples", true), (_vm.SplitAText, _vm.SplitBText, _vm.SplitNote, _vm.IsSplitSmall));
        Assert.Equal(["B", "A", "A"], _vm.Runs.Select(r => r.Group));

        var value = _vm.ValueChart!;
        Assert.Equal(ChartKind.GroupBars, value.Chart.Kind);
        Assert.Equal([true, true, false], value.Chart.IsA);
        Assert.Equal(ChartKind.GroupCumulative, value.Chart2!.Kind);
        Assert.Equal(["1", "2"], value.Chart2.Labels); // nth run of the group
        Assert.Equal([1.4, 6.4], Rounded(value.Chart2.Values));
        Assert.Equal([1.2], Rounded(value.Chart2.Values2));
        Assert.Equal(("A 3.20 vs B 1.20 DIV/run", "▲ 167%", true), (value.SummaryText, value.ChangeText, value.IsUp));
        Assert.Equal(ChartKind.GroupCumulative, _vm.TopCharts[0].Chart.Kind);
    }

    [Fact]
    public void A_filters_define_a_and_the_tables_show_a_and_b_per_run()
    {
        Runs();
        Show();
        _vm.IsCompare = true;

        _vm.AddAFilter(RunFilter.Parse("rituals >= 7", out _)!);

        Assert.False(_vm.ShowHalves);
        Assert.Equal(["Tag: rituals ≥ 7"], _vm.AFilters.Select(f => f.Text));
        Assert.Equal(("A: 1 run", "B: 2 runs"), (_vm.SplitAText, _vm.SplitBText));
        var chaos = Currency(Chaos);
        Assert.Equal(("50.0 · 10.0", "5.00 · 1.00", "▲ 400%", true), (chaos.CountPerRunText, chaos.ValuePerRunText, chaos.ChangeText, chaos.IsUp));
        var trans = Currency(Trans);
        Assert.Equal(("▼ 100%", true), (trans.ChangeText, trans.IsDown));

        _vm.AddAFilter(RunFilter.Parse("rituals >= 99", out _)!); // AND: nothing left in A
        Assert.Equal((true, "no used run matches A"), (_vm.IsSplitEmpty, _vm.SplitNote));

        _vm.AFilters[1].RemoveCommand.Execute(null);
        _vm.AFilters[0].RemoveCommand.Execute(null);
        Assert.True(_vm.ShowHalves);
    }

    [Fact]
    public void An_excluded_run_is_left_out_and_dimmed()
    {
        Runs();
        _feed.Session.SetRunExcluded(_feed.Session.Runs[1].Id, true);
        Show();
        _vm.IsCompare = true;

        Assert.Equal(("2", "of 3 runs used"), (_vm.UsedText, _vm.ScopeText));
        var excluded = _vm.Runs.Single(r => r.NumberText == "2");
        Assert.Equal((0.5, ""), (excluded.RowOpacity, excluded.Group));
        Assert.Equal(["1", "3"], _vm.ValueChart!.Chart.Labels);
    }

    [Fact]
    public async Task Charts_added_by_hand_and_unticked_currencies_are_saved_with_the_session()
    {
        Runs();
        await _vm.LoadLayout();
        Show();
        IReadOnlyList<CurrencyChoice>? offered = null;
        _vm.AskCurrencies = (_, choices) =>
        {
            offered = choices;
            return Task.FromResult<IReadOnlyList<string>?>([Chaos, Trans, GreaterChaos]);
        };

        await _vm.AddChartCommand.ExecuteAsync(null);
        Currency(GreaterChaos).IsUsed = false;

        Assert.Equal([Chaos, Trans, GreaterChaos], offered!.Select(c => c.Name));
        Assert.Equal(("7.00 DIV", "–"), (offered![0].ValueText, offered[2].ValueText));
        var chart = Assert.Single(_vm.CustomCharts);
        Assert.Equal(("ChO, TRN +1", true), (chart.Title, chart.IsCustom));
        Assert.Equal([1.4, 5.0, 1.2], Rounded(chart.Chart.Values)); // its own currencies, ticked or not

        var reopened = NewViewModel();
        await reopened.LoadLayout();
        Show(reopened);
        Assert.Equal("ChO, TRN +1", Assert.Single(reopened.CustomCharts).Title);
        Assert.False(reopened.Currencies.Single(c => c.Name == GreaterChaos).IsUsed);

        _vm.AskCurrencies = (_, choices) => Task.FromResult<IReadOnlyList<string>?>([Trans]);
        _vm.CustomCharts[0].EditCommand!.Execute(null);
        Assert.Equal("TRN", Assert.Single(_vm.CustomCharts).Title);
        _vm.CustomCharts[0].RemoveCommand!.Execute(null);
        Assert.Empty(_vm.CustomCharts);
        Assert.Empty((await _service.LoadAnalyzeLayout(_feed.Session.SessionId)).Charts);
    }
}
