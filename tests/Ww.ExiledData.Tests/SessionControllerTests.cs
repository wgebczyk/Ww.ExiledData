using Ww.ExiledData.App.Services;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

public sealed class SessionControllerTests : IDisposable
{
    private static readonly ScreenPoint A = new(500, 300);
    private static readonly ScreenPoint B = new(560, 300);

    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteStore _store = SqliteStore.Open(":memory:");
    private readonly SessionController _controller;
    private readonly List<Feedback> _feedback = [];
    private readonly List<ActiveSessionInfo?> _states = [];

    public SessionControllerTests()
    {
        _controller = new SessionController(_store, 8, _time);
        _controller.FeedbackRaised += _feedback.Add;
        _controller.StateChanged += _states.Add;
    }

    public void Dispose() => _store.Dispose();

    private void Copy(string fixture, ScreenPoint at, string? text = null)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        _controller.OnItemCopied(new CopiedItem(text ?? Fixture.LoadCrLf(fixture), at, _time.Now, TimeSpan.FromMilliseconds(12)));
    }

    [Fact]
    public async Task First_capture_starts_a_session_and_is_persisted()
    {
        Copy(Fixture.Waystone, A);

        var active = (await _controller.GetActive())!;
        Assert.Equal(1, active.ItemCount);
        Assert.True(active.CanUndo);
        var stored = Assert.Single(_store.ListSessions());
        Assert.Equal((active.Id, 1), (stored.Session.Id, stored.RegularItemCount));
        Assert.Equal(FeedbackKind.Added, Assert.Single(_feedback).Kind);
        Assert.Equal(active, _states[^1]);
    }

    [Fact]
    public void Feedback_distinguishes_outcomes()
    {
        Copy(Fixture.Waystone, A);
        Copy(Fixture.Waystone, A);
        Copy(Fixture.Waystone, B);
        Copy(Fixture.Verisium, A);
        Copy(Fixture.Verisium, A, Fixture.Load(Fixture.Verisium).Replace("31/1,000", "45/1,000"));

        Assert.Equal(
            [FeedbackKind.Added, FeedbackKind.AlreadyRecorded, FeedbackKind.SiblingAdded, FeedbackKind.StackableAdded, FeedbackKind.StackableUpdated],
            _feedback.Select(f => f.Kind));
        Assert.Equal(45, _feedback[^1].Count);
        Assert.Contains("Verisium", _feedback[^1].Message);
    }

    [Fact]
    public void Ignored_copies_give_nothing_feedback_except_outside_the_game()
    {
        var press = new CopyPress(A, 1, "Notepad", _time.Now);

        _controller.OnCopyIgnored(press, CopyIgnoreReason.NotGameWindow);
        _controller.OnCopyIgnored(press, CopyIgnoreReason.ClipboardTimeout);

        Assert.Equal(FeedbackKind.Nothing, Assert.Single(_feedback).Kind);
    }

    [Fact]
    public async Task New_session_hotkey_closes_previous_one()
    {
        Copy(Fixture.Waystone, A);
        var first = (await _controller.GetActive())!.Id;
        _time.Advance(TimeSpan.FromMinutes(5));

        _controller.OnHotkey(new HotkeyPress(HotkeyAction.NewSession, A));

        var second = (await _controller.GetActive())!;
        Assert.NotEqual(first, second.Id);
        Assert.Equal(second.StartedAt, _store.GetSession(first)!.EndedAt);
        Assert.Equal(FeedbackKind.NewSession, _feedback[^1].Kind);
        Assert.Equal(0, second.ItemCount);
    }

    [Fact]
    public async Task Undo_redo_hotkeys_update_store_and_report_nothing_when_empty()
    {
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.Undo, A));
        Assert.Equal((FeedbackKind.Info, "Nothing to undo"), (_feedback[^1].Kind, _feedback[^1].Message));

        Copy(Fixture.ChanceShard, A);
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.Undo, A));
        var id = (await _controller.GetActive())!.Id;
        Assert.Empty(_store.LoadSession(id)!.Stackables);
        Assert.Equal(FeedbackKind.Undo, _feedback[^1].Kind);

        _controller.OnHotkey(new HotkeyPress(HotkeyAction.Redo, A));
        Assert.Equal(1, Assert.Single(_store.LoadSession(id)!.Stackables).Count);
        Assert.Equal((FeedbackKind.Redo, 1), (_feedback[^1].Kind, _feedback[^1].Count));
    }

    [Fact]
    public async Task Remove_item_is_undoable_and_only_allowed_in_active_session()
    {
        Copy(Fixture.Waystone, A);
        Copy(Fixture.BodyArmourRare, B);
        var id = (await _controller.GetActive())!.Id;
        var armour = _store.LoadSession(id)!.Items.Single(i => i.Label.StartsWith("Horror"));

        await _controller.RemoveItem(id, armour.CaptureId);
        Assert.Single(_store.LoadSession(id)!.Items);

        await _controller.Undo();
        Assert.Equal(2, _store.LoadSession(id)!.Items.Count);

        await _controller.StartNewSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.RemoveItem(id, armour.CaptureId));
    }

    [Fact]
    public async Task Rename_delete_and_delete_empty()
    {
        await _controller.StartNewSession();
        var empty = (await _controller.GetActive())!.Id;
        _time.Advance(TimeSpan.FromMinutes(1));
        await _controller.StartNewSession();
        Copy(Fixture.Waystone, A);
        var active = (await _controller.GetActive())!.Id;

        await _controller.Rename(empty, "  Act 2 run  ");
        Assert.Equal("Act 2 run", _store.GetSession(empty)!.Name);

        Assert.Equal(1, await _controller.DeleteEmptySessions());
        Assert.Null(_store.GetSession(empty));

        await _controller.DeleteSession(active);
        Assert.Null(await _controller.GetActive());
        Assert.Empty(_store.ListSessions());
    }

    [Fact]
    public async Task Recovery_closes_sessions_left_open()
    {
        Copy(Fixture.Waystone, A);
        var restarted = new SessionController(_store, 8, _time);

        var recovered = Assert.Single(await restarted.RecoverInterrupted());

        Assert.True(recovered.Session.Interrupted);
        Assert.Null(await restarted.GetActive());
    }

    [Fact]
    public async Task Operations_run_on_the_supplied_queue()
    {
        var queued = 0;
        _controller.UseQueue(work =>
        {
            queued++;
            work();
            return Task.CompletedTask;
        });

        await _controller.StartNewSession();
        await _controller.ListSessions();

        Assert.Equal(2, queued);
    }

    [Fact]
    public async Task End_session_closes_it_and_the_next_capture_starts_a_new_one()
    {
        Copy(Fixture.Waystone, A);
        var first = (await _controller.GetActive())!.Id;
        _time.Advance(TimeSpan.FromMinutes(5));

        await _controller.EndSession();

        Assert.Null(await _controller.GetActive());
        Assert.Null(_states[^1]);
        var ended = Assert.Single(_store.ListSessions()).Session;
        Assert.Equal((first, _time.Now, false), (ended.Id, ended.EndedAt, ended.Interrupted));

        Copy(Fixture.Waystone, A);
        Assert.NotEqual(first, (await _controller.GetActive())!.Id);
        Assert.Equal(2, _store.ListSessions().Count);
    }

    [Fact]
    public async Task End_session_without_a_recording_session_does_nothing()
    {
        await _controller.EndSession();

        Assert.Empty(_store.ListSessions());
        Assert.Empty(_states);
    }

    // --- kinds and continue (MAPPING_PLAN.md M0) --------------------------------------------------

    [Fact]
    public async Task New_session_takes_name_and_kind_and_the_hotkey_keeps_the_kind()
    {
        var id = await _controller.StartNewSession("farming rituals 4", SessionKind.Mapping);

        var active = (await _controller.GetActive())!;
        Assert.Equal((id, "farming rituals 4", SessionKind.Mapping), (active.Id, active.Name, active.Kind));

        _time.Advance(TimeSpan.FromMinutes(1));
        _controller.OnHotkey(new HotkeyPress(HotkeyAction.NewSession, A));
        Assert.Equal(SessionKind.Mapping, (await _controller.GetActive())!.Kind);
        Assert.NotNull(_store.GetSession(id)!.EndedAt);
    }

    [Fact]
    public async Task A_capture_with_no_recording_session_starts_a_free_recording()
    {
        await _controller.StartNewSession(kind: SessionKind.Mapping);
        await _controller.EndSession();

        Copy(Fixture.Waystone, A);

        Assert.Equal(SessionKind.FreeRecording, (await _controller.GetActive())!.Kind);
    }

    [Fact]
    public async Task Continue_ends_the_recording_session_and_captures_go_to_the_continued_one()
    {
        var first = await _controller.StartNewSession("first", SessionKind.Mapping);
        Copy(Fixture.Waystone, A);
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await _controller.StartNewSession("second");
        Copy(Fixture.BodyArmourRare, B);

        await _controller.ContinueSession(first);

        var active = (await _controller.GetActive())!;
        Assert.Equal((first, SessionKind.Mapping, 1, false), (active.Id, active.Kind, active.ItemCount, active.CanUndo));
        Assert.Equal(_time.Now, _store.GetSession(second)!.EndedAt);
        Assert.Equal((FeedbackKind.NewSession, "Recording: first"), (_feedback[^1].Kind, _feedback[^1].Message));
        Assert.Equal(first, _states[^1]!.Id);

        Copy(Fixture.Waystone, A); // already recorded in the continued session
        Copy(Fixture.ChanceShard, B);
        Assert.Equal(FeedbackKind.AlreadyRecorded, _feedback[^2].Kind);
        Assert.Equal((1, 1), (_store.LoadSession(first)!.Items.Count, _store.LoadSession(first)!.Stackables.Count));
        Assert.Single(_store.LoadSession(second)!.Items);

        await _controller.Undo();
        Assert.Empty(_store.LoadSession(first)!.Stackables);
    }

    [Fact]
    public async Task Continue_of_the_recording_session_does_nothing_and_of_a_missing_one_throws()
    {
        var id = await _controller.StartNewSession();
        var states = _states.Count;

        await _controller.ContinueSession(id);

        Assert.Equal(states, _states.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ContinueSession(Guid.NewGuid()));
        Assert.Equal(id, (await _controller.GetActive())!.Id);
    }

    // --- the seam ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_copy_of_item_text_is_published_after_it_is_stored()
    {
        var published = new List<ProcessedCapture>();
        _controller.CaptureProcessed += c =>
        {
            Assert.NotNull(_store.GetSession(c.SessionId)); // stored before publishing
            published.Add(c);
        };
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);

        Copy(Fixture.Verisium, A);
        Copy(Fixture.Verisium, A); // changes nothing: published, not logged
        Copy(Fixture.Waystone, B);
        _controller.OnItemCopied(new CopiedItem("not an item", A, _time.Now, TimeSpan.Zero));

        Assert.Equal(3, published.Count);
        Assert.All(published, c => Assert.Equal((id, SessionKind.Mapping), (c.SessionId, c.SessionKind)));
        var log = _store.LoadSession(id)!.Captures;
        Assert.Equal([log[0].Id, null, log[1].Id], published.Select(c => c.CaptureId));
        Assert.Equal(log[0].CapturedAt, published[0].CapturedAt);
        var stack = published[0].Stack!;
        Assert.Equal(("Verisium", 31, 1000), (stack.Name, stack.Count, stack.MaxStack));
        Assert.Equal(log[0].IdentityKey, stack.Key);
        Assert.Null(published[2].Stack);
        Assert.Equal((B, log[1].RawText), (published[2].Position, published[2].RawText));
        Assert.All(published, c => Assert.Null(c.Cell));
    }

    [Fact]
    public async Task Published_captures_carry_the_sweep_cell()
    {
        var published = new List<ProcessedCapture>();
        _controller.CaptureProcessed += published.Add;
        var sweep = await _controller.StartSweep(new GridKind("Inventory", 12, 5), new GridGeometry(400, 200, 100, 12, 5));

        Copy(Fixture.ChanceShard, new ScreenPoint(650, 350));

        var capture = Assert.Single(published);
        Assert.Equal((sweep.Id, new GridCell(2, 1)), (capture.SweepId, capture.Cell));
    }

    [Fact]
    public async Task Sweeps_starting_and_ending_are_published()
    {
        var published = new List<SweepChanged>();
        _controller.SweepChanged += published.Add;
        var id = await _controller.StartNewSession(kind: SessionKind.Mapping);
        var grid = new GridGeometry(400, 200, 100, 12, 5);

        var first = await _controller.StartSweep(new GridKind("Inventory", 12, 5), grid);
        _time.Advance(TimeSpan.FromSeconds(5));
        var second = await _controller.StartSweep(new GridKind("Inventory", 12, 5), grid); // finishes the first
        await _controller.CancelSweep();

        Assert.Equal(
            [(first.Id, SweepStatus.Active), (first.Id, SweepStatus.Finished), (second.Id, SweepStatus.Active), (second.Id, SweepStatus.Canceled)],
            published.Select(p => (p.SweepId, p.Status)));
        Assert.All(published, p => Assert.Equal((id, SessionKind.Mapping), (p.SessionId, p.SessionKind)));
        Assert.Equal(_time.Now, published[1].At);
    }

    [Fact]
    public void A_failing_subscriber_does_not_fail_the_capture()
    {
        var reached = 0;
        _controller.CaptureProcessed += _ => throw new InvalidOperationException("boom");
        _controller.CaptureProcessed += _ => reached++;

        Copy(Fixture.Waystone, A);

        Assert.Equal(1, reached);
        Assert.Equal(FeedbackKind.Added, _feedback[^1].Kind);
    }
}
