using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;

namespace Ww.ExiledData.Tests;

public sealed class CaptureServiceTests : IDisposable
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeInputSource _input = new();
    private readonly FakeClipboard _clipboard;
    private readonly FakeSleeper _sleeper;
    private readonly RecordingSink _sink = new();
    private readonly CaptureService _service;

    public CaptureServiceTests()
    {
        _clipboard = new FakeClipboard(_time);
        _sleeper = new FakeSleeper(_time);
        _service = new CaptureService(new CaptureOptions(), _input, _clipboard, _sleeper, _sink, _time);
        _service.Start();
    }

    public void Dispose() => _service.Dispose();

    private async Task<object> PressAndWait(CopyPress? press = null)
    {
        _input.Press(press ?? CopyPresses.InGame(_clipboard.SequenceNumber, _time.Now));
        await _service.Enqueue(() => { });
        return _sink.Events[^1];
    }

    [Fact]
    public async Task Delivers_item_text_with_position_and_delay()
    {
        var text = Fixture.Load(Fixture.Waystone);
        _clipboard.WriteAt(9 * Ms, text);

        var item = Assert.IsType<CopiedItem>(await PressAndWait(CopyPresses.InGame(_clipboard.SequenceNumber, _time.Now, 12, 34)));

        Assert.Equal(text, item.Text);
        Assert.Equal(new ScreenPoint(12, 34), item.Position);
        Assert.Equal(12 * Ms, item.ClipboardDelay); // polled at 0, 4, 8, 12 ms
    }

    [Fact]
    public async Task Ignores_copy_outside_game_without_waiting()
    {
        var press = new CopyPress(default, _clipboard.SequenceNumber, "Notepad", _time.Now);

        Assert.Equal(CopyIgnoreReason.NotGameWindow, await PressAndWait(press));
        Assert.Equal(0, _sleeper.Sleeps);
    }

    [Fact]
    public async Task Any_window_accepted_when_title_filter_is_off()
    {
        using var service = new CaptureService(new CaptureOptions { GameWindowTitle = null }, _input, _clipboard, _sleeper, _sink, _time);
        _service.Dispose(); // detach the default service from the shared input
        service.Start();
        _clipboard.WriteAt(Ms, Fixture.Load(Fixture.Waystone));

        _input.Press(new CopyPress(default, _clipboard.SequenceNumber, null, _time.Now));
        await service.Enqueue(() => { });

        Assert.IsType<CopiedItem>(Assert.Single(_sink.Events));
    }

    [Fact]
    public void Hotkeys_are_held_only_for_the_game_window()
    {
        var window = _input.HotkeyWindow!;

        Assert.True(window("Path of Exile 2"));
        Assert.True(window("path of exile 2 (DX12)"));
        Assert.False(window("Google Chrome"));
        Assert.False(window(null));
    }

    [Fact]
    public void Hotkeys_are_held_for_any_window_when_title_filter_is_off()
    {
        using var service = new CaptureService(new CaptureOptions { GameWindowTitle = null }, _input, _clipboard, _sleeper, _sink, _time);
        service.Start();

        Assert.True(_input.HotkeyWindow!("Google Chrome"));
    }

    [Fact]
    public async Task Times_out_when_clipboard_does_not_change()
    {
        Assert.Equal(CopyIgnoreReason.ClipboardTimeout, await PressAndWait());
        Assert.True(_time.Now - new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero) >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Rejects_non_item_text()
    {
        _clipboard.WriteAt(Ms, "hello from chat");

        Assert.Equal(CopyIgnoreReason.NotItemText, await PressAndWait());
    }

    [Fact]
    public async Task Retries_while_clipboard_is_locked()
    {
        _clipboard.WriteAt(Ms, Fixture.Load(Fixture.Waystone));
        _clipboard.BusyUntil = _time.Now + 20 * Ms;

        Assert.IsType<CopiedItem>(await PressAndWait());
    }

    [Fact]
    public async Task Reports_unreadable_when_locked_past_timeout()
    {
        _clipboard.WriteAt(Ms, Fixture.Load(Fixture.Waystone));
        _clipboard.BusyUntil = _time.Now + TimeSpan.FromSeconds(1);

        Assert.Equal(CopyIgnoreReason.ClipboardUnreadable, await PressAndWait());
    }

    [Fact]
    public async Task Identical_recopy_is_detected_by_sequence_number()
    {
        var text = Fixture.Load(Fixture.Waystone);
        _clipboard.WriteAt(Ms, text);
        await PressAndWait();
        _clipboard.WriteAt(Ms, text);

        Assert.IsType<CopiedItem>(await PressAndWait());
        Assert.Equal(2, _sink.Events.Count);
    }

    [Fact]
    public async Task Overlapping_presses_never_consume_the_same_write_twice()
    {
        var baseline = _clipboard.SequenceNumber;
        _clipboard.WriteAt(Ms, Fixture.Load(Fixture.Waystone));

        // Second press arrives before the first is handled, with the same baseline.
        _input.Press(CopyPresses.InGame(baseline, _time.Now));
        _input.Press(CopyPresses.InGame(baseline, _time.Now));
        await _service.Enqueue(() => { });

        Assert.IsType<CopiedItem>(_sink.Events[0]);
        Assert.Equal(CopyIgnoreReason.ClipboardTimeout, _sink.Events[1]);
    }

    [Fact]
    public async Task Hotkeys_and_enqueued_work_run_in_order_on_the_queue()
    {
        _clipboard.WriteAt(Ms, Fixture.Load(Fixture.Waystone));
        _input.Press(CopyPresses.InGame(_clipboard.SequenceNumber, _time.Now));
        _input.Hotkey(HotkeyAction.Undo);
        var order = new List<string>();

        await _service.Enqueue(() => order.Add($"ui after {_sink.Events.Count}"));

        Assert.Equal(["ui after 2"], order);
        Assert.Equal(HotkeyAction.Undo, _sink.Events[1]);
    }

    [Fact]
    public async Task Sink_exception_is_reported_and_queue_keeps_running()
    {
        var errors = new List<Exception>();
        _service.Error += errors.Add;
        _sink.OnNext = () => throw new InvalidOperationException("boom");

        _input.Hotkey(HotkeyAction.NewSession);
        _input.Hotkey(HotkeyAction.Redo);
        await _service.Enqueue(() => { });

        Assert.Equal("boom", Assert.Single(errors).Message);
        Assert.Equal([HotkeyAction.NewSession, HotkeyAction.Redo], _sink.Events.Cast<HotkeyAction>());
    }

    [Fact]
    public void Copy_pressed_is_raised_immediately_for_feedback()
    {
        CopyPress? seen = null;
        _service.CopyPressed += p => seen = p;
        var press = CopyPresses.InGame(1, _time.Now);

        _input.Press(press);

        Assert.Same(press, seen);
    }

    [Fact]
    public void Copy_pressed_is_not_raised_outside_the_game()
    {
        var raised = false;
        _service.CopyPressed += _ => raised = true;

        _input.Press(new CopyPress(default, 1, "Visual Studio Code", _time.Now));

        Assert.False(raised);
    }

    [Fact]
    public async Task Enqueue_after_dispose_is_canceled()
    {
        _service.Dispose();

        Assert.True(_input.Disposed);
        await Assert.ThrowsAsync<TaskCanceledException>(() => _service.Enqueue(() => { }));
    }
}
