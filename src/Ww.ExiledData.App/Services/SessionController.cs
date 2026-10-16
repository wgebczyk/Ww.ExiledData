using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Parsing;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.App.Services;

public enum FeedbackKind
{
    Added,
    SiblingAdded,
    StackableAdded,
    StackableUpdated,
    AlreadyRecorded,
    /// <summary>A copy that captured nothing: the only outcome shown as a failure.</summary>
    Nothing,
    /// <summary>A hotkey with nothing to act on ("Nothing to undo", "No sweep running"): information, not an error.</summary>
    Info,
    /// <summary>Ctrl+C during a sweep while another window (often this app) was in front: the game never saw it.</summary>
    NotInGame,
    RunStarted,
    RunEnded,
    Undo,
    Redo,
    NewSession,
    /// <summary>Same text next to a captured cell: another cell of that (multi-cell) item.</summary>
    Covered,
    SweepStarted,
    SweepEnded,
    SweepCanceled,
    CellSkipped,
}

/// <param name="OutsideGrid">A sweep is running but the capture was outside its grid (recorded as an ordinary capture).</param>
public sealed record Feedback(FeedbackKind Kind, string Message, int? Count = null, bool OutsideGrid = false);

/// <summary>The running sweep, with what the grid overlay needs to draw.</summary>
public sealed record ActiveSweepInfo(Guid Id, string Label, GridKind Kind, GridGeometry Grid, IReadOnlyList<SweepCell> Cells,
    int ItemCount, int ChangeCount);

public sealed record ActiveSessionInfo(Guid Id, string Name, DateTimeOffset StartedAt, int ItemCount, int StackableCount, bool CanUndo, bool CanRedo,
    ActiveSweepInfo? Sweep = null, SessionKind Kind = SessionKind.FreeRecording);

/// <summary>
/// Owns the store and the recording session, and is the recording coordinator (MAPPING_PLAN.md 6): only one session
/// records at a time, whatever its kind, and every capture goes to it and is then published on
/// <see cref="CaptureProcessed"/>. Every operation runs on the capture queue (see <see cref="CaptureService.Enqueue"/>),
/// so captures, hotkeys and UI actions never race. Without a queue (capture unavailable) operations run under a lock.
/// Events are raised on that queue thread.
/// </summary>
public sealed class SessionController(SqliteStore store, int positionTolerance, TimeProvider? time = null, ILogger<SessionController>? logger = null)
    : ICaptureSink, ICaptureStream
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<SessionController>.Instance;
    private readonly Lock _fallbackGate = new();
    private Func<Action, Task>? _enqueue;
    private SessionTracker? _tracker;
    private int _tolerance = positionTolerance;

    public event Action<Feedback>? FeedbackRaised;
    /// <summary>Raised after anything stored changed; carries the active session (null when none).</summary>
    public event Action<ActiveSessionInfo?>? StateChanged;
    /// <summary>
    /// Sweep start/finish hotkeys. They depend on the grid editor (UI), so the UI decides what they do and calls
    /// back into <see cref="StartSweep"/> / <see cref="FinishSweep"/>.
    /// </summary>
    public event Action<HotkeyPress>? SweepHotkeyPressed;

    /// <summary>A mapping hotkey (scans, runs); the host acts on the recording session when it is a mapping session.</summary>
    public event Action<HotkeyPress>? MappingHotkeyPressed;
    /// <summary>The seam: every copy of item text, after it is stored in the recording session.</summary>
    public event Action<ProcessedCapture>? CaptureProcessed;
    /// <summary>The seam: sweeps of the recording session starting and ending.</summary>
    public event Action<SweepChanged>? SweepChanged;

    /// <summary>Routes all operations through the capture queue; null runs them inline under a lock.</summary>
    public void UseQueue(Func<Action, Task>? enqueue) => _enqueue = enqueue;

    // --- capture sink (already on the queue thread) ---------------------------------------------

    public void OnItemCopied(CopiedItem item)
    {
        var tracker = EnsureTracker();
        _log.LogDebug("Item text (clipboard after {Delay:F1} ms, cursor {Position}):\n{Text}",
            item.ClipboardDelay.TotalMilliseconds, item.Position, item.Text);

        var result = tracker.Apply(item.Text, item.Position);
        foreach (var warning in result.Warnings ?? [])
            _log.LogWarning("{Warning}", warning);

        var label = ItemLabel.From(item.Text);
        var where = result.Cell is { } cell ? $"cell {cell}" : result.OutsideGrid ? "outside the grid" : "";
        _log.LogInformation("Capture {Outcome}: {Label}{Count} at {Position} {Where}", result.Outcome, label,
            result.StackCount is { } c ? $" x{c}" : "", item.Position, where);

        store.SaveTracker(tracker);
        Publish(tracker, item, result);
        var outside = result.OutsideGrid ? " (outside the grid)" : "";
        Raise(result.Outcome switch
        {
            CaptureOutcome.Added => new Feedback(FeedbackKind.Added, $"Added {label}{outside}"),
            CaptureOutcome.SiblingAdded => new Feedback(FeedbackKind.SiblingAdded, $"Added {label} (same text elsewhere; possible duplicate){outside}"),
            CaptureOutcome.Covered => new Feedback(FeedbackKind.Covered, $"{label}: next cell of the same item? (possible duplicate; split it in the Items tab)"),
            CaptureOutcome.StackableAdded => new Feedback(FeedbackKind.StackableAdded, $"{label}: {result.StackCount}{outside}", result.StackCount),
            CaptureOutcome.StackableUpdated => new Feedback(FeedbackKind.StackableUpdated, $"{label}: now {result.StackCount}{outside}", result.StackCount),
            CaptureOutcome.AlreadyRecorded => new Feedback(FeedbackKind.AlreadyRecorded, $"{label} already recorded{outside}", result.StackCount),
            _ => new Feedback(FeedbackKind.Nothing, "Nothing captured"),
        } with { OutsideGrid = result.OutsideGrid });
        RaiseState();
    }

    public void OnCopyIgnored(CopyPress press, CopyIgnoreReason reason)
    {
        if (reason == CopyIgnoreReason.NotGameWindow)
        {
            // Outside a sweep, Ctrl+C in another app is none of our business; during one it is a forgotten click into
            // the game (after clicking Swipe in the app, say), so say so instead of staying silent.
            if (_tracker?.ActiveSweep is not null)
                Raise(new Feedback(FeedbackKind.NotInGame, "Ctrl+C went to another window: click into the game first"));
            return;
        }

        if (_tracker?.MarkFailed(press.Position) is { } cell)
        {
            _log.LogInformation("Sweep cell {Cell} marked failed ({Reason})", cell, reason);
            store.SaveTracker(_tracker);
            RaiseState();
        }
        Raise(new Feedback(FeedbackKind.Nothing, reason switch
        {
            CopyIgnoreReason.ClipboardTimeout => "Nothing captured (clipboard did not change in time)",
            CopyIgnoreReason.ClipboardUnreadable => "Nothing captured (clipboard was locked)",
            _ => "Nothing captured (not an item)",
        }));
    }

    public void OnHotkey(HotkeyPress press)
    {
        switch (press.Action)
        {
            case HotkeyAction.NewSession:
                StartNewSessionCore();
                break;
            case HotkeyAction.Undo:
                UndoRedoCore(undo: true);
                break;
            case HotkeyAction.Redo:
                UndoRedoCore(undo: false);
                break;
            case HotkeyAction.SkipCell:
                ToggleSkipCore(press.Cursor);
                break;
            case HotkeyAction.SweepStart or HotkeyAction.SweepFinish:
                SweepHotkeyPressed?.Invoke(press);
                break;
            case HotkeyAction.MapInitialScan or HotkeyAction.MapStashScan or HotkeyAction.MapSwipe or HotkeyAction.MapFinalScan or HotkeyAction.MapStartRun or HotkeyAction.MapEndRun:
                MappingHotkeyPressed?.Invoke(press);
                break;
        }
    }

    // --- operations for the UI (queued) ---------------------------------------------------------

    /// <summary>Launch-time recovery of sessions (and sweeps) left active by a previous run.</summary>
    public Task<IReadOnlyList<SessionSummary>> RecoverInterrupted() => Run(() =>
    {
        var recovered = store.RecoverInterrupted();
        foreach (var s in recovered)
            _log.LogWarning("Session '{Name}' ({Id}) was left open; closed at {End:O} and marked interrupted",
                s.Session.Name, s.Session.Id, s.Session.EndedAt);
        return recovered;
    });

    /// <summary>
    /// Starts a new recording session, ending the current one; returns its id. Without a name it is named after its
    /// start time; without a kind it keeps the kind of the session it replaces.
    /// </summary>
    public Task<Guid> StartNewSession(string? name = null, SessionKind? kind = null) =>
        Run(() => StartSession(announce: true, name, kind).Session.Id);

    /// <summary>
    /// Makes an ended session the recording session again (Continue; "Make this the recording session"), ending the
    /// current one. Its undo history from before is archived: undo starts fresh.
    /// </summary>
    public Task ContinueSession(Guid sessionId) => Run(() =>
    {
        if (_tracker?.Session.Id == sessionId)
            return;
        if (store.GetSession(sessionId) is null)
            throw new InvalidOperationException("Session no longer exists.");

        EndRecording();
        _tracker = store.ContinueSession(sessionId, _tolerance, _time)!;
        _log.LogInformation("Continued session '{Name}' ({Id}, {Kind})", _tracker.Session.Name, sessionId, _tracker.Session.Kind);
        Raise(new Feedback(FeedbackKind.NewSession, $"Recording: {_tracker.Session.Name}"));
        RaiseState();
    });

    public Task Undo() => Run(() => UndoRedoCore(undo: true));
    public Task Redo() => Run(() => UndoRedoCore(undo: false));

    public Task SetPositionTolerance(int tolerance) => Run(() =>
    {
        _tolerance = tolerance;
        _tracker?.PositionTolerance = tolerance;
    });

    public Task<ActiveSessionInfo?> GetActive() => Run(ActiveInfo);

    public Task<IReadOnlyList<SessionSummary>> ListSessions(DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        Run(() => store.ListSessions(from, to));

    public Task<SessionData?> LoadSession(Guid id) => Run(() => store.LoadSession(id));

    public Task RemoveItem(Guid sessionId, Guid captureId) => Run(() =>
    {
        if (RequireActive(sessionId).RemoveItem(captureId) is { } r)
            AfterManualChange("Removed item", r);
    });

    public Task RemoveStackable(Guid sessionId, string key) => Run(() =>
    {
        if (RequireActive(sessionId).RemoveStackable(key) is { } r)
            AfterManualChange("Removed stackable", r);
    });

    /// <summary>Splits the last covered cell of an item into an item of its own.</summary>
    public Task<bool> SplitCovered(Guid sessionId, Guid itemCaptureId) => Run(() =>
    {
        if (RequireActive(sessionId).SplitCovered(itemCaptureId) is not { } r)
            return false;
        AfterManualChange("Split covered cell", r);
        return true;
    });

    /// <summary>Starts a sweep (a session starts if none is active); a running sweep is finished first.</summary>
    public Task<ActiveSweepInfo> StartSweep(GridKind kind, GridGeometry grid) => Run(() =>
    {
        var tracker = EnsureTracker();
        FinishSweepCore();
        var sweep = tracker.StartSweep(kind, grid);
        store.SaveTracker(tracker);
        PublishSweep(tracker.Session, sweep);
        _log.LogInformation("Started sweep '{Label}' ({Id}): {Kind} at ({X:F0}, {Y:F0}), cell {Cell:F1} px",
            sweep.Label, sweep.Id, kind, grid.X, grid.Y, grid.CellSize);
        Raise(new Feedback(FeedbackKind.SweepStarted, $"Sweep started: {sweep.Label}"));
        RaiseState();
        return SweepInfo(tracker)!;
    });

    /// <summary>Moves the running sweep's grid while it is still empty; false when there is none or it has cells.</summary>
    public Task<bool> RepositionSweep(GridGeometry grid) => Run(() =>
    {
        if (_tracker?.ActiveSweep is not { } sweep || !_tracker.RepositionSweep(grid))
            return false;
        store.SaveTracker(_tracker);
        _log.LogInformation("Repositioned sweep '{Label}' ({Id}) to ({X:F0}, {Y:F0}), cell {Cell:F1} px",
            sweep.Label, sweep.Id, grid.X, grid.Y, grid.CellSize);
        RaiseState();
        return true;
    });

    /// <summary>Finishes the running sweep keeping its captures; returns its item count, or null when none ran.</summary>
    public Task<int?> FinishSweep() => Run(FinishSweepCore);

    /// <summary>
    /// Cancels the running sweep, withdrawing every change made during it as one undoable entry. Returns how many
    /// changes were withdrawn, or null when no sweep was running.
    /// </summary>
    public Task<int?> CancelSweep() => Run(() =>
    {
        if (_tracker?.CancelSweep() is not { } canceled)
            return (int?)null;
        var (sweep, change) = canceled;
        store.SaveTracker(_tracker);
        PublishSweep(_tracker.Session, sweep);
        var count = change?.Children?.Count ?? 0;
        _log.LogInformation("Canceled sweep '{Label}' ({Id}); withdrew {Count} change(s)", sweep.Label, sweep.Id, count);
        Raise(new Feedback(FeedbackKind.SweepCanceled, $"Sweep '{sweep.Label}' canceled; withdrew {count} change(s) (undo restores them)"));
        RaiseState();
        return count;
    });

    public Task SaveCalibration(GridCalibration calibration) => Run(() => store.SaveCalibration(calibration));

    public Task<GridCalibration?> FindCalibration(GridKind kind, int screenWidth, int screenHeight) =>
        Run(() => store.FindCalibration(kind, screenWidth, screenHeight));

    public Task Rename(Guid sessionId, string name) => Run(() =>
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("Session name cannot be empty.");
        var session = _tracker?.Session.Id == sessionId ? _tracker.Session : store.GetSession(sessionId)
            ?? throw new InvalidOperationException("Session no longer exists.");
        _log.LogInformation("Rename session {Id}: '{Old}' -> '{New}'", sessionId, session.Name, name);
        session.Name = name;
        store.SaveSession(session);
        RaiseState();
    });

    /// <summary>Ends the recording session (finishing its sweep); the next capture starts a new one.</summary>
    public Task EndSession() => Run(() =>
    {
        if (EndRecording())
            RaiseState();
    });

    public Task DeleteSession(Guid sessionId) => Run(() =>
    {
        if (_tracker?.Session.Id == sessionId)
            _tracker = null;
        store.DeleteSession(sessionId);
        _log.LogInformation("Deleted session {Id}", sessionId);
        RaiseState();
    });

    /// <summary>Deletes closed sessions with no captures; returns how many.</summary>
    public Task<int> DeleteEmptySessions() => Run(() =>
    {
        var empty = store.ListSessions().Where(s => s.IsEmpty && s.Session.Id != _tracker?.Session.Id).ToList();
        foreach (var s in empty)
            store.DeleteSession(s.Session.Id);
        _log.LogInformation("Deleted {Count} empty sessions", empty.Count);
        RaiseState();
        return empty.Count;
    });

    // --- internals ------------------------------------------------------------------------------

    /// <summary>Publishes a stored copy of item text on the seam. A failing subscriber never fails the capture.</summary>
    private void Publish(SessionTracker tracker, CopiedItem item, CaptureResult result)
    {
        if (result.Outcome == CaptureOutcome.NotAnItem || CaptureProcessed is not { } handlers)
            return;

        var logged = result.Change is { } change ? tracker.Log.FirstOrDefault(c => c.Id == change.CaptureId) : null;
        var text = logged?.RawText ?? ItemText.Normalize(item.Text);
        var parse = StackableParser.Parse(text);
        var stack = parse.Status == StackParseStatus.Parsed
            ? new CapturedStack(IdentityKeys.Stackable(parse.ItemClass!, parse.Rarity!, parse.Name!, parse.MaxStack),
                parse.ItemClass!, parse.Rarity!, parse.Name!, parse.Count, parse.MaxStack)
            : null;
        var capture = new ProcessedCapture(tracker.Session.Id, tracker.Session.Kind, logged?.Id, text, item.Position,
            logged?.CapturedAt ?? _time.GetUtcNow(), stack, result.Cell is null ? null : tracker.ActiveSweep?.Id, result.Cell);

        foreach (var handler in handlers.GetInvocationList().Cast<Action<ProcessedCapture>>())
        {
            try
            {
                handler(capture);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A capture subscriber failed on capture {Id}", capture.CaptureId);
            }
        }
    }

    /// <summary>Publishes a sweep's start or end on the seam. A failing subscriber never fails the sweep.</summary>
    private void PublishSweep(Session session, Sweep sweep)
    {
        if (SweepChanged is not { } handlers)
            return;
        var change = new SweepChanged(session.Id, session.Kind, sweep.Id, sweep.Label, sweep.Status, sweep.EndedAt ?? sweep.StartedAt);
        foreach (var handler in handlers.GetInvocationList().Cast<Action<SweepChanged>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A sweep subscriber failed on sweep {Id} ({Status})", sweep.Id, sweep.Status);
            }
        }
    }

    private void StartNewSessionCore() => StartSession(announce: true);

    /// <summary>A capture with no recording session starts a free recording one.</summary>
    private SessionTracker EnsureTracker() => _tracker ?? StartSession(announce: false);

    private SessionTracker StartSession(bool announce, string? name = null, SessionKind? kind = null)
    {
        FinishSweepCore(); // a sweep belongs to its session
        var previous = _tracker?.Session;
        var session = SessionLifecycle.Start(previous, _time.GetUtcNow(), name, kind ?? previous?.Kind ?? SessionKind.FreeRecording);
        if (previous is not null)
        {
            store.SaveSession(previous);
            _log.LogInformation("Closed session '{Name}' ({Id})", previous.Name, previous.Id);
        }
        store.SaveSession(session);
        _tracker = new SessionTracker(session, _tolerance, _time);
        _log.LogInformation("Started {Kind} session '{Name}' ({Id}){Auto}", session.Kind, session.Name, session.Id,
            announce ? "" : " automatically");

        if (announce)
        {
            Raise(new Feedback(FeedbackKind.NewSession, $"New session: {session.Name}"));
            RaiseState();
        }
        return _tracker;
    }

    /// <summary>Ends the recording session (finishing its sweep); false when none records.</summary>
    private bool EndRecording()
    {
        if (_tracker is not { } tracker)
            return false;
        FinishSweepCore();
        SessionLifecycle.End(tracker.Session, _time.GetUtcNow());
        store.SaveSession(tracker.Session);
        _tracker = null;
        _log.LogInformation("Ended session '{Name}' ({Id})", tracker.Session.Name, tracker.Session.Id);
        return true;
    }

    private int? FinishSweepCore()
    {
        if (_tracker?.ActiveSweep is not { } running)
            return null;
        var count = _tracker.ItemCountOf(running.Id);
        _tracker.FinishSweep();
        store.SaveTracker(_tracker);
        PublishSweep(_tracker.Session, running);
        _log.LogInformation("Finished sweep '{Label}' ({Id}): {Count} item(s)", running.Label, running.Id, count);
        Raise(new Feedback(FeedbackKind.SweepEnded, $"Sweep '{running.Label}' finished: {count} item(s)", count));
        RaiseState();
        return count;
    }

    private void ToggleSkipCore(ScreenPoint cursor)
    {
        if (_tracker?.ToggleSkip(cursor) is not { } toggled)
        {
            Raise(new Feedback(FeedbackKind.Info, _tracker?.ActiveSweep is null
                ? "No sweep running"
                : "Nothing to skip there (outside the grid or already captured)"));
            return;
        }
        var (cell, state) = toggled;
        store.SaveTracker(_tracker);
        _log.LogInformation("Sweep cell {Cell} {State}", cell, state == CellState.Skipped ? "skipped" : "unskipped");
        Raise(new Feedback(FeedbackKind.CellSkipped, state == CellState.Skipped ? $"Cell {cell} skipped" : $"Cell {cell} no longer skipped"));
        RaiseState();
    }

    private void UndoRedoCore(bool undo)
    {
        var result = _tracker is not { } t ? null : undo ? (t.CanUndo ? t.Undo() : null) : (t.CanRedo ? t.Redo() : null);
        if (result?.Change is not { } change)
        {
            Raise(new Feedback(FeedbackKind.Info, undo ? "Nothing to undo" : "Nothing to redo"));
            return;
        }

        store.SaveTracker(_tracker!);
        var what = $"{change.Type} {Describe(change)}";
        _log.LogInformation("{Action}: {What}", undo ? "Undo" : "Redo", what);
        Raise(new Feedback(undo ? FeedbackKind.Undo : FeedbackKind.Redo, $"{(undo ? "Undid" : "Redid")} {what}", result.StackCount));
        RaiseState();
    }

    private void AfterManualChange(string what, CaptureResult result)
    {
        store.SaveTracker(_tracker!);
        _log.LogInformation("{What}: {Detail}", what, Describe(result.Change!));
        RaiseState();
    }

    private string Describe(ChangeEntry change)
    {
        if (change.Type == ChangeType.SweepCanceled)
            return $"({change.Children?.Count ?? 0} changes)";
        var capture = _tracker!.Log.FirstOrDefault(c => c.Id == change.CaptureId);
        return capture is null ? change.IdentityKey : ItemLabel.From(capture.RawText);
    }

    private SessionTracker RequireActive(Guid sessionId) =>
        _tracker is { } t && t.Session.Id == sessionId
            ? t
            : throw new InvalidOperationException("Only the recording session can be changed; continue an ended session to change it.");

    private ActiveSessionInfo? ActiveInfo() => _tracker is not { } t
        ? null
        : new ActiveSessionInfo(t.Session.Id, t.Session.Name, t.Session.StartedAt, t.Items.Count, t.Stackables.Count, t.CanUndo, t.CanRedo,
            SweepInfo(t), t.Session.Kind);

    private static ActiveSweepInfo? SweepInfo(SessionTracker t) => t.ActiveSweep is not { } s
        ? null
        : new ActiveSweepInfo(s.Id, s.Label, s.Kind, s.Grid, t.CellsOf(s.Id), t.ItemCountOf(s.Id), t.ChangesInActiveSweep);

    private void Raise(Feedback feedback) => FeedbackRaised?.Invoke(feedback);
    private void RaiseState() => StateChanged?.Invoke(ActiveInfo());

    private Task Run(Action work) => Run(() => { work(); return true; });

    private async Task<T> Run<T>(Func<T> work)
    {
        if (_enqueue is { } enqueue)
        {
            T result = default!;
            await enqueue(() => result = work()).ConfigureAwait(false);
            return result;
        }

        lock (_fallbackGate)
            return work();
    }
}
