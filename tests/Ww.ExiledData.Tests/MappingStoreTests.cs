using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

/// <summary>
/// The mapping tables share the app's database file but use their own connection, so these tests use a temporary
/// file (two connections to ":memory:" would be two databases).
/// </summary>
public sealed class MappingStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ww-mapping-").FullName;
    private readonly SqliteStore _core;
    private readonly MappingStore _store;
    private readonly Session _session;

    public MappingStoreTests()
    {
        var path = Path.Combine(_dir, "data.db");
        _core = SqliteStore.Open(path);
        _store = MappingStore.Open(path);
        _session = SessionLifecycle.Start(null, new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero), "farming", SessionKind.Mapping);
        _core.SaveSession(_session);
    }

    public void Dispose()
    {
        _store.Dispose();
        _core.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private MapFeed Feed() => new() { Session = new MapSession(_session.Id) };

    [Fact]
    public void A_session_round_trips()
    {
        var feed = Feed();
        feed.Swipe(("Chaos Orb", 2));
        feed.Copy(MapItems.Waystone(15));
        feed.Copy(MapItems.Unknown("Mystery"), logged: false);
        feed.Swipe(("Chaos Orb", 3));
        feed.StartScan();
        feed.Stack("Chaos Orb", 15);
        feed.Copy(MapItems.Rare("Armour"), cell: new GridCell(1, 0));
        feed.End(SweepStatus.Canceled);
        var checkpoint = feed.Stash(("Chaos Orb", 15), ("Divine Orb", 1));
        feed.StartScan(ScanKind.Final);
        feed.Stack("Chaos Orb", 4);
        feed.Copy(MapItems.Tablet("Ritual Tablet"), cell: new GridCell(1, 0));
        var final = feed.End();
        var s = feed.Session;
        s.AssignSetup(s.CurrentRun!.Id, s.RecallTablets().Single(i => i.Label == "Ritual Tablet"));
        s.ApplyAdjustments(final.Id, [(MapItems.KeyOf("Chaos Orb"), "Chaos Orb", 2)], feed.Now);
        s.DismissCheck(final.Id, true);
        s.AddTag(s.SessionId, "rituals: 6");
        s.AddTag(s.CurrentRun!.Id, "died");
        s.SetNote(s.CurrentRun.Id, "boss twice");
        s.EndRun(feed.Now);

        _store.Save(s);
        var loaded = _store.Load(_session.Id);

        var run = Assert.Single(loaded.Runs);
        var original = s.Runs[0];
        Assert.Equal((original.Id, 1, original.StartedAt, original.EndedAt), (run.Id, run.Number, run.StartedAt, run.EndedAt));
        Assert.Equal(original.Setup.Select(i => (i.Id, i.Kind, i.CaptureId, i.RawText, i.CopiedAt, i.RecalledFromRun)),
            run.Setup.Select(i => (i.Id, i.Kind, i.CaptureId, i.RawText, i.CopiedAt, i.RecalledFromRun)));
        Assert.Equal(1, run.Setup[^1].RecalledFromRun);
        Assert.Equal(original.Adjustments, run.Adjustments);
        Assert.Null(run.Setup[1].CaptureId);
        Assert.Equal(original.CaptureIds, run.CaptureIds);
        Assert.Equal(s.Scans.Select(x => (x.Id, x.RunId, x.SweepId, x.Label, x.Kind, x.Status, x.StartedAt, x.EndedAt, x.CheckDismissed)),
            loaded.Scans.Select(x => (x.Id, x.RunId, x.SweepId, x.Label, x.Kind, x.Status, x.StartedAt, x.EndedAt, x.CheckDismissed)));
        Assert.Equal(final.Cells.Values.OrderBy(c => c.Cell.Column), loaded.FindScan(final.Id)!.Cells.Values.OrderBy(c => c.Cell.Column));
        Assert.Equal(checkpoint.Cells.Values.OrderBy(c => c.Cell.Column), loaded.FindScan(checkpoint.Id)!.Cells.Values.OrderBy(c => c.Cell.Column));
        Assert.Null(loaded.Scans[2].Cells[new GridCell(1, 0)].Stack);
        Assert.Equal([new MapTag("rituals", "6")], loaded.TagsOf(_session.Id));
        Assert.Equal([new MapTag("died")], loaded.TagsOf(run.Id));
        Assert.Equal("boss twice", loaded.NoteOf(run.Id));
        Assert.Equal(Loot.Of(s, original).Total, Loot.Of(loaded, run).Total);
    }

    [Fact]
    public void Saving_again_replaces_the_rows()
    {
        var feed = Feed();
        feed.Copy(MapItems.Waystone(15));
        feed.Session.AddTag(feed.Session.SessionId, "a");
        _store.Save(feed.Session);
        feed.Session.Undo();
        feed.Copy(MapItems.Waystone(16));
        _store.Save(feed.Session);

        var loaded = _store.Load(_session.Id);
        Assert.Equal(2, loaded.Runs.Count);
        Assert.Empty(loaded.TagsOf(_session.Id));
    }

    [Fact]
    public void Deleting_the_session_deletes_its_mapping_data()
    {
        var feed = Feed();
        feed.Copy(MapItems.Waystone(15));
        feed.Swipe(("Chaos Orb", 3));
        feed.Session.AddTag(feed.Session.SessionId, "x");
        _store.Save(feed.Session);

        _core.DeleteSession(_session.Id);

        var loaded = _store.Load(_session.Id);
        Assert.Empty(loaded.Runs);
        Assert.Empty(loaded.Scans);
        Assert.Empty(_store.ListSummaries());
    }

    [Fact]
    public void Summaries_and_known_tags()
    {
        var other = SessionLifecycle.Start(null, _session.StartedAt.AddHours(1), "other", SessionKind.Mapping);
        _core.SaveSession(other);
        var feed = Feed();
        feed.Copy(MapItems.Waystone(15));
        feed.Copy(MapItems.Waystone(16));
        feed.Session.AddTag(_session.Id, "rituals: 6");
        feed.Session.AddTag(feed.Session.Runs[0].Id, "died");
        feed.Session.AddTag(feed.Session.Runs[1].Id, "died");
        _store.Save(feed.Session);
        var second = new MapSession(other.Id);
        second.AddTag(other.Id, "breach");
        _store.Save(second);

        var summaries = _store.ListSummaries();

        Assert.Equal(2, summaries[_session.Id].RunCount);
        Assert.Equal([new MapTag("rituals", "6")], summaries[_session.Id].Tags);
        Assert.Equal(0, summaries[other.Id].RunCount);
        Assert.Equal(new MapTag("died"), _store.KnownTags()[0]); // most used first
        Assert.Equal(3, _store.KnownTags().Count);
    }

    [Fact]
    public void Reopening_keeps_the_data_and_an_old_schema_is_recreated()
    {
        var feed = Feed();
        feed.Copy(MapItems.Waystone(15));
        _store.Save(feed.Session);
        var path = Path.Combine(_dir, "data.db");

        using (var reopened = MappingStore.Open(path))
            Assert.Single(reopened.Load(_session.Id).Runs);

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE module_schema SET version = 0 WHERE module = 'mapping'";
            cmd.ExecuteNonQuery();
        }
        using (var recreated = MappingStore.Open(path))
            Assert.Empty(recreated.Load(_session.Id).Runs);
    }
}
