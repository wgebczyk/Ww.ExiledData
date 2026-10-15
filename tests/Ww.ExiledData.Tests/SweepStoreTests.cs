using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

public sealed class SweepStoreTests : IDisposable
{
    private static readonly GridKind StashKind = new("Stash", 12, 12);
    private static readonly GridGeometry Stash = new(100.5, 200, 50.25, 12, 12);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ww-exileddata-{Guid.NewGuid():N}.db");
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private SqliteStore _store;

    public SweepStoreTests() => _store = SqliteStore.Open(_path);

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

    private static ScreenPoint At(int column, int row) => new(130 + column * 50, 230 + row * 50);

    private SessionTracker NewTracker()
    {
        var tracker = new SessionTracker(SessionLifecycle.Start(null, _time.Now), 8, _time);
        _store.SaveSession(tracker.Session);
        return tracker;
    }

    private void Capture(SessionTracker tracker, string fixture, ScreenPoint at)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        tracker.Apply(Fixture.Load(fixture), at);
        _store.SaveTracker(tracker);
    }

    [Fact]
    public void Round_trips_sweeps_cells_and_covered_items()
    {
        var t = NewTracker();
        var sweep = t.StartSweep(StashKind, Stash);
        Capture(t, Fixture.BodyArmourRare, At(0, 0));
        Capture(t, Fixture.BodyArmourRare, At(0, 1));
        Capture(t, Fixture.ChanceShard, At(3, 3));
        t.ToggleSkip(At(5, 5));
        t.MarkFailed(At(6, 6));
        _store.SaveTracker(t);

        Reopen();
        var data = _store.LoadSession(t.Session.Id)!;

        var stored = Assert.Single(data.Sweeps);
        Assert.Equal((sweep.Id, "Stash 1", StashKind, Stash, SweepStatus.Active, 2),
            (stored.Sweep.Id, stored.Sweep.Label, stored.Sweep.Kind, stored.Sweep.Grid, stored.Sweep.Status, stored.ItemCount));
        Assert.Equal(t.CellsOf(sweep.Id), data.Cells);
        Assert.Equal([CellState.Captured, CellState.Covered, CellState.Captured, CellState.Skipped, CellState.Failed],
            data.Cells.Select(c => c.State));
        Assert.Equal(t.Log.Select(c => (c.SweepId, c.Cell)), data.Captures.Select(c => (c.SweepId, c.Cell)));
        var item = Assert.Single(data.Items);
        Assert.Equal((sweep.Id, new GridCell(0, 0)), (item.SweepId, item.Cell));
        Assert.Equal(t.Items[0].CoveredCaptureIds, item.CoveredCaptureIds);
        var covered = data.Changes[1].Change;
        Assert.Equal((ChangeType.CellCovered, t.Items[0].CaptureId, sweep.Id), (covered.Type, covered.RelatedCaptureId, covered.SweepId));
    }

    [Fact]
    public void Persists_a_canceled_sweep_with_its_grouped_change()
    {
        var t = NewTracker();
        t.StartSweep(StashKind, Stash);
        Capture(t, Fixture.Waystone, At(0, 0));
        Capture(t, Fixture.Verisium, At(1, 0));
        t.CancelSweep();
        _store.SaveTracker(t);

        var data = _store.LoadSession(t.Session.Id)!;

        Assert.Equal((SweepStatus.Canceled, 0), (data.Sweeps[0].Sweep.Status, data.Sweeps[0].ItemCount));
        Assert.Empty(data.Cells);
        Assert.All(data.Captures, c => Assert.True(c.IsWithdrawn));
        var group = Assert.Single(data.Changes).Change;
        Assert.Equal(ChangeType.SweepCanceled, group.Type);
        Assert.Equal(t.History.Single().Children, group.Children);
    }

    [Fact]
    public void Recovery_marks_running_sweeps_interrupted_at_their_last_capture()
    {
        var t = NewTracker();
        var running = t.StartSweep(StashKind, Stash);
        Capture(t, Fixture.Waystone, At(0, 0));
        var lastCapture = _time.Now;
        _time.Advance(TimeSpan.FromMinutes(10));

        Reopen();
        _store.RecoverInterrupted();

        var sweep = _store.LoadSession(t.Session.Id)!.Sweeps.Single().Sweep;
        Assert.Equal((running.Id, SweepStatus.Interrupted, lastCapture), (sweep.Id, sweep.Status, sweep.EndedAt));
    }

    [Fact]
    public void Calibration_is_found_per_kind_and_resolution_with_a_fallback()
    {
        Assert.Null(_store.FindCalibration(StashKind, 2560, 1440));
        _store.SaveCalibration(new GridCalibration("Stash", 2560, 1440, Stash, _time.Now));
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.SaveCalibration(new GridCalibration("Stash", 1920, 1080, Stash.Translate(-50, -50), _time.Now));
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.SaveCalibration(new GridCalibration("Stash", 2560, 1440, Stash.Resize(1), _time.Now)); // replaces the first

        Reopen();

        Assert.Equal(Stash.Resize(1), _store.FindCalibration(StashKind, 2560, 1440)!.Grid);
        Assert.Equal(Stash.Translate(-50, -50), _store.FindCalibration(StashKind, 1920, 1080)!.Grid);
        var fallback = _store.FindCalibration(StashKind, 3840, 2160)!; // the most recent, at another resolution
        Assert.Equal(2560, fallback.ScreenWidth);
        Assert.Null(_store.FindCalibration(StashKind with { Columns = 24 }, 2560, 1440));
    }
}
