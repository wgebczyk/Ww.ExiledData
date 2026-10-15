namespace Ww.ExiledData.Mapping;

/// <summary>A run in the Analyze scope: its loot and whether it counts.</summary>
/// <param name="Excluded">Excluded by hand (outlier, failed run).</param>
/// <param name="FilteredOut">Does not match the scope's filters.</param>
public sealed record ScopedRun(MapRun Run, RunLoot Loot, bool Excluded, bool FilteredOut)
{
    public bool Included => !Excluded && !FilteredOut;

    /// <summary>The run's count of a currency, by name (variants with one name count as one currency).</summary>
    public int CountOf(string name) => Loot.Total.Where(c => c.Name == name).Sum(c => c.Count);

    /// <summary>The run's count of the currencies <paramref name="use"/> accepts.</summary>
    public int CountOf(Func<string, bool> use) => Loot.Total.Where(c => use(c.Name)).Sum(c => c.Count);

    /// <summary>
    /// The value of the run's currencies <paramref name="use"/> accepts; <paramref name="price"/> gives the value of a
    /// count of one currency (null when it has no price: it adds nothing).
    /// </summary>
    public double ValueOf(Func<string, bool> use, Func<string, int, double?> price) =>
        Loot.Total.Where(c => use(c.Name)).GroupBy(c => c.Name).Sum(g => price(g.Key, g.Sum(c => c.Count)) ?? 0);
}

/// <summary>Compare A vs B in the Analyze tab: the included runs split in two groups, each in run order.</summary>
/// <param name="ByHalves">No filter defines A: A is the first half (the extra run when odd), B the second.</param>
public sealed record RunSplit(IReadOnlyList<ScopedRun> A, IReadOnlyList<ScopedRun> B, bool ByHalves)
{
    public bool IsSmallSample => A.Count < Comparison.SmallSample || B.Count < Comparison.SmallSample;
    public bool HasEmptySide => A.Count == 0 || B.Count == 0;
    public bool IsA(ScopedRun run) => A.Contains(run);
}

/// <summary>Statistics of one currency over the included runs (MAPPING_PLAN.md 4).</summary>
/// <param name="Hits">Runs in which it dropped.</param>
/// <param name="Runs">Included runs (the denominator of the average and the hit rate).</param>
public sealed record CurrencyStat(string Name, int Total, double Average, double Median, int Hits, int Runs)
{
    public double HitRate => Runs == 0 ? 0 : (double)Hits / Runs;
}

/// <summary>A currency's average per run in A and in B (Compare A vs B).</summary>
/// <param name="Change">Relative difference of A from B (0.26 = 26 % more in A); null when B's average is zero.</param>
public sealed record CurrencyComparison(string Name, double AverageA, double AverageB, double? Change);

/// <summary>Compare A vs B: A = the scope's runs matching a filter, B = the rest of the scope.</summary>
public sealed record Comparison(RunFilter Filter, int RunsA, int RunsB, IReadOnlyList<CurrencyComparison> Currencies)
{
    /// <summary>Below this many runs on a side, a difference is likely noise.</summary>
    public const int SmallSample = 10;

    public bool IsSmallSample => RunsA < SmallSample || RunsB < SmallSample;
}

/// <summary>
/// The Analyze tab's numbers (MAPPING_PLAN.md 4): the scope ("N of M runs included": filters AND manual exclusion),
/// per-currency statistics over the included runs, a per-run series and Compare A vs B. Derived from the scans
/// every time, like all loot.
/// </summary>
public sealed class Analysis
{
    private Analysis(MapSession session, IReadOnlyList<RunFilter> filters, IReadOnlyList<ScopedRun> runs)
    {
        Session = session;
        Filters = filters;
        Runs = runs;
        Included = runs.Where(r => r.Included).ToList();
        Currencies = Statistics(Included);
    }

    public MapSession Session { get; }
    public IReadOnlyList<RunFilter> Filters { get; }
    /// <summary>Every run, in run order.</summary>
    public IReadOnlyList<ScopedRun> Runs { get; }
    public IReadOnlyList<ScopedRun> Included { get; }
    /// <summary>Every currency that dropped in an included run, by total, highest first.</summary>
    public IReadOnlyList<CurrencyStat> Currencies { get; }

    public static Analysis Of(MapSession session, IReadOnlyList<RunFilter> filters) =>
        new(session, filters, session.Runs
            .Select(r => new ScopedRun(r, Loot.Of(session, r), r.Excluded, !filters.All(f => f.Matches(session, r))))
            .ToList());

    /// <summary>The currencies sorted by total or by average per run (with the same runs, the same order unless ties).</summary>
    public IReadOnlyList<CurrencyStat> SortedBy(bool average) => average
        ? Currencies.OrderByDescending(c => c.Average).ThenBy(c => c.Name, StringComparer.Ordinal).ToList()
        : Currencies;

    /// <summary>The included runs' counts of one currency, in run order, and their average.</summary>
    public (IReadOnlyList<(int Run, int Count)> Values, double Average) PerRun(string name)
    {
        var values = Included.Select(r => (r.Run.Number, r.CountOf(name))).ToList();
        return (values, values.Count == 0 ? 0 : values.Average(v => v.Item2));
    }

    /// <summary>Compare A (included runs matching <paramref name="filter"/>) with B (the other included runs).</summary>
    public Comparison Compare(RunFilter filter)
    {
        var a = Included.Where(r => filter.Matches(Session, r.Run)).ToList();
        var b = Included.Where(r => !filter.Matches(Session, r.Run)).ToList();
        var statsA = Statistics(a).ToDictionary(s => s.Name);
        var statsB = Statistics(b).ToDictionary(s => s.Name);
        var currencies = statsA.Keys.Union(statsB.Keys)
            .Select(name =>
            {
                var avgA = statsA.GetValueOrDefault(name)?.Average ?? 0;
                var avgB = statsB.GetValueOrDefault(name)?.Average ?? 0;
                return new CurrencyComparison(name, avgA, avgB, avgB == 0 ? null : (avgA - avgB) / avgB);
            })
            .OrderByDescending(c => Math.Max(c.AverageA, c.AverageB)).ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        return new Comparison(filter, a.Count, b.Count, currencies);
    }

    /// <summary>
    /// Compare A vs B: A = the included runs matching every filter, B = the other included runs. With no filter, A is
    /// the first half of the included runs (it gets the extra run when the count is odd) and B the second half.
    /// </summary>
    public RunSplit Split(IReadOnlyList<RunFilter> filters)
    {
        if (filters.Count == 0)
        {
            var half = (Included.Count + 1) / 2;
            return new RunSplit([.. Included.Take(half)], [.. Included.Skip(half)], ByHalves: true);
        }
        var a = Included.Where(r => filters.All(f => f.Matches(Session, r.Run))).ToList();
        return new RunSplit(a, [.. Included.Except(a)], ByHalves: false);
    }

    /// <summary>The running total of <paramref name="values"/>.</summary>
    public static IReadOnlyList<double> Cumulative(IEnumerable<double> values)
    {
        var sum = 0.0;
        return values.Select(v => sum += v).ToList();
    }

    /// <summary>Statistics of every currency that dropped in <paramref name="runs"/>, by total, highest first.</summary>
    public static IReadOnlyList<CurrencyStat> Statistics(IReadOnlyList<ScopedRun> runs)
    {
        var names = runs.SelectMany(r => r.Loot.Total).Select(c => c.Name).Distinct();
        return names
            .Select(name =>
            {
                var counts = runs.Select(r => r.CountOf(name)).ToList();
                return new CurrencyStat(name, counts.Sum(), counts.Average(), Median(counts), counts.Count(c => c > 0), counts.Count);
            })
            .OrderByDescending(s => s.Total).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Median over every included run (a run without the currency counts as zero).</summary>
    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
