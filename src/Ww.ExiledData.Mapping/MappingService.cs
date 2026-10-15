using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sessions;

namespace Ww.ExiledData.Mapping;

/// <summary>
/// The mapping module's entry point. Subscribes to the capture stream (the seam) and records the captures and sweeps
/// of mapping sessions into runs and scans; the recording coordinator decides which session that is. Also takes the
/// user's edits (scan kinds, runs, setup, adjustments, tags, notes) with undo/redo per session.
/// <para>
/// Seam events arrive on the capture queue thread and are handled there. Every other operation is queued the same
/// way (<see cref="UseQueue"/>), so captures and edits never race. Reads return copies loaded from the store.
/// </para>
/// </summary>
public sealed class MappingService
{
    private readonly MappingStore _store;
    private readonly MappingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _fallbackGate = new();
    // Loaded sessions with their undo history (kept for the app's lifetime; one session is small).
    private readonly Dictionary<Guid, MapSession> _sessions = [];
    private Func<Action, Task>? _enqueue;
    private ScanKind? _nextScanKind;

    public MappingService(MappingStore store, ICaptureStream stream, MappingOptions? options = null, TimeProvider? time = null,
        ILogger<MappingService>? logger = null)
    {
        _store = store;
        _options = options ?? new MappingOptions();
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<MappingService>.Instance;
        stream.CaptureProcessed += OnCapture;
        stream.SweepChanged += OnSweep;
    }

    /// <summary>Raised (on the queue thread) after a session's mapping data changed; carries its id.</summary>
    public event Action<Guid>? Changed;

    /// <summary>Routes operations through the capture queue; null runs them inline under a lock.</summary>
    public void UseQueue(Func<Action, Task>? enqueue) => _enqueue = enqueue;

    // --- the seam (already on the queue thread) ----------------------------------------------------

    private void OnCapture(ProcessedCapture capture)
    {
        if (capture.SessionKind != SessionKind.Mapping)
            return;
        var session = Get(capture.SessionId);
        var outcome = session.Apply(capture, _options);
        _log.LogInformation("Mapping {Outcome}: run {Run}, capture {Capture}{Cell}", outcome, session.CurrentRun?.Number,
            capture.CaptureId, capture.Cell is { } cell ? $" in cell {cell}" : "");
        SaveAndRaise(session);
    }

    private void OnSweep(SweepChanged sweep)
    {
        if (sweep.SessionKind != SessionKind.Mapping)
            return;
        var session = Get(sweep.SessionId);
        var kind = sweep.Status == Core.Sweeps.SweepStatus.Active ? _nextScanKind : null;
        if (sweep.Status == Core.Sweeps.SweepStatus.Active)
            _nextScanKind = null;
        if (session.Apply(sweep, kind) is not { } scan)
            return;
        _log.LogInformation("Mapping scan {Id} {Status}: run {Run}, {Name}", scan.Id, scan.Status,
            session.FindRun(scan.RunId)?.Number, session.NameOf(scan));
        SaveAndRaise(session);
    }

    // --- operations (queued) -----------------------------------------------------------------------

    /// <summary>Launch-time recovery: scans left active by a previous run are closed as interrupted.</summary>
    public Task<int> RecoverInterrupted() => Run(() =>
    {
        var ids = _store.SessionsWithActiveScans();
        foreach (var id in ids)
        {
            var session = Get(id);
            session.CloseInterruptedScans();
            _store.Save(session);
            _log.LogWarning("Mapping session {Id}: a scan was left active; closed as interrupted", id);
        }
        return ids.Count;
    });

    /// <summary>A copy of the session's mapping data (empty when it has none).</summary>
    public Task<MapSession> Load(Guid sessionId) => Run(() => _store.Load(sessionId));

    public Task<IReadOnlyDictionary<Guid, MappingSummary>> ListSummaries() => Run(_store.ListSummaries);

    public Task<IReadOnlyList<MapTag>> KnownTags() => Run(_store.KnownTags);

    /// <summary>
    /// The kind the next scan (sweep) of the recording session starts with: the Swipe, Stash scan and Final scan
    /// actions set it right before starting the sweep. A sweep started any other way is a swipe.
    /// </summary>
    public Task SetNextScanKind(ScanKind? kind) => Run(() => _nextScanKind = kind);

    public Task<bool> SetScanKind(Guid sessionId, Guid scanId, ScanKind kind) => Edit(sessionId, s => s.SetScanKind(scanId, kind));

    public Task<bool> RemoveScan(Guid sessionId, Guid scanId) => Edit(sessionId, s => s.RemoveScan(scanId));

    /// <summary>Starts a run by hand (it ends the open one). Not undoable, like a run started by a waystone copy.</summary>
    public Task<MapRun> StartNewRun(Guid sessionId) => Edit(sessionId, s => s.StartNewRun(_time.GetUtcNow()));

    public Task<bool> AssignSetup(Guid sessionId, Guid runId, RecallItem item) => Edit(sessionId, s => s.AssignSetup(runId, item));

    public Task<bool> SetSetup(Guid sessionId, Guid runId, RecallItem? waystone, IReadOnlyList<RecallItem> tablets) =>
        Edit(sessionId, s => s.SetSetup(runId, waystone, tablets));

    public Task<bool> UsePreviousTablets(Guid sessionId, Guid runId) => Edit(sessionId, s => s.UsePreviousTablets(runId));

    public Task<bool> RemoveSetupItem(Guid sessionId, Guid runId, Guid itemId) => Edit(sessionId, s => s.RemoveSetupItem(runId, itemId));

    public Task<bool> ApplyAdjustments(Guid sessionId, Guid finalScanId, IReadOnlyList<(string Key, string Name, int Amount)> adjustments) =>
        Edit(sessionId, s => s.ApplyAdjustments(finalScanId, adjustments, _time.GetUtcNow()));

    public Task<bool> RemoveAdjustment(Guid sessionId, Guid runId, Guid adjustmentId) => Edit(sessionId, s => s.RemoveAdjustment(runId, adjustmentId));

    public Task<bool> DismissCheck(Guid sessionId, Guid finalScanId, bool dismissed) => Edit(sessionId, s => s.DismissCheck(finalScanId, dismissed));

    public Task<bool> EndRun(Guid sessionId) => Edit(sessionId, s => s.EndRun(_time.GetUtcNow()));

    public Task<MapTag?> AddTag(Guid sessionId, Guid ownerId, string text) => Edit(sessionId, s => s.AddTag(ownerId, text));

    public Task<bool> RemoveTag(Guid sessionId, Guid ownerId, MapTag tag) => Edit(sessionId, s => s.RemoveTag(ownerId, tag));

    public Task<bool> SetNote(Guid sessionId, Guid ownerId, string? text) => Edit(sessionId, s => s.SetNote(ownerId, text));

    public Task<bool> SetRunExcluded(Guid sessionId, Guid runId, bool excluded) => Edit(sessionId, s => s.SetRunExcluded(runId, excluded));

    /// <summary>Deletes a run (not undoable; the caller asks first).</summary>
    public Task<bool> DeleteRun(Guid sessionId, Guid runId) => Edit(sessionId, s => s.DeleteRun(runId));

    public Task<bool> SetSetupItemKind(Guid sessionId, Guid itemId, SetupItemKind kind) => Edit(sessionId, s => s.SetSetupItemKind(itemId, kind));

    public Task<MappingEdit?> Undo(Guid sessionId) => Edit(sessionId, s => s.Undo());

    public Task<MappingEdit?> Redo(Guid sessionId) => Edit(sessionId, s => s.Redo());

    public Task<(bool CanUndo, bool CanRedo)> UndoState(Guid sessionId) => Run(() => (Get(sessionId).CanUndo, Get(sessionId).CanRedo));

    // --- internals ---------------------------------------------------------------------------------

    private MapSession Get(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            _sessions[sessionId] = session = _store.Load(sessionId);
        return session;
    }

    /// <summary>Applies an edit; saves and raises <see cref="Changed"/> unless it returned false or null (no change).</summary>
    private Task<T> Edit<T>(Guid sessionId, Func<MapSession, T> edit) => Run(() =>
    {
        var session = Get(sessionId);
        var result = edit(session);
        if (result is not (null or false))
        {
            _log.LogInformation("Mapping edit on session {Id}: {Result}", sessionId, result);
            SaveAndRaise(session);
        }
        return result;
    });

    private void SaveAndRaise(MapSession session)
    {
        _store.Save(session);
        Changed?.Invoke(session.SessionId);
    }

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
