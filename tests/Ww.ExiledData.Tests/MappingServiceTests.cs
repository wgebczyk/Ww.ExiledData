using Ww.ExiledData.App.Services;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

/// <summary>The mapping module on the real seam: the session controller publishes, the service records.</summary>
public sealed class MappingServiceTests : IDisposable
{
    private static readonly GridKind Inventory = new("Inventory", 12, 5);
    private static readonly GridGeometry Grid = new(400, 200, 100, 12, 5);
    private static ScreenPoint CellAt(int col, int row) => new(450 + col * 100, 250 + row * 100);

    private readonly string _dir = Directory.CreateTempSubdirectory("ww-mapping-").FullName;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero));
    private readonly SqliteStore _core;
    private readonly MappingStore _mappingStore;
    private readonly SessionController _controller;
    private readonly MappingService _mapping;
    private readonly List<Guid> _changed = [];

    public MappingServiceTests()
    {
        var path = Path.Combine(_dir, "data.db");
        _core = SqliteStore.Open(path);
        _mappingStore = MappingStore.Open(path);
        _controller = new SessionController(_core, 8, _time);
        _mapping = new MappingService(_mappingStore, _controller, time: _time);
        _mapping.Changed += _changed.Add;
    }

    public void Dispose()
    {
        _mappingStore.Dispose();
        _core.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Copy(string text, ScreenPoint at)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        _controller.OnItemCopied(new CopiedItem(text, at, _time.Now, TimeSpan.FromMilliseconds(10)));
    }

    private async Task Scan(params (string Name, int Count)[] stacks) => await Scan(null, stacks);

    private async Task Scan(ScanKind? kind, params (string Name, int Count)[] stacks)
    {
        if (kind is not null)
            await _mapping.SetNextScanKind(kind);
        await _controller.StartSweep(Inventory, Grid);
        var col = 0;
        foreach (var (name, count) in stacks)
            Copy(MapItems.Currency(name, count), CellAt(col++, 0));
        await _controller.FinishSweep();
    }

    [Fact]
    public async Task A_mapping_session_records_runs_scans_and_loot()
    {
        var id = await _controller.StartNewSession("rituals", SessionKind.Mapping);

        Copy(Fixture.LoadCrLf(Fixture.Waystone), new ScreenPoint(100, 100));
        Copy(MapItems.Tablet("Ritual Tablet"), new ScreenPoint(120, 100));
        await Scan(("Chaos Orb", 3)); // the Initial swipe
        await Scan(ScanKind.Stash, ("Chaos Orb", 10), ("Chaos Orb", 2), ("Divine Orb", 1));
        Copy(MapItems.Waystone(16), new ScreenPoint(100, 100));
        await _mapping.EndRun(id);

        var session = await _mapping.Load(id);
        Assert.Equal(2, session.Runs.Count);
        Assert.Equal(["Ritual Tablet"], session.Runs[0].Tablets.Select(t => t.Label));
        var loot = Loot.Of(session, session.Runs[0]);
        Assert.Equal([("Chaos Orb", 12), ("Divine Orb", 1)], loot.Total.Select(c => (c.Name, c.Count)));
        Assert.All(session.Scans, s => Assert.Equal(ScanStatus.Finished, s.Status));
        Assert.Equal(session.Runs[1].StartedAt, session.Runs[0].EndedAt);
        Assert.NotNull(session.Runs[1].EndedAt);
        Assert.All(_changed, c => Assert.Equal(id, c));

        // Captures of the run are the capture log's entries.
        var log = _core.LoadSession(id)!.Captures.Select(c => c.Id).ToHashSet();
        Assert.All(session.Runs[0].CaptureIds, c => Assert.Contains(c, log));
    }

    [Fact]
    public async Task Free_recording_sessions_are_ignored()
    {
        var id = await _controller.StartNewSession(kind: SessionKind.FreeRecording);

        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));
        await Scan(("Chaos Orb", 3));

        Assert.Empty((await _mapping.Load(id)).Runs);
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task A_canceled_sweep_is_a_canceled_scan()
    {
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);
        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));
        await _controller.StartSweep(Inventory, Grid);
        Copy(MapItems.Currency("Chaos Orb", 3), CellAt(0, 0));

        await _controller.CancelSweep();

        var scan = Assert.Single((await _mapping.Load(id)).Scans);
        Assert.Equal(ScanStatus.Canceled, scan.Status);
    }

    [Fact]
    public async Task The_chosen_scan_kind_applies_to_the_next_scan_only()
    {
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);
        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));

        await _mapping.SetNextScanKind(ScanKind.Stash);
        await Scan(("Chaos Orb", 3));
        await Scan(("Chaos Orb", 5));

        var session = await _mapping.Load(id);
        Assert.Equal([ScanKind.Stash, ScanKind.Swipe], session.Scans.Select(s => s.Kind));
        Assert.Equal(3, Loot.Of(session, session.Runs[0]).Total.Single().Count);
    }

    [Fact]
    public async Task Continuing_an_ended_session_resumes_run_numbering()
    {
        var first = await _controller.StartNewSession(kind: SessionKind.Mapping);
        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));
        Copy(MapItems.Waystone(16), new ScreenPoint(100, 100));
        await _controller.StartNewSession(kind: SessionKind.Mapping);

        await _controller.ContinueSession(first);
        Copy(MapItems.Waystone(17), new ScreenPoint(100, 100));

        // A fresh service (as after an app restart) sees the same data.
        var reloaded = new MappingService(_mappingStore, new SessionController(_core, 8, _time));
        Assert.Equal([1, 2, 3], (await reloaded.Load(first)).Runs.Select(r => r.Number));
    }

    [Fact]
    public async Task Edits_are_stored_and_undoable()
    {
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);
        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));
        var run = (await _mapping.Load(id)).Runs[0].Id;

        await _mapping.AddTag(id, run, "rituals: 6");
        await _mapping.SetNote(id, id, "farming rituals");
        await _mapping.Undo(id);

        var session = await _mapping.Load(id);
        Assert.Equal([new MapTag("rituals", "6")], session.TagsOf(run));
        Assert.Null(session.NoteOf(id));
        Assert.Equal((true, true), await _mapping.UndoState(id));
        Assert.Equal([new MapTag("rituals", "6")], await _mapping.KnownTags());
    }

    [Fact]
    public async Task Recovery_closes_scans_left_active()
    {
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);
        Copy(MapItems.Waystone(15), new ScreenPoint(100, 100));
        await _controller.StartSweep(Inventory, Grid);
        Copy(MapItems.Currency("Chaos Orb", 3), CellAt(0, 0));

        // App exit: nothing finishes the sweep. The next launch recovers.
        var next = new MappingService(_mappingStore, new SessionController(_core, 8, _time));
        Assert.Equal(1, await next.RecoverInterrupted());

        var scan = Assert.Single((await next.Load(id)).Scans);
        Assert.Equal(ScanStatus.Interrupted, scan.Status);
        Assert.True(scan.Counts);
    }
}
