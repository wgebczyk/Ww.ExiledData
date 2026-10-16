using Ww.ExiledData.App;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

public sealed class SweepControllerTests : IDisposable
{
    private static readonly GridKind StashKind = new("Stash", 12, 12);
    private static readonly GridGeometry Stash = new(400, 200, 50, 12, 12);
    private static readonly ScreenPoint A = new(525, 325); // cell c3 r3

    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteStore _store = SqliteStore.Open(":memory:");
    private readonly SessionController _controller;
    private readonly List<Feedback> _feedback = [];
    private readonly List<ActiveSessionInfo?> _states = [];

    public SweepControllerTests()
    {
        _controller = new SessionController(_store, 8, _time);
        _controller.FeedbackRaised += _feedback.Add;
        _controller.StateChanged += _states.Add;
    }

    public void Dispose() => _store.Dispose();

    private void Copy(string fixture, ScreenPoint at)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        _controller.OnItemCopied(new CopiedItem(Fixture.LoadCrLf(fixture), at, _time.Now, TimeSpan.FromMilliseconds(12)));
    }

    [Fact]
    public async Task Sweep_start_capture_and_finish_are_reported_with_cells()
    {
        var info = await _controller.StartSweep(StashKind, Stash);
        Copy(Fixture.Waystone, A);
        Copy(Fixture.Waystone, A with { X = A.X + 50 });
        Copy(Fixture.Waystone, new ScreenPoint(10, 10));

        Assert.Equal("Stash 1", info.Label);
        Assert.Equal([FeedbackKind.SweepStarted, FeedbackKind.Added, FeedbackKind.Covered, FeedbackKind.SiblingAdded],
            _feedback.Select(f => f.Kind));
        Assert.True(_feedback[^1].OutsideGrid);
        Assert.False(_feedback[1].OutsideGrid);
        var sweep = _states[^1]!.Sweep!;
        Assert.Equal([CellState.Captured, CellState.Covered], sweep.Cells.Select(c => c.State));
        Assert.Equal((1, 3), (sweep.ItemCount, sweep.ChangeCount));

        Assert.Equal(1, await _controller.FinishSweep());
        Assert.Null(_states[^1]!.Sweep);
        Assert.Equal((FeedbackKind.SweepEnded, 1), (_feedback[^1].Kind, _feedback[^1].Count));
        Assert.Null(await _controller.FinishSweep());
        Assert.Equal(SweepStatus.Finished, _store.LoadSession(_states[^1]!.Id)!.Sweeps[0].Sweep.Status);
    }

    [Fact]
    public async Task Failed_copy_and_skip_hotkey_mark_cells()
    {
        await _controller.StartSweep(StashKind, Stash);

        _controller.OnCopyIgnored(new CopyPress(A, 1, "Path of Exile 2", _time.Now), CopyIgnoreReason.ClipboardTimeout);
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.SkipCell, A with { Y = A.Y + 50 }));

        Assert.Equal([CellState.Failed, CellState.Skipped], _states[^1]!.Sweep!.Cells.Select(c => c.State));
        Assert.Equal(FeedbackKind.CellSkipped, _feedback[^1].Kind);

        _controller.OnHotkey(new HotkeyPress(HotkeyAction.SkipCell, new ScreenPoint(10, 10)));
        Assert.Equal(FeedbackKind.Info, _feedback[^1].Kind);
    }

    [Fact]
    public async Task Copy_outside_the_game_is_reported_only_during_a_sweep()
    {
        var press = new CopyPress(A, 1, "Ww.ExiledData", _time.Now);
        _controller.OnCopyIgnored(press, CopyIgnoreReason.NotGameWindow);
        Assert.Empty(_feedback);

        await _controller.StartSweep(StashKind, Stash);
        _controller.OnCopyIgnored(press, CopyIgnoreReason.NotGameWindow);
        Assert.Equal(FeedbackKind.NotInGame, _feedback[^1].Kind);
        Assert.Empty(_states[^1]!.Sweep!.Cells); // the cell is not marked failed: the game never got the copy
    }

    [Fact]
    public async Task Reposition_moves_an_empty_sweep_and_is_stored()
    {
        var info = await _controller.StartSweep(StashKind, Stash);
        var moved = Stash.Translate(30, -20);

        Assert.True(await _controller.RepositionSweep(moved));

        var sweep = _states[^1]!.Sweep!;
        Assert.Equal((info.Id, moved), (sweep.Id, sweep.Grid));
        Assert.Equal(moved, _store.LoadSession(_states[^1]!.Id)!.Sweeps[0].Sweep.Grid);
    }

    [Fact]
    public async Task Reposition_is_refused_once_a_cell_holds_something()
    {
        Assert.False(await _controller.RepositionSweep(Stash)); // no sweep running

        await _controller.StartSweep(StashKind, Stash);
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.SkipCell, A));

        Assert.False(await _controller.RepositionSweep(Stash.Translate(30, 0)));
        Assert.Equal(Stash, _states[^1]!.Sweep!.Grid);
    }

    [Fact]
    public async Task Cancel_withdraws_and_undo_restores()
    {
        await _controller.StartSweep(StashKind, Stash);
        Copy(Fixture.Waystone, A);
        Copy(Fixture.Verisium, A with { X = A.X + 100 });

        Assert.Equal(2, await _controller.CancelSweep());
        Assert.Equal(FeedbackKind.SweepCanceled, _feedback[^1].Kind);
        var active = (await _controller.GetActive())!;
        Assert.Equal((0, 0, null), (active.ItemCount, active.StackableCount, active.Sweep));

        await _controller.Undo();
        active = (await _controller.GetActive())!;
        Assert.Equal((1, 1), (active.ItemCount, active.StackableCount));
        Assert.Null(await _controller.CancelSweep());
    }

    [Fact]
    public async Task New_session_finishes_the_running_sweep()
    {
        await _controller.StartSweep(StashKind, Stash);
        var first = (await _controller.GetActive())!.Id;

        await _controller.StartNewSession();

        Assert.Null((await _controller.GetActive())!.Sweep);
        Assert.Equal(SweepStatus.Finished, _store.LoadSession(first)!.Sweeps[0].Sweep.Status);
    }

    [Fact]
    public async Task Split_covered_from_the_ui()
    {
        await _controller.StartSweep(StashKind, Stash);
        Copy(Fixture.Waystone, A);
        Copy(Fixture.Waystone, A with { X = A.X + 50 });
        var id = (await _controller.GetActive())!.Id;
        var item = Assert.Single(_store.LoadSession(id)!.Items);

        Assert.True(await _controller.SplitCovered(id, item.CaptureId));
        Assert.False(await _controller.SplitCovered(id, item.CaptureId));

        Assert.Equal(2, _store.LoadSession(id)!.Items.Count);
    }

    [Fact]
    public void Sweep_start_and_finish_hotkeys_go_to_the_ui()
    {
        var raised = new List<HotkeyAction>();
        _controller.SweepHotkeyPressed += p => raised.Add(p.Action);

        _controller.OnHotkey(new HotkeyPress(HotkeyAction.SweepStart, A));
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.SweepFinish, A));

        Assert.Equal([HotkeyAction.SweepStart, HotkeyAction.SweepFinish], raised);
        Assert.Empty(_feedback);
    }

    [Fact]
    public void Settings_validate_sweep_hotkeys_and_grid_kinds()
    {
        var defaults = new AppSettings();
        Assert.Empty(defaults.Validate());
        Assert.Equal(GridKind.Presets, defaults.ParsedGridKinds());
        Assert.Equal(12, defaults.ToCaptureOptions().Hotkeys.Count);

        Assert.Contains(new AppSettings { HotkeySkipCell = defaults.HotkeyUndo }.Validate(), e => e.Contains("different"));
        Assert.Contains(new AppSettings { HotkeySweepStart = "Ctrl+C" }.Validate(), e => e.Contains("reserved"));
        Assert.NotEmpty(new AppSettings { GridKinds = "Stash=12" }.Validate());
    }
}
