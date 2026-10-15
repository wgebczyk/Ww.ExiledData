using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Parsing;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Core.Tracking;

/// <summary>
/// Current state of one session derived from its capture log, with multi-level undo/redo and sweeps.
/// Not thread-safe: callers serialize captures and undo/redo through one queue.
/// </summary>
public sealed class SessionTracker
{
    private readonly TimeProvider _time;
    private readonly List<Capture> _log = [];
    private readonly Dictionary<Guid, Capture> _capturesById = [];
    private readonly List<RegularItem> _items = [];
    private readonly Dictionary<string, StackableItem> _stackables = [];
    private readonly Stack<ChangeEntry> _undo = new();
    private readonly Stack<ChangeEntry> _redo = new();
    // Objects taken out of the current state by a change (or its undo), keyed by change id,
    // so undo/redo can put back exactly the same object.
    private readonly Dictionary<Guid, RegularItem> _detachedItems = [];
    private readonly Dictionary<Guid, StackableItem> _detachedStackables = [];
    private readonly List<Sweep> _sweeps = [];
    // Failed/skipped marks of cells without a live capture. Not undoable: a capture replaces them.
    private readonly Dictionary<(Guid SweepId, GridCell Cell), CellState> _marks = [];

    public SessionTracker(Session session, int positionTolerance, TimeProvider? time = null)
    {
        Session = session;
        PositionTolerance = positionTolerance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Rebuilds the tracker of a stored session that is being continued, from its capture log, current state and
    /// sweeps. The undo/redo history starts empty: what was done before the session ended can no longer be undone
    /// (removed items are not stored, so their undo could not be rebuilt). No sweep is running afterwards.
    /// </summary>
    public static SessionTracker Restore(Session session, IEnumerable<Capture> log, IEnumerable<RegularItem> items,
        IEnumerable<StackableItem> stackables, IEnumerable<Sweep> sweeps, IEnumerable<SweepCell> cells, int positionTolerance,
        TimeProvider? time = null)
    {
        var tracker = new SessionTracker(session, positionTolerance, time);
        foreach (var capture in log)
        {
            if (capture.SessionId != session.Id)
                throw new ArgumentException($"Capture {capture.Id} belongs to another session.", nameof(log));
            tracker._log.Add(capture);
            tracker._capturesById[capture.Id] = capture;
        }
        tracker._items.AddRange(items);
        foreach (var s in stackables)
            tracker._stackables[s.Key] = s;
        tracker._sweeps.AddRange(sweeps);
        foreach (var sweep in tracker._sweeps.Where(s => s.Status == SweepStatus.Active))
        {
            sweep.Status = SweepStatus.Interrupted;
            sweep.EndedAt ??= sweep.StartedAt;
        }
        // Captured and covered cells are derived from the log; only the marks need restoring.
        foreach (var cell in cells)
        {
            if (cell is { CaptureId: null, State: CellState.Failed or CellState.Skipped })
                tracker._marks[(cell.SweepId, cell.Cell)] = cell.State;
        }
        return tracker;
    }

    public Session Session { get; }

    /// <summary>Pixels; used outside sweeps only. Changing it affects only future captures.</summary>
    public int PositionTolerance
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }
    public IReadOnlyList<Capture> Log => _log;
    public IReadOnlyList<RegularItem> Items => _items;
    public IReadOnlyCollection<StackableItem> Stackables => _stackables.Values;
    public IReadOnlyList<Sweep> Sweeps => _sweeps;
    public Sweep? ActiveSweep { get; private set; }
    public bool CanUndo => Session.IsActive && _undo.Count > 0;
    public bool CanRedo => Session.IsActive && _redo.Count > 0;
    /// <summary>Applied changes, most recent first.</summary>
    public IEnumerable<ChangeEntry> History => _undo;
    /// <summary>Undone changes available to redo, most recently undone first.</summary>
    public IEnumerable<ChangeEntry> UndoneChanges => _redo;

    public CaptureResult Apply(string? rawText, ScreenPoint position)
    {
        EnsureActive();
        if (!ItemText.IsItem(rawText))
            return new CaptureResult(CaptureOutcome.NotAnItem);

        var text = ItemText.Normalize(rawText!);
        var now = _time.GetUtcNow();
        var parse = StackableParser.Parse(text);
        var warnings = parse.Warning is null ? null : new[] { parse.Warning };
        var place = ActiveSweep is { } sweep && sweep.Grid.CellAt(position) is { } cell ? new Place(sweep.Id, cell) : (Place?)null;

        var result = parse.Status == StackParseStatus.Parsed
            ? ApplyStackable(text, position, place, now, parse)
            : ApplyRegular(text, position, place, now, parse.Status == StackParseStatus.HeaderUnparsed, warnings);
        return result with { Cell = place?.Cell, OutsideGrid = ActiveSweep is not null && place is null };
    }

    /// <summary>Manual delete of any regular item (not just the last one). Undoable; withdraws its captures.</summary>
    public CaptureResult? RemoveItem(Guid captureId)
    {
        EnsureActive();
        var item = _items.FirstOrDefault(i => i.CaptureId == captureId);
        if (item is null)
            return null;

        var now = _time.GetUtcNow();
        var change = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.ItemRemoved, captureId, item.IdentityKey));
        DetachItem(change);
        SetWithdrawn(item, now);
        return new CaptureResult(CaptureOutcome.Removed, change);
    }

    /// <summary>
    /// Manual delete of a stackable. Undoable. Its captures stay in the log as they are; the change entry
    /// references the latest one.
    /// </summary>
    public CaptureResult? RemoveStackable(string key)
    {
        EnsureActive();
        if (!_stackables.TryGetValue(key, out var stackable))
            return null;

        var latest = _log.Last(c => c.IdentityKey == key && !c.IsWithdrawn);
        var change = Record(new ChangeEntry(Guid.NewGuid(), _time.GetUtcNow(), ChangeType.StackableRemoved, latest.Id, key,
            Before: Snapshot(stackable)));
        DetachStackable(change);
        return new CaptureResult(CaptureOutcome.Removed, change);
    }

    /// <summary>
    /// Turns the most recently covered cell of an item into an item of its own (for two identical items side by
    /// side). Undoable. Returns null when the item covers no other cell.
    /// </summary>
    public CaptureResult? SplitCovered(Guid itemCaptureId)
    {
        EnsureActive();
        var parent = _items.FirstOrDefault(i => i.CaptureId == itemCaptureId);
        if (parent is not { CoveredCaptureIds.Count: > 0 })
            return null;

        var capture = _capturesById[parent.CoveredCaptureIds[^1]];
        parent.CoveredCaptureIds.RemoveAt(parent.CoveredCaptureIds.Count - 1);
        _items.Add(new RegularItem
        {
            CaptureId = capture.Id,
            IdentityKey = capture.IdentityKey,
            Label = parent.Label,
            RawText = capture.RawText,
            Position = capture.Position,
            SweepId = capture.SweepId,
            Cell = capture.Cell,
            LastSeenAt = capture.CapturedAt,
            PossibleDuplicate = true,
            NeedsReview = parent.NeedsReview,
        });
        var change = Record(new ChangeEntry(Guid.NewGuid(), _time.GetUtcNow(), ChangeType.ItemSplit, capture.Id, capture.IdentityKey,
            RelatedCaptureId: parent.CaptureId));
        return new CaptureResult(CaptureOutcome.Split, change, Cell: capture.Cell);
    }

    public CaptureResult? Undo()
    {
        EnsureActive();
        if (!_undo.TryPop(out var change))
            return null;

        Revert(change);
        // Undoing a cancel brings back the sweep's changes as they were: individually undoable again.
        foreach (var child in change.Children?.Reverse() ?? [])
            _undo.Push(child);
        _redo.Push(change);
        return new CaptureResult(CaptureOutcome.Undone, change, StackCountAfterUndoRedo(change, undone: true), Cell: CellOf(change));
    }

    public CaptureResult? Redo()
    {
        EnsureActive();
        if (!_redo.TryPop(out var change))
            return null;

        // The cancel's children went back on the undo stack when it was undone; LIFO keeps them on top.
        foreach (var child in change.Children ?? [])
        {
            if (_undo.Pop() != child)
                throw new InvalidOperationException("Undo history is out of order.");
        }
        Reapply(change);
        _undo.Push(change);
        return new CaptureResult(CaptureOutcome.Redone, change, StackCountAfterUndoRedo(change, undone: false), Cell: CellOf(change));
    }

    // --- sweeps ---------------------------------------------------------------------------------

    /// <summary>Starts a sweep over <paramref name="grid"/>; a sweep still running is finished first.</summary>
    public Sweep StartSweep(GridKind kind, GridGeometry grid, string? label = null)
    {
        EnsureActive();
        if (grid.Columns != kind.Columns || grid.Rows != kind.Rows)
            throw new ArgumentException($"Grid is {grid.Columns}x{grid.Rows} but {kind} was requested.", nameof(grid));
        if (grid.CellSize < GridGeometry.MinCellSize)
            throw new ArgumentException("Grid cells are too small.", nameof(grid));
        FinishSweep();

        var sweep = new Sweep
        {
            Id = Guid.NewGuid(),
            SessionId = Session.Id,
            Kind = kind,
            Label = string.IsNullOrWhiteSpace(label) ? $"{kind.Name} {_sweeps.Count(s => s.Kind.Name == kind.Name) + 1}" : label.Trim(),
            Grid = grid,
            StartedAt = _time.GetUtcNow(),
        };
        _sweeps.Add(sweep);
        ActiveSweep = sweep;
        return sweep;
    }

    /// <summary>
    /// Moves the running sweep onto <paramref name="grid"/>. Only while none of its cells holds anything (capture or
    /// mark), since cells are positions in the grid. Returns false when there is no such sweep.
    /// </summary>
    public bool RepositionSweep(GridGeometry grid)
    {
        EnsureActive();
        if (ActiveSweep is not { } sweep || CellsOf(sweep.Id).Count > 0)
            return false;
        if (grid.Columns != sweep.Kind.Columns || grid.Rows != sweep.Kind.Rows)
            throw new ArgumentException($"Grid is {grid.Columns}x{grid.Rows} but the sweep is {sweep.Kind}.", nameof(grid));
        if (grid.CellSize < GridGeometry.MinCellSize)
            throw new ArgumentException("Grid cells are too small.", nameof(grid));
        sweep.Grid = grid;
        return true;
    }

    /// <summary>Ends the running sweep keeping all its captures. Returns it, or null when none was running.</summary>
    public Sweep? FinishSweep()
    {
        if (ActiveSweep is not { } sweep)
            return null;
        sweep.Status = SweepStatus.Finished;
        sweep.EndedAt = _time.GetUtcNow();
        ActiveSweep = null;
        return sweep;
    }

    /// <summary>Changes made while the running sweep was active that a cancel would withdraw.</summary>
    public int ChangesInActiveSweep => ActiveSweep is { } sweep ? _undo.TakeWhile(c => c.SweepId == sweep.Id).Count() : 0;

    /// <summary>
    /// Ends the running sweep and withdraws every change made during it (captures in and outside the grid, manual
    /// removals) as one undoable entry. The change is null when there was nothing to withdraw.
    /// </summary>
    public (Sweep Sweep, ChangeEntry? Change)? CancelSweep()
    {
        EnsureActive();
        if (ActiveSweep is not { } sweep)
            return null;

        // Changes made during the sweep sit contiguously on top of the undo stack: it is the most recent activity.
        var children = new List<ChangeEntry>();
        while (_undo.TryPeek(out var top) && top.SweepId == sweep.Id)
        {
            _undo.Pop();
            Revert(top);
            children.Add(top);
        }

        var now = _time.GetUtcNow();
        sweep.Status = SweepStatus.Canceled;
        sweep.EndedAt = now;
        ActiveSweep = null;
        if (children.Count == 0)
            return (sweep, null);

        var change = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.SweepCanceled, children[0].CaptureId, $"sweep:{sweep.Id}",
            SweepId: sweep.Id, Children: children));
        return (sweep, change);
    }

    /// <summary>Marks the cell under <paramref name="position"/> failed, unless it already holds a capture.</summary>
    public GridCell? MarkFailed(ScreenPoint position)
    {
        if (!Session.IsActive || ActiveSweep is not { } sweep || sweep.Grid.CellAt(position) is not { } cell
            || LiveCaptureAt(sweep.Id, cell) is not null)
            return null;
        _marks[(sweep.Id, cell)] = CellState.Failed;
        return cell;
    }

    /// <summary>
    /// Toggles "skipped" on the cell under <paramref name="position"/>. Returns the cell and its new state, or null
    /// when outside the grid or the cell already holds a capture.
    /// </summary>
    public (GridCell Cell, CellState State)? ToggleSkip(ScreenPoint position)
    {
        if (!Session.IsActive || ActiveSweep is not { } sweep || sweep.Grid.CellAt(position) is not { } cell
            || LiveCaptureAt(sweep.Id, cell) is not null)
            return null;

        if (_marks.TryGetValue((sweep.Id, cell), out var mark) && mark == CellState.Skipped)
        {
            _marks.Remove((sweep.Id, cell));
            return (cell, CellState.Pending);
        }
        _marks[(sweep.Id, cell)] = CellState.Skipped;
        return (cell, CellState.Skipped);
    }

    /// <summary>Non-pending cells of a sweep, derived from live captures and marks.</summary>
    public IReadOnlyList<SweepCell> CellsOf(Guid sweepId)
    {
        var covered = _items.SelectMany(i => i.CoveredCaptureIds).ToHashSet();
        var cells = new Dictionary<GridCell, SweepCell>();
        foreach (var c in _log)
        {
            if (c is { IsWithdrawn: false, Cell: { } cell } && c.SweepId == sweepId)
                cells[cell] = new SweepCell(sweepId, cell, covered.Contains(c.Id) ? CellState.Covered : CellState.Captured, c.Id);
        }
        foreach (var ((id, cell), state) in _marks)
        {
            if (id == sweepId)
                cells.TryAdd(cell, new SweepCell(sweepId, cell, state, null));
        }
        return cells.Values.OrderBy(c => c.Cell.Row).ThenBy(c => c.Cell.Column).ToList();
    }

    /// <summary>
    /// Items found by a sweep: captured cells. Covered cells are further cells of an item already counted;
    /// there is no "n of total" since item sizes are unknown.
    /// </summary>
    public int ItemCountOf(Guid sweepId) => CellsOf(sweepId).Count(c => c.State == CellState.Captured);

    // --- capture handling -----------------------------------------------------------------------

    private readonly record struct Place(Guid SweepId, GridCell Cell);

    private CaptureResult ApplyRegular(string text, ScreenPoint position, Place? place, DateTimeOffset now, bool needsReview, string[]? warnings)
    {
        var key = IdentityKeys.Regular(text);
        var sameText = _items.Where(i => i.IdentityKey == key).ToList();

        // Inside a sweep the cell is the position (no pixel tolerance); outside, pixels within tolerance.
        var existing = place is { } p
            ? sameText.FirstOrDefault(i => PlacesOf(i).Contains(p))
            : sameText.FirstOrDefault(i => i.Position.IsNear(position, PositionTolerance));
        if (existing is not null)
        {
            existing.LastSeenAt = now;
            return new CaptureResult(CaptureOutcome.AlreadyRecorded, Warnings: warnings);
        }

        var capture = AppendCapture(text, position, place, now, CaptureKind.Regular, key);

        // Same text right next to a cell of an item from this sweep: most likely another cell of a multi-cell item.
        var neighbour = place is { } q
            ? sameText.FirstOrDefault(i => PlacesOf(i).Any(o => o.SweepId == q.SweepId && o.Cell.IsAdjacent(q.Cell)))
            : null;
        if (neighbour is not null)
        {
            neighbour.CoveredCaptureIds.Add(capture.Id);
            neighbour.LastSeenAt = now;
            var covered = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.CellCovered, capture.Id, key,
                RelatedCaptureId: neighbour.CaptureId));
            return new CaptureResult(CaptureOutcome.Covered, covered, Warnings: warnings);
        }

        _items.Add(new RegularItem
        {
            CaptureId = capture.Id,
            IdentityKey = key,
            Label = ItemLabel.From(text),
            RawText = text,
            Position = position,
            SweepId = place?.SweepId,
            Cell = place?.Cell,
            LastSeenAt = now,
            PossibleDuplicate = sameText.Count > 0,
            NeedsReview = needsReview,
        });

        var change = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.ItemAdded, capture.Id, key));
        var outcome = sameText.Count > 0 ? CaptureOutcome.SiblingAdded : CaptureOutcome.Added;
        return new CaptureResult(outcome, change, Warnings: warnings);
    }

    private CaptureResult ApplyStackable(string text, ScreenPoint position, Place? place, DateTimeOffset now, StackParseResult parse)
    {
        var key = IdentityKeys.Stackable(parse.ItemClass!, parse.Rarity!, parse.Name!, parse.MaxStack);

        if (_stackables.TryGetValue(key, out var existing))
        {
            // The same stack only at the same place: inside a sweep its cell (no pixel tolerance), outside pixels
            // within tolerance. Anywhere else it is another stack of the same currency, and counts add up.
            var same = place is { } p
                ? existing.Stacks.FirstOrDefault(s => s.SweepId == p.SweepId && s.Cell == p.Cell)
                : existing.Stacks.FirstOrDefault(s => s.Position.IsNear(position, PositionTolerance));
            if (same?.Count == parse.Count)
            {
                // Nothing changes for the stackable, but a sweep cell not showing it (any more) still gets marked.
                if (place is { } q && LiveCaptureAt(q.SweepId, q.Cell)?.IdentityKey != key)
                {
                    var marking = AppendCapture(text, position, place, now, CaptureKind.Stackable, key);
                    var marked = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.CellMarked, marking.Id, key));
                    return new CaptureResult(CaptureOutcome.AlreadyRecorded, marked, existing.Count);
                }
                return new CaptureResult(CaptureOutcome.AlreadyRecorded, StackCount: existing.Count);
            }

            // A new count at the same place replaces that stack; a new place adds one.
            var before = Snapshot(existing);
            var updateCapture = AppendCapture(text, position, place, now, CaptureKind.Stackable, key);
            var stack = new StackPlace(updateCapture.Id, position, place?.SweepId, place?.Cell, parse.Count);
            var stacks = same is null ? [.. existing.Stacks, stack] : existing.Stacks.Select(s => s == same ? stack : s).ToList();
            var after = new StackableSnapshot(stacks.Sum(s => s.Count), text, now, StackableParser.Version, stacks);
            Restore(existing, after);
            var update = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.StackableCountUpdated, updateCapture.Id, key, before, after));
            return new CaptureResult(CaptureOutcome.StackableUpdated, update, existing.Count);
        }

        var capture = AppendCapture(text, position, place, now, CaptureKind.Stackable, key);
        _stackables[key] = new StackableItem
        {
            Key = key,
            ItemClass = parse.ItemClass!,
            Rarity = parse.Rarity!,
            Name = parse.Name!,
            MaxStack = parse.MaxStack,
            Stacks = [new StackPlace(capture.Id, position, place?.SweepId, place?.Cell, parse.Count)],
            LatestRawText = text,
            UpdatedAt = now,
            ParserVersion = StackableParser.Version,
        };
        var change = Record(new ChangeEntry(Guid.NewGuid(), now, ChangeType.StackableAdded, capture.Id, key));
        return new CaptureResult(CaptureOutcome.StackableAdded, change, parse.Count);
    }

    private Capture AppendCapture(string text, ScreenPoint position, Place? place, DateTimeOffset now, CaptureKind kind, string key)
    {
        var capture = new Capture
        {
            Id = Guid.NewGuid(),
            SessionId = Session.Id,
            RawText = text,
            Position = position,
            CapturedAt = now,
            Kind = kind,
            IdentityKey = key,
            SweepId = place?.SweepId,
            Cell = place?.Cell,
        };
        _log.Add(capture);
        _capturesById[capture.Id] = capture;
        if (place is { } p)
            _marks.Remove((p.SweepId, p.Cell));
        return capture;
    }

    /// <summary>Sweep cells an item occupies: its own and the ones it covers.</summary>
    private IEnumerable<Place> PlacesOf(RegularItem item) =>
        new[] { item.CaptureId }.Concat(item.CoveredCaptureIds)
            .Select(id => _capturesById[id])
            .Where(c => c is { SweepId: not null, Cell: not null })
            .Select(c => new Place(c.SweepId!.Value, c.Cell!.Value));

    private Capture? LiveCaptureAt(Guid sweepId, GridCell cell) =>
        _log.LastOrDefault(c => !c.IsWithdrawn && c.SweepId == sweepId && c.Cell == cell);

    // --- undo/redo ------------------------------------------------------------------------------

    private void Revert(ChangeEntry change)
    {
        var now = _time.GetUtcNow();
        switch (change.Type)
        {
            case ChangeType.ItemAdded:
                DetachItem(change);
                _capturesById[change.CaptureId].WithdrawnAt = now;
                break;
            case ChangeType.StackableAdded:
                DetachStackable(change);
                _capturesById[change.CaptureId].WithdrawnAt = now;
                break;
            case ChangeType.StackableCountUpdated:
                Restore(_stackables[change.IdentityKey], change.Before!);
                _capturesById[change.CaptureId].WithdrawnAt = now;
                break;
            case ChangeType.ItemRemoved:
                SetWithdrawn(AttachItem(change), null);
                break;
            case ChangeType.StackableRemoved:
                AttachStackable(change);
                break;
            case ChangeType.CellCovered:
                ItemByCapture(change.RelatedCaptureId!.Value).CoveredCaptureIds.Remove(change.CaptureId);
                _capturesById[change.CaptureId].WithdrawnAt = now;
                break;
            case ChangeType.ItemSplit:
                DetachItem(change);
                ItemByCapture(change.RelatedCaptureId!.Value).CoveredCaptureIds.Add(change.CaptureId);
                break;
            case ChangeType.CellMarked:
                _capturesById[change.CaptureId].WithdrawnAt = now;
                break;
            case ChangeType.SweepCanceled:
                // Children are most recent first: re-apply them oldest first.
                foreach (var child in change.Children!.Reverse())
                    Reapply(child);
                SweepOf(change).Status = SweepStatus.Finished;
                break;
        }
    }

    private void Reapply(ChangeEntry change)
    {
        var now = _time.GetUtcNow();
        switch (change.Type)
        {
            case ChangeType.ItemAdded:
                AttachItem(change);
                _capturesById[change.CaptureId].WithdrawnAt = null;
                break;
            case ChangeType.StackableAdded:
                AttachStackable(change);
                _capturesById[change.CaptureId].WithdrawnAt = null;
                break;
            case ChangeType.StackableCountUpdated:
                Restore(_stackables[change.IdentityKey], change.After!);
                _capturesById[change.CaptureId].WithdrawnAt = null;
                break;
            case ChangeType.ItemRemoved:
                SetWithdrawn(DetachItem(change), now);
                break;
            case ChangeType.StackableRemoved:
                DetachStackable(change);
                break;
            case ChangeType.CellCovered:
                ItemByCapture(change.RelatedCaptureId!.Value).CoveredCaptureIds.Add(change.CaptureId);
                _capturesById[change.CaptureId].WithdrawnAt = null;
                break;
            case ChangeType.ItemSplit:
                ItemByCapture(change.RelatedCaptureId!.Value).CoveredCaptureIds.Remove(change.CaptureId);
                AttachItem(change);
                break;
            case ChangeType.CellMarked:
                _capturesById[change.CaptureId].WithdrawnAt = null;
                break;
            case ChangeType.SweepCanceled:
                foreach (var child in change.Children!)
                    Revert(child);
                SweepOf(change).Status = SweepStatus.Canceled;
                break;
        }
    }

    private RegularItem DetachItem(ChangeEntry change)
    {
        var item = _items.Single(i => i.CaptureId == change.CaptureId);
        _items.Remove(item);
        _detachedItems[change.Id] = item;
        return item;
    }

    private RegularItem AttachItem(ChangeEntry change)
    {
        var item = _detachedItems[change.Id];
        _items.Add(item);
        _detachedItems.Remove(change.Id);
        return item;
    }

    private void DetachStackable(ChangeEntry change)
    {
        _detachedStackables[change.Id] = _stackables[change.IdentityKey];
        _stackables.Remove(change.IdentityKey);
    }

    private void AttachStackable(ChangeEntry change)
    {
        _stackables[change.IdentityKey] = _detachedStackables[change.Id];
        _detachedStackables.Remove(change.Id);
    }

    /// <summary>An item and the cells it covers are withdrawn and restored together.</summary>
    private void SetWithdrawn(RegularItem item, DateTimeOffset? at)
    {
        _capturesById[item.CaptureId].WithdrawnAt = at;
        foreach (var id in item.CoveredCaptureIds)
            _capturesById[id].WithdrawnAt = at;
    }

    private RegularItem ItemByCapture(Guid captureId) => _items.Single(i => i.CaptureId == captureId);

    private Sweep SweepOf(ChangeEntry change) => _sweeps.Single(s => s.Id == change.SweepId);

    private GridCell? CellOf(ChangeEntry change) =>
        change.Type == ChangeType.SweepCanceled ? null : _capturesById.GetValueOrDefault(change.CaptureId)?.Cell;

    private ChangeEntry Record(ChangeEntry change)
    {
        if (change.SweepId is null && ActiveSweep is { } sweep)
            change = change with { SweepId = sweep.Id };
        _undo.Push(change);
        // The redo stack becomes unreachable; drop anything it was holding on to. (A cancel on the redo stack has
        // its children back on the undo stack, still live: they are not dropped.)
        foreach (var discarded in _redo)
        {
            _detachedItems.Remove(discarded.Id);
            _detachedStackables.Remove(discarded.Id);
        }
        _redo.Clear();
        return change;
    }

    private int? StackCountAfterUndoRedo(ChangeEntry change, bool undone) => change.Type switch
    {
        ChangeType.StackableCountUpdated => (undone ? change.Before : change.After)!.Count,
        ChangeType.StackableAdded when !undone => _stackables[change.IdentityKey].Count,
        ChangeType.StackableRemoved when undone => _stackables[change.IdentityKey].Count,
        _ => null,
    };

    private static StackableSnapshot Snapshot(StackableItem s) => new(s.Count, s.LatestRawText, s.UpdatedAt, s.ParserVersion, s.Stacks.ToList());

    private static void Restore(StackableItem s, StackableSnapshot snap)
    {
        s.Stacks = snap.Stacks?.ToList() ?? throw new InvalidOperationException("Change entry has no stacks to restore.");
        s.LatestRawText = snap.LatestRawText;
        s.UpdatedAt = snap.UpdatedAt;
        s.ParserVersion = snap.ParserVersion;
    }

    private void EnsureActive()
    {
        if (!Session.IsActive)
            throw new InvalidOperationException($"Session '{Session.Name}' is closed and read-only.");
    }
}
