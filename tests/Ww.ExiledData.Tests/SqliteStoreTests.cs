using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private static readonly ScreenPoint A = new(500, 300);
    private static readonly ScreenPoint B = new(560, 300);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ww-exileddata-{Guid.NewGuid():N}.db");
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567));
    private SqliteStore _store;

    public SqliteStoreTests() => _store = SqliteStore.Open(_path);

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

    private SessionTracker NewTracker(Session? previous = null)
    {
        var tracker = new SessionTracker(SessionLifecycle.Start(previous, _time.Now), 8, _time);
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

    [Fact]
    public void Creates_schema_and_reopens_idempotently()
    {
        Assert.Equal(4, _store.SchemaVersion);
        Reopen();
        Assert.Equal(4, _store.SchemaVersion);
        Assert.Empty(_store.ListSessions());
    }

    [Fact]
    public void Round_trips_full_session_state()
    {
        var t = NewTracker();
        Capture(t, Fixture.LoadCrLf(Fixture.BodyArmourRare), A);
        Capture(t, Fixture.Load(Fixture.Waystone), A);
        Capture(t, Fixture.Load(Fixture.Waystone), B);
        Capture(t, Fixture.Load(Fixture.Verisium), A);
        Capture(t, Fixture.Load(Fixture.Verisium).Replace("31/1,000", "40/1,000"), B);

        Reopen();
        var data = _store.LoadSession(t.Session.Id)!;

        Assert.Equal(t.Session.StartedAt, data.Session.StartedAt);
        Assert.True(data.Session.IsActive);
        Assert.Equal(t.Log.Select(c => (c.Id, c.RawText, c.Position, c.CapturedAt, c.Kind, c.IdentityKey)),
                     data.Captures.Select(c => (c.Id, c.RawText, c.Position, c.CapturedAt, c.Kind, c.IdentityKey)));
        Assert.Equal(t.Items.Select(i => (i.CaptureId, i.IdentityKey, i.Label, i.Position, i.LastSeenAt, i.PossibleDuplicate)),
                     data.Items.Select(i => (i.CaptureId, i.IdentityKey, i.Label, i.Position, i.LastSeenAt, i.PossibleDuplicate)));
        var s = Assert.Single(data.Stackables);
        Assert.Equal(("Verisium", 71, 1000, 1), (s.Name, s.Count, s.MaxStack, s.ParserVersion)); // stacks at A and B
        Assert.Equal(t.Stackables.Single().Stacks, s.Stacks);
        Assert.Equal(t.History.Reverse(), data.Changes.Select(c => c.Change));
        Assert.All(data.Changes, c => Assert.False(c.Undone));
    }

    [Fact]
    public void Persists_undo_as_soft_delete_and_redo_stack()
    {
        var t = NewTracker();
        Capture(t, Fixture.Load(Fixture.Transmutation), A);
        Capture(t, Fixture.Load(Fixture.Transmutation).Replace("2/40", "9/40"), A);
        Capture(t, Fixture.Load(Fixture.Waystone), B);
        t.Undo();
        t.Undo();
        _store.SaveTracker(t);

        var data = _store.LoadSession(t.Session.Id)!;

        Assert.Equal(3, data.Captures.Count);
        Assert.Equal([false, true, true], data.Captures.Select(c => c.IsWithdrawn));
        Assert.Empty(data.Items);
        Assert.Equal(2, Assert.Single(data.Stackables).Count);
        Assert.Equal([false, true, true], data.Changes.Select(c => c.Undone));
        Assert.Equal(ChangeType.StackableCountUpdated, data.Changes[1].Change.Type);
        Assert.Equal((2, 9), (data.Changes[1].Change.Before!.Count, data.Changes[1].Change.After!.Count));
    }

    [Fact]
    public void New_capture_after_undo_drops_redo_entries_but_keeps_withdrawn_capture()
    {
        var t = NewTracker();
        Capture(t, Fixture.Load(Fixture.Waystone), A);
        t.Undo();
        _store.SaveTracker(t);
        Capture(t, Fixture.Load(Fixture.BodyArmourRare), B);

        var data = _store.LoadSession(t.Session.Id)!;

        Assert.Equal(2, data.Captures.Count);
        Assert.True(data.Captures[0].IsWithdrawn);
        Assert.Equal(ChangeType.ItemAdded, Assert.Single(data.Changes).Change.Type);
        Assert.Equal("Horror Salvation Ironclad Vestments", Assert.Single(data.Items).Label);
    }

    [Fact]
    public void Lists_sessions_newest_first_with_counts()
    {
        var first = NewTracker();
        Capture(first, Fixture.Load(Fixture.Waystone), A);
        Capture(first, Fixture.Load(Fixture.ChanceShard), A);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = NewTracker(first.Session);
        _store.SaveSession(first.Session);

        var list = _store.ListSessions();

        Assert.Equal([second.Session.Id, first.Session.Id], list.Select(s => s.Session.Id));
        Assert.Equal((0, 0, true), (list[0].RegularItemCount, list[0].StackableCount, list[0].IsEmpty));
        Assert.Equal((1, 1, 2), (list[1].RegularItemCount, list[1].StackableCount, list[1].CaptureCount));
        Assert.Equal(second.Session.StartedAt, list[1].Session.EndedAt);
        Assert.Single(_store.ListSessions(startedFrom: second.Session.StartedAt));
    }

    [Fact]
    public void Recovers_interrupted_sessions_at_last_capture()
    {
        var t = NewTracker();
        Capture(t, Fixture.Load(Fixture.Waystone), A);
        var lastCapture = _time.Now;
        _time.Advance(TimeSpan.FromHours(1));

        Reopen();
        var recovered = Assert.Single(_store.RecoverInterrupted());

        var session = _store.GetSession(t.Session.Id)!;
        Assert.Equal(recovered.Session.Id, session.Id);
        Assert.Equal(lastCapture, session.EndedAt);
        Assert.True(session.Interrupted);
        Assert.Empty(_store.RecoverInterrupted());
    }

    [Fact]
    public void Delete_cascades_to_everything_in_the_session()
    {
        var keep = NewTracker();
        Capture(keep, Fixture.Load(Fixture.Waystone), A);
        var drop = NewTracker(keep.Session);
        Capture(drop, Fixture.Load(Fixture.Verisium), A);

        Assert.True(_store.DeleteSession(drop.Session.Id));

        Assert.Null(_store.LoadSession(drop.Session.Id));
        Assert.Single(_store.LoadSession(keep.Session.Id)!.Captures);
        Assert.False(_store.DeleteSession(drop.Session.Id));
    }
}
