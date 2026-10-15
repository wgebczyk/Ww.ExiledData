using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Mapping;

public sealed record MappingOptions
{
    /// <summary>
    /// How long after the last setup item an unknown item still counts as a possible tablet (the map device screen
    /// is a short moment; later copies are loot checks).
    /// </summary>
    public TimeSpan SetupWindow { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>What a capture did to a mapping session.</summary>
public enum MappingOutcome
{
    /// <summary>Not a mapping capture's concern beyond being attached to the current run (or nothing at all).</summary>
    Attached,
    RunStarted,
    /// <summary>The current run's waystone copied again before anything else happened: still the same run.</summary>
    SameWaystone,
    SetupItemAdded,
    ScanCellUpdated,
}

/// <summary>
/// The mapping side of one session (MAPPING_PLAN.md 3): runs with their setup, scans and tags and notes, plus the
/// undo/redo history of user edits. Captures and sweeps from the seam drive runs and scans; loot is derived
/// (<see cref="Loot"/>), never stored. Not thread-safe: one owner, one queue.
/// </summary>
public sealed class MapSession(Guid sessionId)
{
    public const string WaystoneItemClass = "Waystones";
    public const int MaxTablets = 4;

    private readonly Stack<MappingEdit> _undo = new();
    private readonly Stack<MappingEdit> _redo = new();

    public Guid SessionId { get; } = sessionId;
    public List<MapRun> Runs { get; init; } = [];
    /// <summary>All scans in start order.</summary>
    public List<Scan> Scans { get; init; } = [];
    /// <summary>Tags per owner: the session id or a run id.</summary>
    public Dictionary<Guid, List<MapTag>> Tags { get; init; } = [];
    /// <summary>Notes per owner: the session id or a run id.</summary>
    public Dictionary<Guid, string> Notes { get; init; } = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The open run, which new captures attach to; null before the first run and after End run.</summary>
    public MapRun? CurrentRun => Runs.Count > 0 && Runs[^1].IsOpen ? Runs[^1] : null;
    public Scan? ActiveScan => Scans.LastOrDefault(s => s.Status == ScanStatus.Active);

    public MapRun? FindRun(Guid id) => Runs.FirstOrDefault(r => r.Id == id);
    public Scan? FindScan(Guid id) => Scans.FirstOrDefault(s => s.Id == id);
    public IEnumerable<Scan> ScansOf(MapRun run) => Scans.Where(s => s.RunId == run.Id);
    public IReadOnlyList<MapTag> TagsOf(Guid ownerId) => Tags.TryGetValue(ownerId, out var tags) ? tags : [];
    public string? NoteOf(Guid ownerId) => Notes.GetValueOrDefault(ownerId);

    // --- recording (from the seam) ----------------------------------------------------------------

    /// <summary>
    /// Applies one capture (MAPPING_PLAN.md 3.2, 3.3). A copy in a sweep cell belongs to that scan only (a waystone
    /// lying in the inventory does not start a run); otherwise a waystone starts a run (or completes a run started by
    /// hand that has no waystone yet), unknown items right after it form the setup, and everything else is attached to
    /// the current run.
    /// </summary>
    public MappingOutcome Apply(ProcessedCapture capture, MappingOptions options)
    {
        if (capture is { SweepId: { } sweepId, Cell: { } cell })
        {
            var scan = Scans.FirstOrDefault(s => s.SweepId == sweepId) ?? StartScan(sweepId, "", capture.CapturedAt, ScanKind.Swipe);
            Attach(capture);
            if (scan.Status != ScanStatus.Active)
                return MappingOutcome.Attached;
            var previous = scan.Cells.GetValueOrDefault(cell);
            var stack = capture.Stack is { } s ? new CurrencyStack(s.Key, s.Name, s.Count) : null;
            scan.Cells[cell] = new ScanCell(cell, capture.CaptureId ?? previous?.CaptureId, stack, stack is null ? capture.RawText : null);
            return MappingOutcome.ScanCellUpdated;
        }

        var itemClass = ItemClassOf(capture.RawText);
        var run = CurrentRun;
        if (itemClass == WaystoneItemClass)
        {
            if (run is not null && IsSameWaystone(run, capture.RawText))
            {
                Attach(capture);
                return MappingOutcome.SameWaystone;
            }
            if (run is not null && run.Waystone is null && !IsGathering(run))
            {
                // A run started by hand or by a scan: the waystone copied before any stash or final scan is its setup.
                run.Setup.Insert(0, SetupFrom(capture, SetupItemKind.Waystone));
                Attach(capture);
                return MappingOutcome.SetupItemAdded;
            }
            StartRun(capture.CapturedAt, capture);
            return MappingOutcome.RunStarted;
        }

        if (run is not null && capture.Stack is null && SetupKindFor(run, itemClass, capture.CapturedAt, options) is { } kind)
        {
            run.Setup.Add(SetupFrom(capture, kind));
            Attach(capture);
            return MappingOutcome.SetupItemAdded;
        }

        Attach(capture);
        return MappingOutcome.Attached;
    }

    /// <summary>
    /// A sweep started or ended. A started sweep is a scan of the current run, of <paramref name="kind"/> (a swipe
    /// when null); with no run open it starts a new run first.
    /// </summary>
    public Scan? Apply(SweepChanged sweep, ScanKind? kind = null)
    {
        var scan = Scans.FirstOrDefault(s => s.SweepId == sweep.SweepId);
        switch (sweep.Status)
        {
            case SweepStatus.Active:
                return scan ?? StartScan(sweep.SweepId, sweep.Label, sweep.At, kind ?? ScanKind.Swipe);
            case SweepStatus.Finished or SweepStatus.Canceled or SweepStatus.Interrupted when scan is { Status: ScanStatus.Active }:
                scan.Status = sweep.Status switch
                {
                    SweepStatus.Finished => ScanStatus.Finished,
                    SweepStatus.Canceled => ScanStatus.Canceled,
                    _ => ScanStatus.Interrupted,
                };
                scan.EndedAt = sweep.At;
                return scan;
            default:
                return scan;
        }
    }

    /// <summary>
    /// Starts a run by hand (MAPPING_PLAN.md 3.2: "Start new run"), with no waystone or tablet copied; it ends the open
    /// run. Its setup comes from the next copies or from the recall lists.
    /// </summary>
    public MapRun StartNewRun(DateTimeOffset at) => StartRun(at, capture: null);

    /// <summary>Launch-time recovery: a scan left active by an app exit is closed as interrupted at its start.</summary>
    public bool CloseInterruptedScans()
    {
        var any = false;
        foreach (var scan in Scans.Where(s => s.Status == ScanStatus.Active))
        {
            scan.Status = ScanStatus.Interrupted;
            scan.EndedAt ??= scan.StartedAt;
            any = true;
        }
        return any;
    }

    // --- scans --------------------------------------------------------------------------------------

    /// <summary>The run's Initial: its first swipe (canceled ones aside).</summary>
    public Scan? InitialOf(MapRun run) =>
        ScansOf(run).FirstOrDefault(s => s.Kind == ScanKind.Swipe && !s.IsDiscarded);

    /// <summary>The run's last usable Final scan, the one its check uses.</summary>
    public Scan? FinalOf(MapRun run) =>
        ScansOf(run).LastOrDefault(s => s.Kind == ScanKind.Final && s.Counts);

    /// <summary>"Initial" for the run's first swipe; else the kind's name.</summary>
    public string NameOf(Scan scan) => scan.Kind switch
    {
        ScanKind.Swipe when FindRun(scan.RunId) is { } run && InitialOf(run) == scan => "Initial",
        ScanKind.Swipe => "Swipe",
        ScanKind.Stash => "Stash scan",
        _ => "Final",
    };

    /// <summary>
    /// The last thing that happened in a run: its end, else its latest setup copy or scan. For an open run of a
    /// session that is not recording, this is where its duration stops.
    /// </summary>
    public DateTimeOffset LastActivityOf(MapRun run) =>
        run.EndedAt ?? ScansOf(run).Select(s => s.EndedAt ?? s.StartedAt)
            .Concat(run.Setup.Where(i => !i.IsRecalled).Select(i => i.CopiedAt))
            .Append(run.StartedAt)
            .Max();

    /// <summary>
    /// Where a run's duration stops: its end; <paramref name="now"/> for the open run while the session records;
    /// else its last activity.
    /// </summary>
    public DateTimeOffset EndOf(MapRun run, bool recording, DateTimeOffset now) =>
        run.EndedAt ?? (recording && run == CurrentRun ? now : LastActivityOf(run));

    /// <summary>The run after <paramref name="run"/>, if any.</summary>
    public MapRun? NextRun(MapRun run) => Runs.SkipWhile(r => r != run).Skip(1).FirstOrDefault();

    /// <summary>The run before <paramref name="run"/>, if any.</summary>
    public MapRun? PreviousRun(MapRun run) => Runs.TakeWhile(r => r != run).LastOrDefault();

    // --- recall (MAPPING_PLAN.md 3.2) -------------------------------------------------------------

    /// <summary>Every distinct waystone copied in the session (setups and scans), latest copy first.</summary>
    public IReadOnlyList<RecallItem> RecallWaystones() => Recall(SetupItemKind.Waystone);

    /// <summary>Every distinct tablet copied in the session (setups and scans), latest copy first.</summary>
    public IReadOnlyList<RecallItem> RecallTablets() => Recall(SetupItemKind.Tablet);

    private List<RecallItem> Recall(SetupItemKind kind)
    {
        var fromSetups = Runs.SelectMany(run => run.Setup
            .Where(i => !i.IsRecalled && (kind == SetupItemKind.Waystone ? i.Kind == SetupItemKind.Waystone : i.IsTabletSlot))
            .Select(i => new RecallItem(kind, i.RawText, i.CaptureId, run.Number, i.CopiedAt)));
        var fromScans = Scans.Where(s => !s.IsDiscarded).SelectMany(scan => scan.Cells.Values
            .Where(c => c.RawText is { } text && SetupKindOf(ItemClassOf(text)) == kind)
            .Select(c => new RecallItem(kind, c.RawText!, c.CaptureId, FindRun(scan.RunId)?.Number ?? 0, scan.EndedAt ?? scan.StartedAt)));
        return fromSetups.Concat(fromScans)
            .OrderByDescending(i => i.CopiedAt)
            .DistinctBy(i => i.RawText)
            .ToList();
    }

    // --- edits (undoable) ---------------------------------------------------------------------------

    /// <summary>Corrects a scan's kind (swipe, stash scan, final); the loot is recomputed. False when nothing changed.</summary>
    public bool SetScanKind(Guid scanId, ScanKind kind)
    {
        var scan = FindScan(scanId) ?? throw new InvalidOperationException("Scan not found.");
        return Do(new ScanKindEdit(scanId, scan.Kind, kind));
    }

    /// <summary>
    /// Removes an ended scan from its run; the loot and the check are recomputed. A Final's applied adjustments go with
    /// it, in the same edit. Its captures stay with the run. False when it is already canceled or removed.
    /// </summary>
    public bool RemoveScan(Guid scanId)
    {
        var scan = FindScan(scanId) ?? throw new InvalidOperationException("Scan not found.");
        if (scan.Status == ScanStatus.Active)
            throw new InvalidOperationException("A running scan is canceled, not removed.");
        if (scan.IsDiscarded)
            return false;
        var run = FindRun(scan.RunId)!;
        var adjustments = run.Adjustments.Any(a => a.SourceScanId == scanId)
            ? new AdjustmentsEdit(run.Id, run.Adjustments.ToList(), run.Adjustments.Where(a => a.SourceScanId != scanId).ToList())
            : null;
        return Do(new ScanRemovedEdit(scanId, scan.Status, ScanStatus.Removed, adjustments));
    }

    /// <summary>Ends the open run (needed for the last run of a session). False when no run is open.</summary>
    public bool EndRun(DateTimeOffset at) =>
        CurrentRun is { } run && Do(new RunEndEdit(run.Id, null, at));

    /// <summary>
    /// Assigns an earlier copy to a run's setup: a waystone replaces the run's waystone; a tablet fills a free slot
    /// (false when the tablet is already there or all four slots are taken).
    /// </summary>
    public bool AssignSetup(Guid runId, RecallItem item)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        var recalled = Recalled(item, item.Kind);
        List<SetupItem> after;
        if (item.Kind == SetupItemKind.Waystone)
        {
            if (run.Waystone?.RawText == item.RawText)
                return false;
            after = [recalled, .. run.Setup.Where(i => i.Kind != SetupItemKind.Waystone)];
        }
        else
        {
            if (run.Tablets.Count() >= MaxTablets || run.Tablets.Any(t => t.RawText == item.RawText))
                return false;
            after = [.. run.Setup, recalled];
        }
        return Do(new SetupEdit(runId, run.Setup.ToList(), after));
    }

    /// <summary>
    /// Sets a run's setup from the recall dialog in one edit: <paramref name="waystone"/> (null keeps the current one)
    /// and exactly <paramref name="tablets"/> (tablets already in the run stay as they are; others are recalled).
    /// </summary>
    public bool SetSetup(Guid runId, RecallItem? waystone, IReadOnlyList<RecallItem> tablets)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        if (tablets.Count > MaxTablets)
            throw new InvalidOperationException($"A run has at most {MaxTablets} tablets.");
        var after = new List<SetupItem>();
        if (waystone is null || run.Waystone?.RawText == waystone.RawText)
        {
            if (run.Waystone is { } kept)
                after.Add(kept);
        }
        else
        {
            after.Add(Recalled(waystone, SetupItemKind.Waystone));
        }
        foreach (var tablet in tablets.DistinctBy(t => t.RawText))
            after.Add(run.Tablets.FirstOrDefault(t => t.RawText == tablet.RawText) ?? Recalled(tablet, SetupItemKind.Tablet));
        after.AddRange(run.Setup.Where(i => i.Kind == SetupItemKind.NotSetup));
        return Do(new SetupEdit(runId, run.Setup.ToList(), after));
    }

    private static SetupItem Recalled(RecallItem item, SetupItemKind kind) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, CaptureId = item.CaptureId, RawText = item.RawText, CopiedAt = item.CopiedAt, RecalledFromRun = item.RunNumber,
    };

    /// <summary>
    /// Adds the previous run's tablets to a run's free slots (tablets with uses left usually carry over, with one use
    /// fewer). False when nothing was added.
    /// </summary>
    public bool UsePreviousTablets(Guid runId)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        if (PreviousRun(run) is not { } previous)
            return false;
        var after = run.Setup.ToList();
        foreach (var tablet in previous.Tablets)
        {
            if (after.Count(i => i.IsTabletSlot) >= MaxTablets)
                break;
            if (after.Any(i => i.IsTabletSlot && i.RawText == tablet.RawText))
                continue;
            after.Add(new SetupItem
            {
                Id = Guid.NewGuid(), Kind = SetupItemKind.Tablet, CaptureId = tablet.CaptureId, RawText = tablet.RawText,
                CopiedAt = tablet.CopiedAt, RecalledFromRun = previous.Number,
            });
        }
        return after.Count != run.Setup.Count && Do(new SetupEdit(runId, run.Setup.ToList(), after));
    }

    /// <summary>Takes an item out of a run's setup (its capture stays with the run).</summary>
    public bool RemoveSetupItem(Guid runId, Guid itemId)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        return run.Setup.Any(i => i.Id == itemId) && Do(new SetupEdit(runId, run.Setup.ToList(), run.Setup.Where(i => i.Id != itemId).ToList()));
    }

    /// <summary>
    /// Applies adjustments proposed (and possibly edited) by a Final scan's check. Zero amounts are skipped. False when
    /// nothing was applied.
    /// </summary>
    public bool ApplyAdjustments(Guid finalScanId, IEnumerable<(string Key, string Name, int Amount)> adjustments, DateTimeOffset at)
    {
        var scan = FindScan(finalScanId) ?? throw new InvalidOperationException("Scan not found.");
        var run = FindRun(scan.RunId)!;
        var added = adjustments.Where(a => a.Amount != 0)
            .Select(a => new LootAdjustment(Guid.NewGuid(), a.Key, a.Name, a.Amount, finalScanId, at))
            .ToList();
        return added.Count > 0 && Do(new AdjustmentsEdit(run.Id, run.Adjustments.ToList(), [.. run.Adjustments, .. added]));
    }

    /// <summary>Removes an applied adjustment (the check proposes it again).</summary>
    public bool RemoveAdjustment(Guid runId, Guid adjustmentId)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        return run.Adjustments.Any(a => a.Id == adjustmentId)
            && Do(new AdjustmentsEdit(runId, run.Adjustments.ToList(), run.Adjustments.Where(a => a.Id != adjustmentId).ToList()));
    }

    /// <summary>Dismisses a Final scan's check warning (or brings it back).</summary>
    public bool DismissCheck(Guid finalScanId, bool dismissed)
    {
        var scan = FindScan(finalScanId) ?? throw new InvalidOperationException("Scan not found.");
        return Do(new CheckDismissedEdit(finalScanId, scan.CheckDismissed, dismissed));
    }

    /// <summary>Adds a tag; an equal tag already on the owner is not added twice. Null when the text is no tag.</summary>
    public MapTag? AddTag(Guid ownerId, string text)
    {
        RequireOwner(ownerId);
        if (MapTag.Parse(text) is not { } tag)
            return null;
        var before = TagsOf(ownerId).ToList();
        if (!before.Contains(tag))
            Do(new TagsEdit(ownerId, before, [.. before, tag]));
        return tag;
    }

    public bool RemoveTag(Guid ownerId, MapTag tag)
    {
        var before = TagsOf(ownerId).ToList();
        return before.Contains(tag) && Do(new TagsEdit(ownerId, before, before.Where(t => t != tag).ToList()));
    }

    /// <summary>Sets the owner's note; blank text removes it.</summary>
    public bool SetNote(Guid ownerId, string? text)
    {
        RequireOwner(ownerId);
        var after = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return Do(new NoteEdit(ownerId, NoteOf(ownerId), after));
    }

    /// <summary>Confirms a possible tablet (<see cref="SetupItemKind.Tablet"/>) or rejects it (<see cref="SetupItemKind.NotSetup"/>).</summary>
    public bool SetSetupItemKind(Guid itemId, SetupItemKind kind)
    {
        var item = Runs.SelectMany(r => r.Setup).FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException("Setup item not found.");
        if ((item.Kind == SetupItemKind.Waystone) != (kind == SetupItemKind.Waystone))
            throw new InvalidOperationException("The waystone starts the run; it cannot change kind.");
        return Do(new SetupKindEdit(itemId, item.Kind, kind));
    }

    /// <summary>Excludes a run from the Analyse statistics (or includes it again).</summary>
    public bool SetRunExcluded(Guid runId, bool excluded)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        return Do(new RunExcludedEdit(runId, run.Excluded, excluded));
    }

    /// <summary>
    /// Deletes a run with its setup, scans, adjustments, tags and note. Not undoable (the app asks first): edits that
    /// refer to it leave the undo/redo history. Its captures stay in the session's capture log, and the other runs keep
    /// their numbers. Not while one of its scans is running.
    /// </summary>
    public bool DeleteRun(Guid runId)
    {
        var run = FindRun(runId) ?? throw new InvalidOperationException("Run not found.");
        if (ScansOf(run).Any(s => s.Status == ScanStatus.Active))
            throw new InvalidOperationException("Finish or cancel the running scan before deleting its run.");

        var scanIds = ScansOf(run).Select(s => s.Id).ToHashSet();
        var itemIds = _undo.Concat(_redo).OfType<SetupEdit>().Where(e => e.RunId == runId).SelectMany(e => e.Before.Concat(e.After))
            .Concat(run.Setup).Select(i => i.Id).ToHashSet();
        DropEdits(e => e switch
        {
            RunEndEdit x => x.RunId == runId,
            RunExcludedEdit x => x.RunId == runId,
            SetupEdit x => x.RunId == runId,
            AdjustmentsEdit x => x.RunId == runId,
            TagsEdit x => x.OwnerId == runId,
            NoteEdit x => x.OwnerId == runId,
            SetupKindEdit x => itemIds.Contains(x.ItemId),
            ScanKindEdit x => scanIds.Contains(x.ScanId),
            CheckDismissedEdit x => scanIds.Contains(x.ScanId),
            ScanRemovedEdit x => scanIds.Contains(x.ScanId),
            _ => false,
        });
        Scans.RemoveAll(s => s.RunId == runId);
        Runs.Remove(run);
        Tags.Remove(runId);
        Notes.Remove(runId);
        return true;
    }

    public MappingEdit? Undo()
    {
        if (!_undo.TryPop(out var edit))
            return null;
        Set(edit, forward: false);
        _redo.Push(edit);
        return edit;
    }

    public MappingEdit? Redo()
    {
        if (!_redo.TryPop(out var edit))
            return null;
        Set(edit, forward: true);
        _undo.Push(edit);
        return edit;
    }

    // --- internals ----------------------------------------------------------------------------------

    private Scan StartScan(Guid sweepId, string label, DateTimeOffset at, ScanKind kind)
    {
        var run = CurrentRun ?? StartRun(at, capture: null);
        var scan = new Scan
        {
            Id = Guid.NewGuid(), RunId = run.Id, SweepId = sweepId, Label = label, Kind = kind, Status = ScanStatus.Active, StartedAt = at,
        };
        Scans.Add(scan);
        return scan;
    }

    /// <summary>Ends the open run and starts the next; <paramref name="capture"/> is its waystone (null: started by hand).</summary>
    private MapRun StartRun(DateTimeOffset at, ProcessedCapture? capture)
    {
        if (Runs.Count > 0 && Runs[^1].IsOpen)
            Runs[^1].EndedAt = at;
        var run = new MapRun { Id = Guid.NewGuid(), Number = Runs.Count == 0 ? 1 : Runs[^1].Number + 1, StartedAt = at };
        if (capture is not null)
            run.Setup.Add(SetupFrom(capture, SetupItemKind.Waystone));
        Runs.Add(run);
        if (capture is not null)
            Attach(capture);

        // Ending an earlier run is no longer undoable once a later run exists (it would reopen it in the past).
        DropEdits(e => e is RunEndEdit);
        return run;
    }

    private static SetupItem SetupFrom(ProcessedCapture capture, SetupItemKind kind) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, CaptureId = capture.CaptureId, RawText = capture.RawText, CopiedAt = capture.CapturedAt,
    };

    private void Attach(ProcessedCapture capture)
    {
        if (capture.CaptureId is { } id && CurrentRun is { } run && !run.CaptureIds.Contains(id))
            run.CaptureIds.Add(id);
    }

    /// <summary>The same waystone again, before anything but its setup happened: a repeated copy, not a new run.</summary>
    private bool IsSameWaystone(MapRun run, string rawText) =>
        run.Waystone?.RawText == rawText && IsFresh(run);

    /// <summary>Nothing but setup happened in the run yet: no scan, no other copy.</summary>
    private bool IsFresh(MapRun run) => !ScansOf(run).Any() && !HasNonSetupCaptures(run);

    /// <summary>Gathering has begun: a stash or final scan was taken (swipes, like the Initial, still allow setup).</summary>
    private bool IsGathering(MapRun run) => ScansOf(run).Any(s => s.Kind != ScanKind.Swipe && !s.IsDiscarded);

    /// <summary>
    /// The setup phase: before the run's first stash or final scan, fewer than 4 tablets. A known tablet joins any time in
    /// it; an unknown item only while nothing but setup (and swipes) was copied, within <see cref="MappingOptions.SetupWindow"/> of the
    /// last setup copy (or of the start of a run started by hand).
    /// </summary>
    private SetupItemKind? SetupKindFor(MapRun run, string? itemClass, DateTimeOffset at, MappingOptions options)
    {
        if (IsGathering(run) || run.Tablets.Count() >= MaxTablets)
            return null;
        if (SetupKindOf(itemClass) == SetupItemKind.Tablet)
            return SetupItemKind.Tablet;
        var lastSetup = run.Setup.Where(i => !i.IsRecalled).Select(i => i.CopiedAt).Append(run.StartedAt).Max();
        return !HasLooseCaptures(run) && at - lastSetup <= options.SetupWindow ? SetupItemKind.PossibleTablet : null;
    }

    /// <summary>Waystone or tablet by item class; null for anything else.</summary>
    private static SetupItemKind? SetupKindOf(string? itemClass) =>
        itemClass == WaystoneItemClass ? SetupItemKind.Waystone
        : itemClass?.Contains("Tablet", StringComparison.OrdinalIgnoreCase) == true ? SetupItemKind.Tablet
        : null;

    private static bool HasNonSetupCaptures(MapRun run) =>
        run.CaptureIds.Except(run.Setup.Where(i => i.Kind != SetupItemKind.NotSetup && i.CaptureId is not null).Select(i => i.CaptureId!.Value)).Any();

    /// <summary>Copies of the run outside its setup and outside its scans (a loot check, a pickup).</summary>
    private bool HasLooseCaptures(MapRun run) =>
        run.CaptureIds
            .Except(run.Setup.Where(i => i.CaptureId is not null).Select(i => i.CaptureId!.Value))
            .Except(ScansOf(run).SelectMany(s => s.Cells.Values).Where(c => c.CaptureId is not null).Select(c => c.CaptureId!.Value))
            .Any();

    private static string? ItemClassOf(string rawText) =>
        ItemText.FindValue(ItemText.Sections(ItemText.Normalize(rawText))[0], ItemText.ItemClassPrefix);

    private void RequireOwner(Guid ownerId)
    {
        if (ownerId != SessionId && FindRun(ownerId) is null)
            throw new InvalidOperationException("Tags and notes belong to the session or one of its runs.");
    }

    private bool Do(MappingEdit edit)
    {
        if (!edit.Changes)
            return false;
        Set(edit, forward: true);
        _undo.Push(edit);
        _redo.Clear();
        return true;
    }

    private void DropEdits(Func<MappingEdit, bool> predicate)
    {
        foreach (var stack in new[] { _undo, _redo })
        {
            var kept = stack.Where(e => !predicate(e)).Reverse().ToList();
            stack.Clear();
            foreach (var e in kept)
                stack.Push(e);
        }
    }

    private void Set(MappingEdit edit, bool forward)
    {
        switch (edit)
        {
            case ScanKindEdit e:
                FindScan(e.ScanId)!.Kind = forward ? e.After : e.Before;
                break;
            case RunEndEdit e:
                FindRun(e.RunId)!.EndedAt = forward ? e.After : e.Before;
                break;
            case TagsEdit e:
                var tags = forward ? e.After : e.Before;
                if (tags.Count == 0)
                    Tags.Remove(e.OwnerId);
                else
                    Tags[e.OwnerId] = [.. tags];
                break;
            case NoteEdit e:
                if ((forward ? e.After : e.Before) is { } note)
                    Notes[e.OwnerId] = note;
                else
                    Notes.Remove(e.OwnerId);
                break;
            case RunExcludedEdit e:
                FindRun(e.RunId)!.Excluded = forward ? e.After : e.Before;
                break;
            case SetupKindEdit e:
                Runs.SelectMany(r => r.Setup).First(i => i.Id == e.ItemId).Kind = forward ? e.After : e.Before;
                break;
            case SetupEdit e:
                FindRun(e.RunId)!.Setup = [.. forward ? e.After : e.Before];
                break;
            case AdjustmentsEdit e:
                FindRun(e.RunId)!.Adjustments = [.. forward ? e.After : e.Before];
                break;
            case CheckDismissedEdit e:
                FindScan(e.ScanId)!.CheckDismissed = forward ? e.After : e.Before;
                break;
            case ScanRemovedEdit e:
                FindScan(e.ScanId)!.Status = forward ? e.After : e.Before;
                if (e.Adjustments is { } adjustments)
                    Set(adjustments, forward);
                break;
        }
    }
}

/// <summary>A reversible user edit: a value before and after (MAPPING_PLAN.md 6, the change-entry idea).</summary>
public abstract record MappingEdit
{
    public abstract bool Changes { get; }
}

public sealed record ScanKindEdit(Guid ScanId, ScanKind Before, ScanKind After) : MappingEdit
{
    public override bool Changes => Before != After;
}

public sealed record RunEndEdit(Guid RunId, DateTimeOffset? Before, DateTimeOffset? After) : MappingEdit
{
    public override bool Changes => Before != After;
}

public sealed record TagsEdit(Guid OwnerId, IReadOnlyList<MapTag> Before, IReadOnlyList<MapTag> After) : MappingEdit
{
    public override bool Changes => !Before.SequenceEqual(After);
}

public sealed record NoteEdit(Guid OwnerId, string? Before, string? After) : MappingEdit
{
    public override bool Changes => Before != After;
}

public sealed record SetupKindEdit(Guid ItemId, SetupItemKind Before, SetupItemKind After) : MappingEdit
{
    public override bool Changes => Before != After;
}

/// <summary>A run's whole setup before and after (recall, previous run's tablets, removing an item).</summary>
public sealed record SetupEdit(Guid RunId, IReadOnlyList<SetupItem> Before, IReadOnlyList<SetupItem> After) : MappingEdit
{
    public override bool Changes => !Before.SequenceEqual(After);
}

public sealed record AdjustmentsEdit(Guid RunId, IReadOnlyList<LootAdjustment> Before, IReadOnlyList<LootAdjustment> After) : MappingEdit
{
    public override bool Changes => !Before.SequenceEqual(After);
}

public sealed record CheckDismissedEdit(Guid ScanId, bool Before, bool After) : MappingEdit
{
    public override bool Changes => Before != After;
}

/// <summary>A scan removed (with its status before), together with the adjustments a removed Final had proposed.</summary>
public sealed record ScanRemovedEdit(Guid ScanId, ScanStatus Before, ScanStatus After, AdjustmentsEdit? Adjustments) : MappingEdit
{
    public override bool Changes => Before != After;
}

public sealed record RunExcludedEdit(Guid RunId, bool Before, bool After) : MappingEdit
{
    public override bool Changes => Before != After;
}
