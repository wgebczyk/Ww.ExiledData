using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>The Analyse tab built from a session's mapping data.</summary>
public sealed class AnalyseViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ww-analyse-").FullName;
    private readonly MappingStore _store;
    private readonly MapFeed _feed = new();
    private readonly AnalyseViewModel _vm;

    public AnalyseViewModelTests()
    {
        _store = MappingStore.Open(Path.Combine(_dir, "data.db"));
        _vm = new AnalyseViewModel(new MappingService(_store, new NoStream()), _feed.Session.SessionId, GatherViewModelTests.TestBook, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class NoStream : ICaptureStream
    {
        public event Action<ProcessedCapture>? CaptureProcessed { add { } remove { } }
        public event Action<SweepChanged>? SweepChanged { add { } remove { } }
    }

    private void Show() => _vm.Update(_feed.Session, recording: false, _feed.Now);

    private void Runs()
    {
        foreach (var (chaos, trans, rituals) in new[] { (10, 40, 6), (50, 0, 7), (10, 20, 2) })
        {
            _feed.Copy(MapItems.Waystone(15, $"of Run {_feed.Session.Runs.Count + 1}"));
            _feed.Stash(("Chaos Orb", chaos), ("Orb of Transmutation", trans), ("Greater Chaos Orb", 1));
            _feed.Session.AddTag(_feed.Session.Runs[^1].Id, $"rituals: {rituals}");
        }
    }

    [Fact]
    public void Without_runs_there_is_nothing_to_analyse()
    {
        Show();

        Assert.False(_vm.HasRuns);
        Assert.Equal(("0", "of 0 runs included"), (_vm.IncludedText, _vm.ScopeText));
        Assert.Empty(_vm.Currencies);
        Assert.Null(_vm.ChartCurrency);
    }

    [Fact]
    public void Scope_currencies_chart_and_runs()
    {
        Runs();
        Show();

        Assert.Equal(("3", "of 3 runs included"), (_vm.IncludedText, _vm.ScopeText));
        Assert.StartsWith("3 runs · ", _vm.SummaryText);
        Assert.Equal(["Chaos Orb", "Orb of Transmutation", "Greater Chaos Orb"], _vm.Currencies.Select(c => c.Name));
        var chaos = _vm.Currencies[0];
        Assert.Equal(("ChO", "70", "23.3", "3/3", 1.0), (chaos.Code, chaos.TotalText, chaos.AverageText, chaos.HitText, chaos.Bar));
        Assert.Equal("2/3", _vm.Currencies[1].HitText);
        Assert.Equal("G", _vm.Currencies[2].Badge);

        Assert.Equal("Chaos Orb", _vm.ChartCurrency);
        Assert.Equal([10.0, 50.0, 10.0], _vm.ChartValues);
        Assert.Equal(["1", "2", "3"], _vm.ChartLabels);
        Assert.Equal("dashed line = average (23.3)", _vm.ChartCaption);

        Assert.Equal(["ChO", "TRN", "ChO/G"], _vm.ValueHeaders);
        Assert.Equal(["3", "2", "1"], _vm.Runs.Select(r => r.NumberText)); // newest first
        Assert.Equal(["10", "20", "1"], _vm.Runs[0].Values);
        Assert.Equal("rituals: 2", _vm.Runs[0].TagsText);
        Assert.Equal("Include run 3", _vm.Runs[0].IncludeLabel);
    }

    [Fact]
    public void A_filter_narrows_everything_and_shows_as_a_chip()
    {
        Runs();
        Show();

        _vm.AddFilter(RunFilter.Parse("rituals >= 5", out _)!);

        Assert.Equal("2", _vm.IncludedText);
        Assert.Equal(["Tag: rituals ≥ 5"], _vm.Filters.Select(f => f.Text));
        Assert.Equal("60", _vm.Currencies.Single(c => c.Name == "Chaos Orb").TotalText);
        Assert.Equal([10.0, 50.0], _vm.ChartValues);
        var filtered = _vm.Runs.Single(r => r.NumberText == "3");
        Assert.Equal((true, 0.5), (filtered.FilteredOut, filtered.RowOpacity));
        Assert.EndsWith("filtered out", filtered.TagsText);

        _vm.Filters[0].RemoveCommand.Execute(null);
        Assert.Equal("3", _vm.IncludedText);
        Assert.Empty(_vm.Filters);
    }

    [Fact]
    public void Sorting_by_average_and_the_grid_show_averages()
    {
        Runs();
        Show();

        _vm.SortByAverage = true;

        var cell = _vm.LootRows.SelectMany(r => r.Cells).Single(c => c.Name == "Chaos Orb");
        Assert.Equal(("23.3", 70), (cell.CountText, cell.Count));
        _vm.SortByTotal = true;
        Assert.Equal("70", cell.CountText);
    }

    [Fact]
    public void Compare_shows_a_and_b_with_direction_marks()
    {
        Runs();
        Show();

        _vm.CompareBy(RunFilter.Parse("rituals >= 5", out _)!);

        Assert.True(_vm.HasCompare);
        Assert.Equal("A · Tag: rituals ≥ 5 · 2 runs", _vm.CompareAText);
        Assert.Equal("B · the rest · 1 run", _vm.CompareBText);
        Assert.StartsWith("Small samples", _vm.CompareNote);
        var chaos = _vm.CompareRows.Single(r => r.Name == "Chaos Orb");
        Assert.Equal(("30.0", "10.0", "▲ 200%", true), (chaos.AText, chaos.BText, chaos.ChangeText, chaos.IsUp));
        var trans = _vm.CompareRows.Single(r => r.Name == "Orb of Transmutation");
        Assert.Equal(("= 0%", false, false), (trans.ChangeText, trans.IsUp, trans.IsDown)); // 20 vs 20
        _vm.CompareBy(RunFilter.Parse("rituals < 5", out _)!); // the other way round
        var down = _vm.CompareRows.Single(r => r.Name == "Chaos Orb");
        Assert.Equal(("▼ 67%", true), (down.ChangeText, down.IsDown));

        _vm.ClearCompareCommand.Execute(null);
        Assert.False(_vm.HasCompare);
        Assert.Empty(_vm.CompareRows);
    }
}
