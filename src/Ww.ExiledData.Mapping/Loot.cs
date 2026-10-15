namespace Ww.ExiledData.Mapping;

/// <summary>
/// A run's loot: the sum of its counting stash scans plus the adjustments the user confirmed, sorted by count,
/// highest first. A currency an adjustment brings to zero or below is left out of <see cref="Total"/>.
/// </summary>
public sealed record RunLoot(MapRun Run, IReadOnlyList<Scan> StashScans, IReadOnlyList<LootAdjustment> Adjustments,
    IReadOnlyList<CurrencyStack> Total)
{
    public int CountOf(string key) => Total.FirstOrDefault(c => c.Key == key)?.Count ?? 0;
}

/// <summary>One currency of the Final-vs-Initial check.</summary>
/// <param name="Applied">Adjustments already applied from this check for the currency.</param>
public sealed record CheckDifference(string Key, string Name, int Initial, int Final, int Applied)
{
    /// <summary>More at the end than at the start: gathered but never stashed (positive), or spent (negative).</summary>
    public int Difference => Final - Initial;
    /// <summary>The adjustment still proposed: the difference not yet applied.</summary>
    public int Proposed => Difference - Applied;
}

/// <summary>
/// The consistency check of a run (MAPPING_PLAN.md 3.3): its last Final scan compared with its Initial swipe (with no
/// Initial swipe, with an empty inventory). Warns while a difference is neither applied as an adjustment nor dismissed.
/// </summary>
public sealed record FinalCheck(Scan Final, Scan? Initial, IReadOnlyList<CheckDifference> Differences)
{
    /// <summary>The differences not yet applied, each with its proposed adjustment.</summary>
    public IReadOnlyList<CheckDifference> Open => Differences.Where(d => d.Proposed != 0).ToList();
    public bool HasWarning => !Final.CheckDismissed && Open.Count > 0;
}

/// <summary>What a scan shows (MAPPING_PLAN.md 3.3): its name, totals and warnings; for a Final scan, its check.</summary>
/// <param name="Name">"Initial", "Swipe", "Stash scan" or "Final".</param>
public sealed record ScanSummary(Scan Scan, string Name, IReadOnlyList<CurrencyStack> Totals, FinalCheck? Check, IReadOnlyList<string> Warnings);

/// <summary>
/// Derives loot from scans (MAPPING_PLAN.md 3.3). Run loot = the sum of the run's stash scans plus confirmed
/// adjustments, per currency. Swipes and Final scans never count. Loot is never stored, so a corrected scan kind
/// recomputes everything.
/// </summary>
public static class Loot
{
    public static RunLoot Of(MapSession session, MapRun run)
    {
        var stashes = session.ScansOf(run).Where(s => s.Counts && s.Kind == ScanKind.Stash).ToList();
        var stacks = stashes.SelectMany(s => s.Totals())
            .Concat(run.Adjustments.Select(a => new CurrencyStack(a.CurrencyKey, a.CurrencyName, a.Amount)));
        return new RunLoot(run, stashes, run.Adjustments.ToList(), Sum(stacks).Where(c => c.Count > 0).ToList());
    }

    /// <summary>Every run's loot, in run order.</summary>
    public static IReadOnlyList<RunLoot> OfRuns(MapSession session) => session.Runs.Select(r => Of(session, r)).ToList();

    /// <summary>The session's loot: the sum over its runs.</summary>
    public static IReadOnlyList<CurrencyStack> OfSession(MapSession session) =>
        Sum(OfRuns(session).SelectMany(r => r.Total));

    /// <summary>The run's check, or null when it has no counting Final scan.</summary>
    public static FinalCheck? CheckOf(MapSession session, MapRun run) =>
        session.FinalOf(run) is { } final ? Check(session, run, final) : null;

    public static ScanSummary Summarize(MapSession session, Scan scan)
    {
        var totals = scan.Totals();
        var warnings = new List<string>();
        if (scan.Status == ScanStatus.Canceled)
            warnings.Add("Canceled: this scan is not used.");
        if (scan.Status == ScanStatus.Interrupted)
            warnings.Add("Interrupted by an app exit: cells after the last capture are missing.");
        if (scan.Kind == ScanKind.Stash && totals.Count == 0)
            warnings.Add("No currency found. A forgotten cell?");

        FinalCheck? check = null;
        if (scan.Kind == ScanKind.Final && session.FindRun(scan.RunId) is { } run)
        {
            check = Check(session, run, scan);
            if (check.HasWarning)
                warnings.Add($"Differs from {(check.Initial is null ? "an empty inventory (no Initial swipe)" : "the Initial swipe")}: "
                    + string.Join(", ", check.Open.Select(d => $"{d.Name} {d.Proposed:+0;-0}")));
        }
        return new ScanSummary(scan, session.NameOf(scan), totals, check, warnings);
    }

    private static FinalCheck Check(MapSession session, MapRun run, Scan final)
    {
        var initial = session.InitialOf(run);
        var before = initial?.Totals().ToDictionary(c => c.Key) ?? [];
        var after = final.Totals().ToDictionary(c => c.Key);
        var applied = run.Adjustments.Where(a => a.SourceScanId == final.Id)
            .GroupBy(a => a.CurrencyKey).ToDictionary(g => g.Key, g => (g.First().CurrencyName, Amount: g.Sum(a => a.Amount)));
        var differences = before.Keys.Union(after.Keys).Union(applied.Keys)
            .Select(k => new CheckDifference(k,
                after.GetValueOrDefault(k)?.Name ?? before.GetValueOrDefault(k)?.Name ?? applied[k].CurrencyName,
                before.GetValueOrDefault(k)?.Count ?? 0,
                after.GetValueOrDefault(k)?.Count ?? 0,
                applied.TryGetValue(k, out var a) ? a.Amount : 0))
            .Where(d => d.Difference != 0 || d.Applied != 0)
            .OrderByDescending(d => Math.Abs(d.Difference)).ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        return new FinalCheck(final, initial, differences);
    }

    private static IReadOnlyList<CurrencyStack> Sum(IEnumerable<CurrencyStack> stacks) =>
        stacks.GroupBy(s => s.Key)
            .Select(g => new CurrencyStack(g.Key, g.First().Name, g.Sum(s => s.Count)))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
}
