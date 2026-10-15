using System.Globalization;
using System.Text.RegularExpressions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Mapping;

/// <summary>What a setup item is (MAPPING_PLAN.md 3.2). Stored as an integer: never renumber.</summary>
public enum SetupItemKind
{
    Waystone = 0,
    Tablet = 1,
    /// <summary>
    /// An unknown item copied in the setup phase. The tablet item class text is not known yet, so the user confirms
    /// (<see cref="Tablet"/>) or rejects (<see cref="NotSetup"/>) it.
    /// </summary>
    PossibleTablet = 2,
    /// <summary>Rejected possible tablet: kept as a capture of the run, not part of the setup.</summary>
    NotSetup = 3,
}

/// <summary>The waystone or a tablet of a run's setup. Raw text only; mods are parsed later (3.6).</summary>
public sealed class SetupItem
{
    public required Guid Id { get; init; }
    public required SetupItemKind Kind { get; set; }
    /// <summary>The entry in the raw capture log, or null when the copy changed nothing there (already recorded).</summary>
    public Guid? CaptureId { get; init; }
    public required string RawText { get; init; }
    /// <summary>When it was copied (for a recalled item: when the earlier copy was made).</summary>
    public required DateTimeOffset CopiedAt { get; init; }
    /// <summary>
    /// Recalled from an earlier copy (MAPPING_PLAN.md 3.2) instead of copied in this run's setup: the number of the run
    /// it was copied in. Null for an item copied in the setup itself.
    /// </summary>
    public int? RecalledFromRun { get; init; }

    public string Label => ItemLabel.From(RawText);
    /// <summary>Counts toward the 0 to 4 tablets (confirmed or not).</summary>
    public bool IsTabletSlot => Kind is SetupItemKind.Tablet or SetupItemKind.PossibleTablet;
    public bool IsRecalled => RecalledFromRun is not null;
}

/// <summary>
/// An earlier copy of a waystone or tablet in the session, offered by the recall lists (MAPPING_PLAN.md 3.2). It
/// references that copy; assigning it does not create a new capture.
/// </summary>
/// <param name="RunNumber">The run it was copied in; 0 when before the first run.</param>
public sealed record RecallItem(SetupItemKind Kind, string RawText, Guid? CaptureId, int RunNumber, DateTimeOffset CopiedAt)
{
    public string Label => ItemLabel.From(RawText);

    /// <summary>A tablet's "Uses Remaining" when it shows one.</summary>
    public int? UsesLeft =>
        Regex.Match(RawText, @"^Uses Remaining: (\d+)", RegexOptions.Multiline) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
}

/// <summary>One map run of a mapping session (MAPPING_PLAN.md 3.2).</summary>
public sealed class MapRun
{
    public required Guid Id { get; init; }
    public required int Number { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    /// <summary>Set by the next run's start or End run; null while the run is open.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Waystone first, then tablets, in copy order. Empty for a run started by hand before any copy.</summary>
    public List<SetupItem> Setup { get; set; } = [];
    /// <summary>Every logged capture made while this was the current run (setup and scans included), in order.</summary>
    public List<Guid> CaptureIds { get; init; } = [];
    /// <summary>Loot corrections the user confirmed (from the Final-vs-Initial check), in order.</summary>
    public List<LootAdjustment> Adjustments { get; set; } = [];

    /// <summary>Excluded from the Analyse statistics by hand (an outlier or a failed run).</summary>
    public bool Excluded { get; set; }

    public bool IsOpen => EndedAt is null;
    public SetupItem? Waystone => Setup.FirstOrDefault(i => i.Kind == SetupItemKind.Waystone);
    public IEnumerable<SetupItem> Tablets => Setup.Where(i => i.IsTabletSlot);

    /// <summary>The waystone's tier, read from its name ("Waystone (Tier 15)"); null when it has none.</summary>
    public int? WaystoneTier =>
        Waystone is { } w && Regex.Match(w.Label, @"\(Tier (\d+)\)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Short setup text for tables: "T15 + 3 tablets".</summary>
    public string SetupText
    {
        get
        {
            var waystone = WaystoneTier is { } tier ? $"T{tier}" : Waystone?.Label ?? "?";
            var tablets = Tablets.Count();
            return tablets switch
            {
                0 => waystone,
                1 => $"{waystone} + 1 tablet",
                _ => $"{waystone} + {tablets} tablets",
            };
        }
    }
}

/// <summary>
/// A loot correction the user confirmed (MAPPING_PLAN.md 3.3): added to the run's loot like a stash scan. Undoing it
/// removes it.
/// </summary>
/// <param name="Amount">Added to the currency's loot; negative takes some away.</param>
/// <param name="SourceScanId">The Final scan whose check proposed it.</param>
public sealed record LootAdjustment(Guid Id, string CurrencyKey, string CurrencyName, int Amount, Guid? SourceScanId, DateTimeOffset AppliedAt);

/// <summary>
/// What a scan is for (MAPPING_PLAN.md 3.3), chosen when it starts and correctable later. Stored as an integer:
/// never renumber.
/// </summary>
public enum ScanKind
{
    /// <summary>A snapshot only: never counts toward loot. The first swipe of a run is its "Initial".</summary>
    Swipe = 0,
    /// <summary>The inventory right before its contents are dropped into the stash: counts toward loot.</summary>
    Stash = 1,
    /// <summary>An optional last scan, compared with the Initial swipe as a consistency check. Does not count.</summary>
    Final = 2,
}

/// <summary>Stored as an integer: never renumber.</summary>
public enum ScanStatus
{
    Active = 0,
    Finished = 1,
    /// <summary>The sweep was canceled (its captures withdrawn): never counts.</summary>
    Canceled = 2,
    /// <summary>The app exited during the scan. Counts with what it got; the summary warns.</summary>
    Interrupted = 3,
    /// <summary>Removed by the user after it ended (undoable): never counts; its captures stay with the run.</summary>
    Removed = 4,
}

/// <summary>A currency stack: identity key (class + rarity + name + max stack), display name and count.</summary>
public sealed record CurrencyStack(string Key, string Name, int Count);

/// <summary>
/// What one inventory cell held at the scan. <see cref="Stack"/> is null for an item that is not stackable; such an
/// item keeps its <see cref="RawText"/> (a waystone or tablet waiting in the inventory can be recalled into a run's
/// setup later).
/// </summary>
public sealed record ScanCell(GridCell Cell, Guid? CaptureId, CurrencyStack? Stack, string? RawText = null);

/// <summary>A snapshot of the stackables in the inventory, taken as one sweep (MAPPING_PLAN.md 3.3).</summary>
public sealed class Scan
{
    public required Guid Id { get; init; }
    /// <summary>The run it belongs to (a scan started with no run open starts one).</summary>
    public required Guid RunId { get; init; }
    public required Guid SweepId { get; init; }
    public required string Label { get; init; }
    public required ScanKind Kind { get; set; }
    public required ScanStatus Status { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>A Final scan whose differences from the Initial swipe the user dismissed: no warning any more.</summary>
    public bool CheckDismissed { get; set; }
    /// <summary>Re-copying a cell replaces what it held: a scan never adds the same cell twice.</summary>
    public Dictionary<GridCell, ScanCell> Cells { get; init; } = [];

    /// <summary>Finished, or interrupted with what it got: its snapshot is usable.</summary>
    public bool Counts => Status is ScanStatus.Finished or ScanStatus.Interrupted;
    /// <summary>Canceled or removed: no longer part of the run (not listed, not the Initial, not recalled from).</summary>
    public bool IsDiscarded => Status is ScanStatus.Canceled or ScanStatus.Removed;

    /// <summary>Per currency, the sum over all its stacks (two stacks of 40 and 25 = 65).</summary>
    public IReadOnlyList<CurrencyStack> Totals() =>
        Cells.Values.Where(c => c.Stack is not null).Select(c => c.Stack!)
            .GroupBy(s => s.Key)
            .Select(g => new CurrencyStack(g.Key, g.First().Name, g.Sum(s => s.Count)))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
}
