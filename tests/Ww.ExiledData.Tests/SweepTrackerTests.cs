using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Tracking;

namespace Ww.ExiledData.Tests;

public class SweepTrackerTests
{
    private static readonly GridKind StashKind = new("Stash", 12, 12);
    private static readonly GridGeometry Stash = new(100, 200, 50, 12, 12);
    private static readonly ScreenPoint Outside = new(20, 20);

    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionTracker _tracker;

    public SweepTrackerTests() =>
        _tracker = new SessionTracker(SessionLifecycle.Start(null, _time.Now), 8, _time);

    /// <summary>A point inside cell (column, row), offset from the cell's top-left corner.</summary>
    private static ScreenPoint At(int column, int row, int dx = 25, int dy = 25) =>
        new(100 + column * 50 + dx, 200 + row * 50 + dy);

    private CaptureResult Capture(string fixture, ScreenPoint at, string? text = null)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _tracker.Apply(text ?? Fixture.LoadCrLf(fixture), at);
    }

    private Sweep Start() => _tracker.StartSweep(StashKind, Stash);

    private CellState StateOf(GridCell cell) =>
        _tracker.CellsOf(_tracker.Sweeps[^1].Id).FirstOrDefault(c => c.Cell == cell)?.State ?? CellState.Pending;

    [Fact]
    public void Capture_inside_the_grid_records_its_cell()
    {
        var sweep = Start();

        var r = Capture(Fixture.Waystone, At(3, 2));

        Assert.Equal((CaptureOutcome.Added, new GridCell(3, 2), false), (r.Outcome, r.Cell, r.OutsideGrid));
        var item = Assert.Single(_tracker.Items);
        Assert.Equal((sweep.Id, new GridCell(3, 2)), (item.SweepId, item.Cell));
        Assert.Equal(new GridCell(3, 2), _tracker.Log[0].Cell);
        Assert.Equal(CellState.Captured, StateOf(new GridCell(3, 2)));
        Assert.Equal(1, _tracker.ItemCountOf(sweep.Id));
        Assert.Equal(sweep.Id, Assert.Single(_tracker.History).SweepId);
    }

    [Fact]
    public void Same_text_same_cell_is_already_recorded_however_far_apart_in_pixels()
    {
        Start();
        Capture(Fixture.Waystone, At(3, 2, dx: 1, dy: 1));

        var r = Capture(Fixture.Waystone, At(3, 2, dx: 48, dy: 48)); // 47 px apart, tolerance is 8

        Assert.Equal(CaptureOutcome.AlreadyRecorded, r.Outcome);
        Assert.Single(_tracker.Log);
    }

    [Fact]
    public void Same_text_in_a_neighbouring_cell_covers_it_as_part_of_the_same_item()
    {
        var sweep = Start();
        Capture(Fixture.BodyArmourRare, At(0, 0));

        var r = Capture(Fixture.BodyArmourRare, At(1, 1)); // diagonal, e.g. a 2x3 body armour

        Assert.Equal(CaptureOutcome.Covered, r.Outcome);
        var item = Assert.Single(_tracker.Items);
        Assert.Equal([_tracker.Log[1].Id], item.CoveredCaptureIds);
        Assert.Equal(CellState.Covered, StateOf(new GridCell(1, 1)));
        Assert.Equal(1, _tracker.ItemCountOf(sweep.Id));

        // A further cell next to a covered one belongs to the same item too; re-hovering a covered cell is a refresh.
        Assert.Equal(CaptureOutcome.Covered, Capture(Fixture.BodyArmourRare, At(1, 2)).Outcome);
        Assert.Equal(CaptureOutcome.AlreadyRecorded, Capture(Fixture.BodyArmourRare, At(1, 1)).Outcome);
        Assert.Equal(2, Assert.Single(_tracker.Items).CoveredCaptureIds.Count);
    }

    [Fact]
    public void Same_text_in_a_distant_cell_is_a_sibling()
    {
        Start();
        Capture(Fixture.Waystone, At(0, 0));

        Assert.Equal(CaptureOutcome.SiblingAdded, Capture(Fixture.Waystone, At(2, 0)).Outcome);
        Assert.Equal(2, _tracker.Items.Count);
        Assert.True(_tracker.Items[1].PossibleDuplicate);
    }

    [Fact]
    public void Same_cell_in_another_sweep_is_a_sibling_not_a_refresh()
    {
        Start();
        Capture(Fixture.Waystone, At(4, 4));
        _tracker.FinishSweep();
        Start(); // e.g. the next stash tab, same screen position

        Assert.Equal(CaptureOutcome.SiblingAdded, Capture(Fixture.Waystone, At(4, 4)).Outcome);
        Assert.Equal("Stash 2", _tracker.Sweeps[1].Label);
    }

    [Fact]
    public void Capture_outside_the_grid_is_an_ordinary_capture()
    {
        Start();

        var r = Capture(Fixture.Waystone, Outside);
        var again = Capture(Fixture.Waystone, Outside with { X = Outside.X + 5 });

        Assert.Equal((CaptureOutcome.Added, null, true), (r.Outcome, r.Cell, r.OutsideGrid));
        Assert.Equal(CaptureOutcome.AlreadyRecorded, again.Outcome); // pixel tolerance still applies outside
        Assert.Null(Assert.Single(_tracker.Items).Cell);
        Assert.Empty(_tracker.CellsOf(_tracker.Sweeps[0].Id));
    }

    [Fact]
    public void Without_a_sweep_nothing_is_outside_the_grid()
    {
        var r = Capture(Fixture.Waystone, At(1, 1));

        Assert.Equal((CaptureOutcome.Added, null, false), (r.Outcome, r.Cell, r.OutsideGrid));
    }

    [Fact]
    public void Undo_returns_the_cell_to_pending()
    {
        Start();
        Capture(Fixture.BodyArmourRare, At(0, 0));
        Capture(Fixture.BodyArmourRare, At(0, 1));

        var u = _tracker.Undo()!;
        Assert.Equal(new GridCell(0, 1), u.Cell);
        Assert.Equal(CellState.Pending, StateOf(new GridCell(0, 1)));
        Assert.Empty(_tracker.Items[0].CoveredCaptureIds);

        _tracker.Undo();
        Assert.Equal(CellState.Pending, StateOf(new GridCell(0, 0)));

        _tracker.Redo();
        _tracker.Redo();
        Assert.Equal(CellState.Covered, StateOf(new GridCell(0, 1)));
        Assert.Single(Assert.Single(_tracker.Items).CoveredCaptureIds);
    }

    [Fact]
    public void Same_stack_in_another_cell_adds_up_and_the_same_cell_again_does_not()
    {
        Start();
        Capture(Fixture.ChanceShard, At(0, 0));

        var r = Capture(Fixture.ChanceShard, At(5, 5)); // even with the same count

        Assert.Equal((CaptureOutcome.StackableUpdated, 2), (r.Outcome, r.StackCount));
        Assert.Equal(CellState.Captured, StateOf(new GridCell(5, 5)));
        Assert.Equal(2, Assert.Single(_tracker.Stackables).Stacks.Count);
        Assert.Null(Capture(Fixture.ChanceShard, At(5, 5, dx: 48, dy: 48)).Change); // same cell again: nothing new

        _tracker.Undo();
        Assert.Equal(CellState.Pending, StateOf(new GridCell(5, 5)));
        Assert.Equal(1, Assert.Single(_tracker.Stackables).Count);
    }

    [Fact]
    public void Same_cell_in_a_later_sweep_is_a_further_stash_and_adds_up()
    {
        Start();
        Capture(Fixture.Verisium, At(0, 0));
        _tracker.FinishSweep();
        Start(); // stashed again later in the session: the inventory's first cell holds Verisium once more

        var r = Capture(Fixture.Verisium, At(0, 0));

        Assert.Equal((CaptureOutcome.StackableUpdated, 62), (r.Outcome, r.StackCount));
        Assert.Equal(_tracker.Sweeps.Select(s => (Guid?)s.Id), Assert.Single(_tracker.Stackables).Stacks.Select(t => t.SweepId));
    }

    [Fact]
    public void Stack_count_update_in_a_cell_is_undone_with_the_cell()
    {
        Start();
        Capture(Fixture.Verisium, At(0, 0));
        Capture(Fixture.Verisium, At(0, 0), Fixture.Load(Fixture.Verisium).Replace("31/1,000", "12/1,000"));
        Capture(Fixture.Verisium, At(1, 0), Fixture.Load(Fixture.Verisium).Replace("31/1,000", "5/1,000"));

        Assert.Equal(17, Assert.Single(_tracker.Stackables).Count); // 12 replaced 31 in its cell
        Assert.Equal(CellState.Captured, StateOf(new GridCell(1, 0)));
        _tracker.Undo();
        Assert.Equal(CellState.Pending, StateOf(new GridCell(1, 0)));
        _tracker.Undo();
        Assert.Equal(31, Assert.Single(_tracker.Stackables).Count);
        Assert.Equal(CellState.Captured, StateOf(new GridCell(0, 0)));
    }

    [Fact]
    public void Split_turns_a_covered_cell_into_its_own_item_and_is_undoable()
    {
        var sweep = Start();
        Capture(Fixture.Waystone, At(0, 0));
        Capture(Fixture.Waystone, At(1, 0)); // two identical waystones side by side
        var parent = _tracker.Items[0];

        var split = _tracker.SplitCovered(parent.CaptureId)!;

        Assert.Equal((CaptureOutcome.Split, new GridCell(1, 0)), (split.Outcome, split.Cell));
        Assert.Equal(2, _tracker.Items.Count);
        Assert.Empty(parent.CoveredCaptureIds);
        Assert.True(_tracker.Items[1].PossibleDuplicate);
        Assert.Equal(CellState.Captured, StateOf(new GridCell(1, 0)));
        Assert.Equal(2, _tracker.ItemCountOf(sweep.Id));
        Assert.Null(_tracker.SplitCovered(parent.CaptureId));

        _tracker.Undo();
        Assert.Single(_tracker.Items);
        Assert.Single(parent.CoveredCaptureIds);

        _tracker.Redo();
        Assert.Equal(2, _tracker.Items.Count);
    }

    [Fact]
    public void Removing_an_item_withdraws_its_covered_cells_too()
    {
        Start();
        Capture(Fixture.BodyArmourRare, At(0, 0));
        Capture(Fixture.BodyArmourRare, At(0, 1));

        _tracker.RemoveItem(_tracker.Items[0].CaptureId);
        Assert.All(_tracker.Log, c => Assert.True(c.IsWithdrawn));
        Assert.Empty(_tracker.CellsOf(_tracker.Sweeps[0].Id));

        _tracker.Undo();
        Assert.All(_tracker.Log, c => Assert.False(c.IsWithdrawn));
        Assert.Equal(CellState.Covered, StateOf(new GridCell(0, 1)));
    }

    [Fact]
    public void Failed_and_skipped_marks_until_a_capture_lands()
    {
        Start();

        Assert.Equal(new GridCell(2, 2), _tracker.MarkFailed(At(2, 2)));
        Assert.Null(_tracker.MarkFailed(Outside));
        Assert.Equal(CellState.Failed, StateOf(new GridCell(2, 2)));

        Assert.Equal((new GridCell(3, 3), CellState.Skipped), _tracker.ToggleSkip(At(3, 3)));
        Assert.Equal(CellState.Skipped, StateOf(new GridCell(3, 3)));
        Assert.Equal((new GridCell(3, 3), CellState.Pending), _tracker.ToggleSkip(At(3, 3)));
        Assert.Equal(CellState.Pending, StateOf(new GridCell(3, 3)));

        Capture(Fixture.Waystone, At(2, 2)); // retry succeeded
        Assert.Equal(CellState.Captured, StateOf(new GridCell(2, 2)));
        Assert.Null(_tracker.MarkFailed(At(2, 2))); // a failed re-copy doesn't hide a captured cell
        Assert.Null(_tracker.ToggleSkip(At(2, 2)));

        _tracker.Undo();
        Assert.Equal(CellState.Pending, StateOf(new GridCell(2, 2)));
    }

    [Fact]
    public void Marks_need_a_running_sweep()
    {
        Assert.Null(_tracker.MarkFailed(At(1, 1)));
        Assert.Null(_tracker.ToggleSkip(At(1, 1)));
    }

    [Fact]
    public void Starting_a_sweep_finishes_the_running_one()
    {
        var first = Start();
        _time.Advance(TimeSpan.FromMinutes(1));

        var second = _tracker.StartSweep(new GridKind("Inventory", 12, 5), new GridGeometry(0, 0, 40, 12, 5));

        Assert.Equal((SweepStatus.Finished, _time.Now), (first.Status, first.EndedAt));
        Assert.Same(second, _tracker.ActiveSweep);
        Assert.Equal("Inventory 1", second.Label);
        Assert.Throws<ArgumentException>(() => _tracker.StartSweep(StashKind, new GridGeometry(0, 0, 40, 12, 5)));
    }

    [Fact]
    public void Finished_sweep_keeps_its_captures_and_later_captures_are_ordinary()
    {
        var sweep = Start();
        Capture(Fixture.Waystone, At(0, 0));

        Assert.Same(sweep, _tracker.FinishSweep());
        Assert.Null(_tracker.FinishSweep());

        var r = Capture(Fixture.BodyArmourRare, At(5, 5));
        Assert.Equal((null, false), (r.Cell, r.OutsideGrid));
        Assert.Null(_tracker.History.First().SweepId);
        Assert.Equal(1, _tracker.ItemCountOf(sweep.Id));
    }

    [Fact]
    public void Cancel_withdraws_everything_done_during_the_sweep_as_one_undoable_change()
    {
        Capture(Fixture.Transmutation, Outside); // before the sweep: kept
        var sweep = Start();
        Capture(Fixture.Waystone, At(0, 0));
        Capture(Fixture.BodyArmourRare, At(4, 0));
        Capture(Fixture.BodyArmourRare, At(4, 1));
        Capture(Fixture.Transmutation, At(6, 6), Fixture.Load(Fixture.Transmutation).Replace("2/40", "9/40"));
        Capture(Fixture.ChanceShard, Outside); // outside the grid, but during the sweep
        Assert.Equal(5, _tracker.ChangesInActiveSweep);

        var (canceled, change) = _tracker.CancelSweep()!.Value;

        Assert.Same(sweep, canceled);
        Assert.Equal(SweepStatus.Canceled, sweep.Status);
        Assert.Null(_tracker.ActiveSweep);
        Assert.Equal(ChangeType.SweepCanceled, change!.Type);
        Assert.Equal(5, change.Children!.Count);
        Assert.Empty(_tracker.Items);
        Assert.Equal(2, Assert.Single(_tracker.Stackables).Count);
        Assert.Empty(_tracker.CellsOf(sweep.Id));
        Assert.Equal([false, true, true, true, true, true], _tracker.Log.Select(c => c.IsWithdrawn));

        _tracker.Undo();
        Assert.Equal(SweepStatus.Finished, sweep.Status);
        Assert.Single(_tracker.Items, i => i.CoveredCaptureIds.Count == 1);
        Assert.Equal(2, _tracker.Items.Count);
        Assert.Equal(11, _tracker.Stackables.Single(s => s.Name == "Orb of Transmutation").Count); // 2 outside + 9 in the cell
        Assert.Equal(2, _tracker.Stackables.Count);
        Assert.Equal(3, _tracker.ItemCountOf(sweep.Id));

        _tracker.Redo();
        Assert.Equal(SweepStatus.Canceled, sweep.Status);
        Assert.Empty(_tracker.Items);

        // Once the cancel is undone, the sweep's changes are individually undoable again.
        _tracker.Undo();
        Assert.Equal(5, _tracker.History.Count(c => c.SweepId == sweep.Id));
        _tracker.Undo(); // the chance shard
        Assert.Equal(4, _tracker.History.Count(c => c.SweepId == sweep.Id));
        Assert.Single(_tracker.Stackables);

        // Redo in order: the shard, then the cancel again.
        _tracker.Redo();
        _tracker.Redo();
        Assert.Equal(SweepStatus.Canceled, sweep.Status);
        Assert.Empty(_tracker.Items);
        Assert.False(_tracker.CanRedo);
    }

    [Fact]
    public void Cancel_with_nothing_recorded_makes_no_change()
    {
        Start();
        var (sweep, change) = _tracker.CancelSweep()!.Value;

        Assert.Null(change);
        Assert.Equal(SweepStatus.Canceled, sweep.Status);
        Assert.False(_tracker.CanUndo);
        Assert.Null(_tracker.CancelSweep());
    }

    [Fact]
    public void Undone_changes_of_the_sweep_are_not_withdrawn_twice_by_cancel()
    {
        Start();
        Capture(Fixture.Waystone, At(0, 0));
        Capture(Fixture.BodyArmourRare, At(5, 5));
        _tracker.Undo();

        var (_, change) = _tracker.CancelSweep()!.Value;

        Assert.Single(change!.Children!);
        Assert.False(_tracker.CanRedo);
        _tracker.Undo();
        Assert.Equal("Waystone (Tier 1) of Hexwarding", Assert.Single(_tracker.Items).Label);
    }
}
