using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

/// <summary>Session kinds and continuing an ended session (MAPPING_PLAN.md M0), at the lifecycle and store level.</summary>
public sealed class SessionContinueTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScreenPoint A = new(500, 300);
    private static readonly ScreenPoint B = new(560, 300);
    private static readonly GridKind StashKind = new("Stash", 12, 12);
    private static readonly GridGeometry Stash = new(100, 200, 50, 12, 12);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ww-exileddata-{Guid.NewGuid():N}.db");
    private readonly FakeTime _time = new(T0);
    private SqliteStore _store;

    public SessionContinueTests() => _store = SqliteStore.Open(_path);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_path + suffix);
    }

    private void Reopen()
    {
        _store.Dispose();
        _store = SqliteStore.Open(_path);
    }

    private static ScreenPoint At(int column, int row) => new(125 + column * 50, 225 + row * 50);

    private SessionTracker NewTracker(SessionKind kind = SessionKind.FreeRecording)
    {
        var tracker = new SessionTracker(SessionLifecycle.Start(null, _time.Now, kind: kind), 8, _time);
        _store.SaveSession(tracker.Session);
        return tracker;
    }

    private CaptureResult Capture(SessionTracker tracker, string text, ScreenPoint at)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = tracker.Apply(text, at);
        _store.SaveTracker(tracker);
        return result;
    }

    private void End(SessionTracker tracker)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        SessionLifecycle.End(tracker.Session, _time.Now);
        _store.SaveSession(tracker.Session);
    }

    [Fact]
    public void Start_takes_a_trimmed_name_and_a_kind()
    {
        var named = SessionLifecycle.Start(null, T0, "  farming rituals 4 ", SessionKind.Mapping);
        var unnamed = SessionLifecycle.Start(null, T0, "   ");

        Assert.Equal(("farming rituals 4", SessionKind.Mapping), (named.Name, named.Kind));
        Assert.Equal((SessionLifecycle.DefaultName(T0), SessionKind.FreeRecording), (unnamed.Name, unnamed.Kind));
    }

    [Fact]
    public void Continue_reactivates_an_ended_session_and_clears_interrupted()
    {
        var s = SessionLifecycle.Start(null, T0);
        SessionLifecycle.CloseInterrupted(s, T0.AddMinutes(3));

        SessionLifecycle.Continue(s);

        Assert.Equal((true, false, T0), (s.IsActive, s.Interrupted, s.StartedAt));
        Assert.Throws<InvalidOperationException>(() => SessionLifecycle.Continue(s));
    }

    [Fact]
    public void Kind_round_trips_and_is_kept_on_later_saves()
    {
        var mapping = NewTracker(SessionKind.Mapping);
        Capture(mapping, Fixture.Load(Fixture.Waystone), A);
        mapping.Session.Name = "renamed";
        _store.SaveSession(mapping.Session);

        Reopen();

        Assert.Equal(SessionKind.Mapping, _store.GetSession(mapping.Session.Id)!.Kind);
        Assert.Equal(SessionKind.Mapping, Assert.Single(_store.ListSessions()).Session.Kind);
        Assert.Equal(SessionKind.Mapping, _store.LoadSession(mapping.Session.Id)!.Session.Kind);
    }

    [Fact]
    public void Continued_tracker_keeps_state_and_identity_but_starts_a_fresh_undo_history()
    {
        var t = NewTracker(SessionKind.Mapping);
        Capture(t, Fixture.Load(Fixture.Waystone), A);
        Capture(t, Fixture.Load(Fixture.Verisium), B);
        End(t);
        Reopen();

        var continued = _store.ContinueSession(t.Session.Id, 8, _time)!;

        Assert.True(continued.Session.IsActive);
        Assert.Equal(SessionKind.Mapping, continued.Session.Kind);
        Assert.Equal(t.Log.Select(c => c.Id), continued.Log.Select(c => c.Id));
        Assert.Equal(t.Items.Select(i => i.CaptureId), continued.Items.Select(i => i.CaptureId));
        Assert.Equal(31, Assert.Single(continued.Stackables).Count);
        Assert.False(continued.CanUndo);
        Assert.True(_store.GetSession(t.Session.Id)!.IsActive);

        // Same item at the same place is recognized; a new count updates the stackable.
        Assert.Equal(CaptureOutcome.AlreadyRecorded, Capture(continued, Fixture.Load(Fixture.Waystone), A).Outcome);
        Assert.Equal(CaptureOutcome.StackableUpdated,
            Capture(continued, Fixture.Load(Fixture.Verisium).Replace("31/1,000", "40/1,000"), B).Outcome);
        Assert.True(continued.CanUndo);
    }

    [Fact]
    public void Earlier_history_is_archived_and_new_entries_follow_it()
    {
        var t = NewTracker();
        Capture(t, Fixture.Load(Fixture.Waystone), A);
        Capture(t, Fixture.Load(Fixture.BodyArmourRare), B);
        t.Undo(); // on the redo stack when the session ends
        _store.SaveTracker(t);
        End(t);

        var continued = _store.ContinueSession(t.Session.Id, 8, _time)!;
        Capture(continued, Fixture.Load(Fixture.ChanceShard), B);
        continued.Undo();
        continued.Redo();
        _store.SaveTracker(continued);

        var changes = _store.LoadSession(t.Session.Id)!.Changes;
        Assert.Equal([true, true, false], changes.Select(c => c.Archived));
        Assert.Equal([false, true, false], changes.Select(c => c.Undone));
        Assert.Equal([ChangeType.ItemAdded, ChangeType.ItemAdded, ChangeType.StackableAdded], changes.Select(c => c.Change.Type));
        // The undone armour stays withdrawn: its redo is gone with the old history.
        Assert.Single(_store.LoadSession(t.Session.Id)!.Items);
    }

    [Fact]
    public void Continue_restores_sweep_marks_and_closes_a_sweep_left_running()
    {
        var t = NewTracker();
        var sweep = t.StartSweep(StashKind, Stash);
        Capture(t, Fixture.Load(Fixture.ChanceShard), At(0, 0));
        t.ToggleSkip(At(1, 0));
        t.MarkFailed(At(2, 0));
        _store.SaveTracker(t);
        End(t);

        var continued = _store.ContinueSession(t.Session.Id, 8, _time)!;

        Assert.Null(continued.ActiveSweep);
        Assert.Equal(SweepStatus.Interrupted, Assert.Single(continued.Sweeps).Status);
        Assert.Equal([CellState.Captured, CellState.Skipped, CellState.Failed], continued.CellsOf(sweep.Id).Select(c => c.State));
        Assert.Equal(SweepStatus.Interrupted, Assert.Single(_store.LoadSession(t.Session.Id)!.Sweeps).Sweep.Status);
    }

    [Fact]
    public void Continue_of_a_missing_session_returns_null()
    {
        Assert.Null(_store.ContinueSession(Guid.NewGuid(), 8, _time));
    }
}
