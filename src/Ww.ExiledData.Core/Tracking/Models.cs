using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Core.Tracking;

public enum CaptureKind
{
    Regular,
    Stackable,
}

/// <summary>One entry of the append-only raw capture log (source of truth).</summary>
public sealed class Capture
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required string RawText { get; init; }
    public required ScreenPoint Position { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required CaptureKind Kind { get; init; }
    public required string IdentityKey { get; init; }
    /// <summary>Set when taken inside a sweep's grid; the cell is then the position used for identity.</summary>
    public Guid? SweepId { get; init; }
    public GridCell? Cell { get; init; }
    public DateTimeOffset? WithdrawnAt { get; set; }

    public bool IsWithdrawn => WithdrawnAt is not null;
}

public sealed class RegularItem
{
    public required Guid CaptureId { get; init; }
    public required string IdentityKey { get; init; }
    public required string Label { get; init; }
    public required string RawText { get; init; }
    public required ScreenPoint Position { get; init; }
    public Guid? SweepId { get; init; }
    public GridCell? Cell { get; init; }
    public required DateTimeOffset LastSeenAt { get; set; }
    /// <summary>Same text already recorded elsewhere: maybe a real sibling, maybe the same item moved.</summary>
    public bool PossibleDuplicate { get; init; }
    public bool NeedsReview { get; init; }
    /// <summary>
    /// Captures of the same text in adjacent sweep cells, taken as further cells of this (multi-cell) item.
    /// A flag, not a certainty: two identical items side by side look the same, hence "split".
    /// </summary>
    public List<Guid> CoveredCaptureIds { get; init; } = [];
}

/// <summary>
/// One stack of a stackable where it was copied: a sweep cell (a stack takes exactly one cell), or a screen position
/// outside sweeps. <see cref="CaptureId"/> is the capture its count comes from.
/// </summary>
public sealed record StackPlace(Guid CaptureId, ScreenPoint Position, Guid? SweepId, GridCell? Cell, int Count);

public sealed class StackableItem
{
    public required string Key { get; init; }
    public required string ItemClass { get; init; }
    public required string Rarity { get; init; }
    public required string Name { get; init; }
    public required int MaxStack { get; init; }
    /// <summary>
    /// Every stack copied in the session. Re-copying the same place replaces its stack; a stack elsewhere is another
    /// stack, so stashing several times adds up.
    /// </summary>
    public List<StackPlace> Stacks { get; set; } = [];
    /// <summary>Sum over all stacks.</summary>
    public int Count => Stacks.Sum(s => s.Count);
    public required string LatestRawText { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required int ParserVersion { get; set; }
}

/// <summary>
/// Values of a stackable that an update can change; used for undo/redo. <see cref="Count"/> is the total;
/// <see cref="Stacks"/> is null only in entries stored before stacks were tracked per place.
/// </summary>
public sealed record StackableSnapshot(int Count, string LatestRawText, DateTimeOffset UpdatedAt, int ParserVersion,
    IReadOnlyList<StackPlace>? Stacks = null)
{
    // By value, stacks included (a stored entry reads back with its own list).
    public bool Equals(StackableSnapshot? other) =>
        other is not null && (Count, LatestRawText, UpdatedAt, ParserVersion) == (other.Count, other.LatestRawText, other.UpdatedAt, other.ParserVersion)
        && (Stacks ?? []).SequenceEqual(other.Stacks ?? []);

    public override int GetHashCode() => HashCode.Combine(Count, LatestRawText, UpdatedAt, ParserVersion);
}

public enum ChangeType
{
    ItemAdded,
    StackableAdded,
    StackableCountUpdated,
    ItemRemoved,
    StackableRemoved,
    /// <summary>A capture merged into an adjacent item with the same text (<see cref="ChangeEntry.RelatedCaptureId"/>).</summary>
    CellCovered,
    /// <summary>A covered capture turned into an item of its own; related = the item it was part of.</summary>
    ItemSplit,
    /// <summary>A capture that changed nothing but the sweep cell it was taken in (e.g. a stack with an unchanged count).</summary>
    CellMarked,
    /// <summary>Everything done during a sweep, withdrawn as one entry (<see cref="ChangeEntry.Children"/>, most recent first).</summary>
    SweepCanceled,
}

/// <summary>A reversible state change. Kept general enough for a future history UI.</summary>
public sealed record ChangeEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    ChangeType Type,
    Guid CaptureId,
    string IdentityKey,
    StackableSnapshot? Before = null,
    StackableSnapshot? After = null,
    Guid? RelatedCaptureId = null,
    // The sweep that was running when the change was made.
    Guid? SweepId = null,
    IReadOnlyList<ChangeEntry>? Children = null);

public enum CaptureOutcome
{
    Added,
    SiblingAdded,
    StackableAdded,
    StackableUpdated,
    AlreadyRecorded,
    NotAnItem,
    Undone,
    Redone,
    Removed,
    /// <summary>Same text in a cell adjacent to a captured one: recorded as another cell of that item.</summary>
    Covered,
    Split,
}

public sealed record CaptureResult(
    CaptureOutcome Outcome,
    ChangeEntry? Change = null,
    int? StackCount = null,
    IReadOnlyList<string>? Warnings = null,
    // The sweep cell the capture landed in.
    GridCell? Cell = null,
    // A sweep is running but the capture was outside its grid (recorded as an ordinary capture).
    bool OutsideGrid = false);
