using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Analyze scope, filters, statistics and compare (MAPPING_PLAN.md 4).</summary>
public sealed class AnalysisTests
{
    private const string Chaos = "Chaos Orb";
    private const string Divine = "Divine Orb";

    private readonly MapFeed _feed = new();
    private MapSession Session => _feed.Session;

    /// <summary>A run with one checkpoint (from zero) and optional tags.</summary>
    private MapRun Run(int chaos, int divine = 0, params string[] tags)
    {
        _feed.Copy(MapItems.Waystone(15, $"of Run {Session.Runs.Count + 1}"));
        var stacks = new List<(string, int)> { (Chaos, chaos) };
        if (divine > 0)
            stacks.Add((Divine, divine));
        _feed.Stash([.. stacks]);
        var run = Session.Runs[^1];
        foreach (var tag in tags)
            Session.AddTag(run.Id, tag);
        return run;
    }

    private static RunFilter Filter(string text) => RunFilter.Parse(text, out var error) ?? throw new InvalidOperationException(error);

    // --- parsing --------------------------------------------------------------------------------

    [Theory]
    [InlineData("died", "Tag: died")]
    [InlineData("!died", "Tag: no died")]
    [InlineData("not died", "Tag: no died")]
    [InlineData("rituals >= 5", "Tag: rituals ≥ 5")]
    [InlineData("rituals≥5", "Tag: rituals ≥ 5")]
    [InlineData("rituals < 5", "Tag: rituals < 5")]
    [InlineData("rituals: 6", "Tag: rituals = 6")]
    [InlineData("boss = Arbiter", "Tag: boss = Arbiter")]
    [InlineData("boss != Arbiter", "Tag: boss ≠ Arbiter")]
    [InlineData("runs 5-23", "Runs 5–23")]
    [InlineData("Runs 5–", "Runs 5–")]
    [InlineData("runs -10", "Runs –10")]
    [InlineData("run 7", "Run 7")]
    public void Filters_parse_from_short_text(string text, string chip) => Assert.Equal(chip, Filter(text).Text);

    [Theory]
    [InlineData("")]
    [InlineData("rituals >= many")]
    [InlineData("runs 9-3")]
    [InlineData(">= 5")]
    public void Invalid_filter_text_says_why(string text)
    {
        Assert.Null(RunFilter.Parse(text, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // --- matching -------------------------------------------------------------------------------

    [Fact]
    public void Tag_filters_compare_numbers_and_text()
    {
        var six = Run(10, tags: "rituals: 6");
        var four = Run(10, tags: ["rituals: 4", "died"]);
        var none = Run(10, tags: "boss: Arbiter");

        bool M(string f, MapRun r) => Filter(f).Matches(Session, r);

        Assert.Equal([true, false, false], new[] { six, four, none }.Select(r => M("rituals >= 5", r)));
        Assert.Equal([false, true, false], new[] { six, four, none }.Select(r => M("rituals < 5", r)));
        Assert.Equal([true, false, false], new[] { six, four, none }.Select(r => M("rituals: 6.0", r)));
        Assert.Equal([true, false, true], new[] { six, four, none }.Select(r => M("!died", r)));
        Assert.Equal([false, false, true], new[] { six, four, none }.Select(r => M("BOSS = arbiter", r)));
        Assert.Equal([false, true, true], new[] { six, four, none }.Select(r => M("runs 2-", r)));
        Assert.Equal([false, true, true], new[] { six, four, none }.Select(r => Filter("rituals >= 5").Negate().Matches(Session, r)));
    }

    // --- scope and statistics -------------------------------------------------------------------

    [Fact]
    public void Statistics_are_over_the_included_runs()
    {
        Run(10, 1);
        Run(20);
        Run(30, 2);
        Run(100); // an outlier

        var analysis = Analysis.Of(Session, []);
        var chaos = analysis.Currencies.Single(c => c.Name == Chaos);
        Assert.Equal((160, 40.0, 25.0, 4, 4), (chaos.Total, chaos.Average, chaos.Median, chaos.Hits, chaos.Runs));
        var divine = analysis.Currencies.Single(c => c.Name == Divine);
        Assert.Equal((3, 0.75, 0.5, 2), (divine.Total, divine.Average, divine.Median, divine.Hits)); // runs without it count as zero
        Assert.Equal(0.5, divine.HitRate);
        Assert.Equal([Chaos, Divine], analysis.Currencies.Select(c => c.Name)); // by total
    }

    [Fact]
    public void Filters_and_manual_exclusion_narrow_the_scope()
    {
        Run(10, tags: "rituals: 6");
        var outlier = Run(100, tags: "rituals: 7");
        Run(20, tags: "rituals: 3");
        Session.SetRunExcluded(outlier.Id, true);

        var analysis = Analysis.Of(Session, [Filter("rituals >= 5")]);

        Assert.Single(analysis.Included);
        Assert.Equal([(false, false), (true, false), (false, true)], analysis.Runs.Select(r => (r.Excluded, r.FilteredOut)));
        Assert.Equal(10, analysis.Currencies.Single().Total);

        Session.Undo();
        Assert.Equal(2, Analysis.Of(Session, [Filter("rituals >= 5")]).Included.Count);
    }

    [Fact]
    public void Per_run_series_has_the_included_runs_and_their_average()
    {
        Run(10);
        var skipped = Run(50);
        Run(20, 1);
        Session.SetRunExcluded(skipped.Id, true);

        var (values, average) = Analysis.Of(Session, []).PerRun(Divine);

        Assert.Equal([(1, 0), (3, 1)], values);
        Assert.Equal(0.5, average);
    }

    [Fact]
    public void Sorting_by_average_and_an_empty_scope()
    {
        Run(5, 9);
        Assert.Equal([Divine, Chaos], Analysis.Of(Session, []).SortedBy(average: true).Select(c => c.Name));

        var none = Analysis.Of(Session, [Filter("died")]);
        Assert.Empty(none.Included);
        Assert.Empty(none.Currencies);
        Assert.Equal(0, none.PerRun(Chaos).Average);
    }

    [Fact]
    public void Compare_splits_the_scope_by_a_filter()
    {
        Run(20, 1, "rituals: 6");
        Run(30, 1, "rituals: 8");
        Run(20, tags: "rituals: 2");
        Run(10); // no tag: in B (the rest)

        var comparison = Analysis.Of(Session, []).Compare(Filter("rituals >= 5"));

        Assert.Equal((2, 2), (comparison.RunsA, comparison.RunsB));
        Assert.True(comparison.IsSmallSample);
        var chaos = comparison.Currencies.Single(c => c.Name == Chaos);
        Assert.Equal((25.0, 15.0), (chaos.AverageA, chaos.AverageB));
        Assert.Equal(10.0 / 15, chaos.Change!.Value, 6);
        var divine = comparison.Currencies.Single(c => c.Name == Divine);
        Assert.Equal((1.0, 0.0, (double?)null), (divine.AverageA, divine.AverageB, divine.Change)); // only in A
    }

    // --- storage ----------------------------------------------------------------------------------

    // --- Analyze: A vs B, values -----------------------------------------------------------------

    [Fact]
    public void Without_filters_a_is_the_first_half_with_the_extra_run()
    {
        Run(1);
        Run(2);
        var excluded = Run(3);
        Run(4);
        Run(5);
        Run(6);
        Session.SetRunExcluded(excluded.Id, true);

        var split = Analysis.Of(Session, []).Split([]);

        Assert.True(split.ByHalves);
        Assert.Equal([1, 2, 4], split.A.Select(r => r.Run.Number));
        Assert.Equal([5, 6], split.B.Select(r => r.Run.Number));
        Assert.True(split.IsSmallSample);
        Assert.False(split.HasEmptySide);
    }

    [Fact]
    public void Filters_define_a_together_and_b_is_the_rest()
    {
        Run(1, tags: ["rituals: 6"]);
        Run(2, tags: ["rituals: 7", "died"]);
        Run(3, tags: ["rituals: 2"]);

        var split = Analysis.Of(Session, []).Split([Filter("rituals >= 5"), Filter("!died")]);

        Assert.False(split.ByHalves);
        Assert.Equal([1], split.A.Select(r => r.Run.Number));
        Assert.Equal([2, 3], split.B.Select(r => r.Run.Number));
        Assert.True(split.IsA(split.A[0]));
        Assert.True(Analysis.Of(Session, []).Split([Filter("rituals >= 99")]).HasEmptySide);
    }

    [Fact]
    public void A_run_value_sums_the_used_priced_currencies()
    {
        Run(chaos: 10, divine: 2);
        var run = Analysis.Of(Session, []).Included[0];
        double? Price(string name, int count) => name == Chaos ? count * 0.1 : null;

        Assert.Equal(1.0, run.ValueOf(_ => true, Price), 6); // the Divine Orb has no price here
        Assert.Equal(0.0, run.ValueOf(n => n != Chaos, Price));
        Assert.Equal(12, run.CountOf(_ => true));
        Assert.Equal(2, run.CountOf(n => n == Divine));
        Assert.Equal([1.0, 3.0, 6.0], Analysis.Cumulative([1, 2, 3]));
    }

    [Fact]
    public void Exclusion_is_stored_and_a_mapping_schema_before_scan_kinds_is_reset()
    {
        var dir = Directory.CreateTempSubdirectory("ww-analysis-").FullName;
        var path = Path.Combine(dir, "data.db");
        try
        {
            using (var core = Storage.SqliteStore.Open(path))
            {
                var session = Core.Sessions.SessionLifecycle.Start(null, _feed.Now, "s", Core.Sessions.SessionKind.Mapping);
                core.SaveSession(session);
                var feed = new MapFeed { Session = new MapSession(session.Id) };
                feed.Copy(MapItems.Waystone(15));
                feed.Copy(MapItems.Waystone(16));
                feed.Session.SetRunExcluded(feed.Session.Runs[0].Id, true);
                using (var store = MappingStore.Open(path))
                {
                    store.Save(feed.Session);
                    Assert.Equal([true, false], store.Load(session.Id).Runs.Select(r => r.Excluded));
                }

                // Scan roles and legs (schema 3) were replaced by scan kinds: older mapping data is dropped, not converted.
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "UPDATE module_schema SET version = 3 WHERE module = 'mapping';";
                    cmd.ExecuteNonQuery();
                }
                using (var reset = MappingStore.Open(path))
                    Assert.Empty(reset.Load(session.Id).Runs);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
