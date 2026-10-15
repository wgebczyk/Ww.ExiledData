using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Tracking;

namespace Ww.ExiledData.Tests;

public class SessionTrackerTests
{
    private const int Tolerance = 8;
    private static readonly ScreenPoint A = new(500, 300);
    private static readonly ScreenPoint B = new(560, 300);

    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionTracker _tracker;

    public SessionTrackerTests() =>
        _tracker = new SessionTracker(SessionLifecycle.Start(null, _time.Now), Tolerance, _time);

    private CaptureResult Capture(string fixture, ScreenPoint at)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _tracker.Apply(Fixture.LoadCrLf(fixture), at);
    }

    private static string WithStack(string fixture, string stackSize) =>
        Fixture.Load(fixture).Replace("Stack Size: 31/1,000", $"Stack Size: {stackSize}");

    [Fact]
    public void Non_item_text_is_ignored()
    {
        Assert.Equal(CaptureOutcome.NotAnItem, _tracker.Apply("some chat text", A).Outcome);
        Assert.Equal(CaptureOutcome.NotAnItem, _tracker.Apply(null, A).Outcome);
        Assert.Empty(_tracker.Log);
    }

    [Fact]
    public void Same_item_same_position_is_already_recorded_and_refreshes_time()
    {
        Capture(Fixture.BodyArmourRare, A);
        var second = Capture(Fixture.BodyArmourRare, A with { X = A.X + Tolerance });

        Assert.Equal(CaptureOutcome.AlreadyRecorded, second.Outcome);
        Assert.Null(second.Change);
        Assert.Single(_tracker.Items);
        Assert.Single(_tracker.Log);
        Assert.Equal(_time.Now, _tracker.Items[0].LastSeenAt);
    }

    [Fact]
    public void Same_item_other_position_is_flagged_sibling()
    {
        Assert.Equal(CaptureOutcome.Added, Capture(Fixture.Waystone, A).Outcome);
        Assert.Equal(CaptureOutcome.SiblingAdded, Capture(Fixture.Waystone, B).Outcome);

        Assert.Equal(2, _tracker.Items.Count);
        Assert.False(_tracker.Items[0].PossibleDuplicate);
        Assert.True(_tracker.Items[1].PossibleDuplicate);
        Assert.Equal("Waystone (Tier 1) of Hexwarding", _tracker.Items[1].Label);
    }

    [Fact]
    public void Stackable_ignores_position_and_last_count_wins()
    {
        var added = _tracker.Apply(Fixture.Load(Fixture.Verisium), A);
        var updated = _tracker.Apply(WithStack(Fixture.Verisium, "25/1,000"), B);

        Assert.Equal(CaptureOutcome.StackableAdded, added.Outcome);
        Assert.Equal(CaptureOutcome.StackableUpdated, updated.Outcome);
        Assert.Equal(25, updated.StackCount);
        var s = Assert.Single(_tracker.Stackables);
        Assert.Equal((25, 1000), (s.Count, s.MaxStack));
        Assert.Empty(_tracker.Items);
    }

    [Fact]
    public void Stackable_identity_ignores_descriptive_text()
    {
        _tracker.Apply(Fixture.Load(Fixture.Verisium), A);
        var atOne = WithStack(Fixture.Verisium, "1/1,000").Replace("Shift click to unstack.\n", "");

        var r = _tracker.Apply(atOne, A);

        Assert.Equal(CaptureOutcome.StackableUpdated, r.Outcome);
        Assert.Equal(1, Assert.Single(_tracker.Stackables).Count);
    }

    [Fact]
    public void Stackable_with_unchanged_count_is_already_recorded()
    {
        Capture(Fixture.ChanceShard, A);

        var r = Capture(Fixture.ChanceShard, B);

        Assert.Equal(CaptureOutcome.AlreadyRecorded, r.Outcome);
        Assert.Single(_tracker.Log);
    }

    [Fact]
    public void Different_max_stack_is_different_stackable()
    {
        _tracker.Apply(Fixture.Load(Fixture.Verisium), A);
        _tracker.Apply(WithStack(Fixture.Verisium, "31/500"), A);

        Assert.Equal(2, _tracker.Stackables.Count);
    }

    [Fact]
    public void Unparsed_stack_header_is_kept_as_regular_item_needing_review()
    {
        var r = _tracker.Apply("Item Class: Stackable Currency\nRarity: Currency\n--------\nStack Size: 5/10", A);

        Assert.Equal(CaptureOutcome.Added, r.Outcome);
        Assert.NotEmpty(r.Warnings!);
        Assert.True(Assert.Single(_tracker.Items).NeedsReview);
    }

    [Fact]
    public void Multi_level_undo_and_redo()
    {
        Capture(Fixture.BodyArmourRare, A);                                    // item added
        Capture(Fixture.Transmutation, A);                                     // stackable added (2)
        _tracker.Apply(Fixture.Load(Fixture.Transmutation).Replace("2/40", "7/40"), B); // updated to 7

        var u1 = _tracker.Undo()!;
        Assert.Equal(CaptureOutcome.Undone, u1.Outcome);
        Assert.Equal(2, u1.StackCount);
        Assert.Equal(2, Assert.Single(_tracker.Stackables).Count);

        _tracker.Undo();
        Assert.Empty(_tracker.Stackables);

        _tracker.Undo();
        Assert.Empty(_tracker.Items);
        Assert.False(_tracker.CanUndo);
        Assert.Null(_tracker.Undo());

        // Soft delete: the log keeps everything, flagged as withdrawn.
        Assert.Equal(3, _tracker.Log.Count);
        Assert.All(_tracker.Log, c => Assert.True(c.IsWithdrawn));

        _tracker.Redo();
        _tracker.Redo();
        var r3 = _tracker.Redo()!;
        Assert.Equal(CaptureOutcome.Redone, r3.Outcome);
        Assert.Equal(7, r3.StackCount);
        Assert.Single(_tracker.Items);
        Assert.Equal(7, Assert.Single(_tracker.Stackables).Count);
        Assert.All(_tracker.Log, c => Assert.False(c.IsWithdrawn));
        Assert.False(_tracker.CanRedo);
    }

    [Fact]
    public void New_capture_after_undo_clears_redo()
    {
        Capture(Fixture.Waystone, A);
        _tracker.Undo();
        Assert.True(_tracker.CanRedo);

        Capture(Fixture.BodyArmourRare, B);

        Assert.False(_tracker.CanRedo);
        Assert.Null(_tracker.Redo());
        Assert.Equal("Horror Salvation Ironclad Vestments", Assert.Single(_tracker.Items).Label);
    }

    [Fact]
    public void Undone_sibling_frees_position_for_recapture()
    {
        Capture(Fixture.Waystone, A);
        _tracker.Undo();

        Assert.Equal(CaptureOutcome.Added, Capture(Fixture.Waystone, A).Outcome);
    }

    [Fact]
    public void Closed_session_is_read_only()
    {
        Capture(Fixture.Waystone, A);
        SessionLifecycle.Start(_tracker.Session, _time.Now);

        Assert.False(_tracker.CanUndo);
        Assert.Throws<InvalidOperationException>(() => _tracker.Apply(Fixture.Load(Fixture.Waystone), B));
        Assert.Throws<InvalidOperationException>(() => _tracker.Undo());
    }

    [Fact]
    public void Remove_any_item_is_undoable_and_redoable()
    {
        Capture(Fixture.Waystone, A);
        Capture(Fixture.BodyArmourRare, B);
        var waystone = _tracker.Items[0];

        var removed = _tracker.RemoveItem(waystone.CaptureId)!;

        Assert.Equal(CaptureOutcome.Removed, removed.Outcome);
        Assert.Equal("Horror Salvation Ironclad Vestments", Assert.Single(_tracker.Items).Label);
        Assert.True(_tracker.Log[0].IsWithdrawn);

        _tracker.Undo();
        Assert.Contains(waystone, _tracker.Items);
        Assert.False(_tracker.Log[0].IsWithdrawn);

        _tracker.Redo();
        Assert.DoesNotContain(waystone, _tracker.Items);
        Assert.Null(_tracker.RemoveItem(waystone.CaptureId));
    }

    [Fact]
    public void Remove_stackable_keeps_its_captures_and_undo_restores_count()
    {
        _tracker.Apply(Fixture.Load(Fixture.Verisium), A);
        _tracker.Apply(WithStack(Fixture.Verisium, "50/1,000"), A);
        var key = Assert.Single(_tracker.Stackables).Key;

        _tracker.RemoveStackable(key);
        Assert.Empty(_tracker.Stackables);
        Assert.All(_tracker.Log, c => Assert.False(c.IsWithdrawn));

        var undone = _tracker.Undo()!;
        Assert.Equal(50, undone.StackCount);
        Assert.Equal(50, Assert.Single(_tracker.Stackables).Count);

        _tracker.Undo(); // the count update
        Assert.Equal(31, Assert.Single(_tracker.Stackables).Count);
    }

    [Fact]
    public void Capture_after_undone_removal_discards_redo()
    {
        Capture(Fixture.Waystone, A);
        _tracker.RemoveItem(_tracker.Items[0].CaptureId);
        _tracker.Undo();

        Capture(Fixture.BodyArmourRare, B);

        Assert.False(_tracker.CanRedo);
        Assert.Equal(2, _tracker.Items.Count);
    }

    [Fact]
    public void Tolerance_change_applies_to_later_captures()
    {
        Capture(Fixture.Waystone, A);
        _tracker.PositionTolerance = 100;

        Assert.Equal(CaptureOutcome.AlreadyRecorded, Capture(Fixture.Waystone, B).Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() => _tracker.PositionTolerance = -1);
    }
}
