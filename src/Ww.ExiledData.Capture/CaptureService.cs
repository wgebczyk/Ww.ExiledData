using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Capture;

/// <summary>
/// Turns global Ctrl+C presses into copied item texts and forwards hotkeys. Clipboard waits, sink calls and
/// any <see cref="Enqueue"/>d work (e.g. undo from a UI button) share one serial queue, so they never race.
/// Owns the input source and sleeper and disposes them.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly CaptureOptions _options;
    private readonly IInputSource _input;
    private readonly ICaptureSink _sink;
    private readonly ISleeper _sleeper;
    private readonly ILogger _log;
    private readonly ClipboardWaiter _waiter;
    private readonly SerialQueue _queue = new("Ww capture queue");
    private uint _consumedSequence;

    public CaptureService(CaptureOptions options, IInputSource input, IClipboard clipboard, ISleeper sleeper, ICaptureSink sink,
        TimeProvider? time = null, ILogger<CaptureService>? logger = null)
    {
        _options = options;
        _input = input;
        _sink = sink;
        _sleeper = sleeper;
        _log = logger ?? NullLogger<CaptureService>.Instance;
        _waiter = new ClipboardWaiter(clipboard, sleeper, time ?? TimeProvider.System);
        _consumedSequence = clipboard.SequenceNumber;
    }

    /// <summary>
    /// Raised on the input thread the moment Ctrl+C goes down in the game window (e.g. to start feedback).
    /// Must return immediately.
    /// </summary>
    public event Action<CopyPress>? CopyPressed;

    /// <summary>Raised on the queue thread when the sink throws while handling a capture or hotkey.</summary>
    public event Action<Exception>? Error;

    /// <summary>Grid editor keys, raised on the input thread while <see cref="GridEditing"/> is on. Must return immediately.</summary>
    public event Action<GridKeyPress>? GridKeyPressed
    {
        add => _input.GridKeyPressed += value;
        remove => _input.GridKeyPressed -= value;
    }

    /// <summary>Grid editor drags, raised on the input thread while <see cref="GridEditing"/> is on. Must return immediately.</summary>
    public event Action<GridMousePress>? GridMousePressed
    {
        add => _input.GridMousePressed += value;
        remove => _input.GridMousePressed -= value;
    }

    /// <inheritdoc cref="IInputSource.GridEditing"/>
    public bool GridEditing
    {
        get => _input.GridEditing;
        set => _input.GridEditing = value;
    }

    /// <inheritdoc cref="IInputSource.GridChrome"/>
    public IReadOnlyList<ScreenRect> GridChrome
    {
        get => _input.GridChrome;
        set => _input.GridChrome = value;
    }

    /// <inheritdoc cref="IInputSource.HotkeysSuspended"/>
    public bool HotkeysSuspended
    {
        get => _input.HotkeysSuspended;
        set => _input.HotkeysSuspended = value;
    }

    /// <summary>Starts listening; returns the hotkeys that could not be registered.</summary>
    public IReadOnlyList<HotkeyAction> Start()
    {
        _input.CopyPressed += OnCopyPressed;
        _input.HotkeyPressed += OnHotkeyPressed;
        _log.LogInformation("Capture starting: timeout {Timeout} ms, poll {Poll} ms, window filter '{Title}', hotkeys {Hotkeys}",
            _options.ClipboardTimeout.TotalMilliseconds, _options.PollInterval.TotalMilliseconds, _options.GameWindowTitle,
            string.Join(", ", _options.Hotkeys.Select(h => $"{h.Key}={h.Value}")));

        var failed = _input.Start(_options.Hotkeys, IsGameTitle);
        foreach (var action in failed)
            _log.LogWarning("Hotkey {Hotkey} for {Action} could not be registered (in use by another app?)", _options.Hotkeys[action], action);
        return failed;
    }

    /// <summary>Runs <paramref name="work"/> on the capture queue, after everything queued before it.</summary>
    public Task Enqueue(Action work) => _queue.Post(work);

    public void Dispose()
    {
        _input.Dispose();
        _input.CopyPressed -= OnCopyPressed;
        _input.HotkeyPressed -= OnHotkeyPressed;
        _queue.Dispose();
        (_sleeper as IDisposable)?.Dispose();
        _log.LogInformation("Capture stopped");
    }

    /// <summary>Whether a window title is the game's; with no title set, any window is.</summary>
    private bool IsGameTitle(string? windowTitle) =>
        _options.GameWindowTitle is not { } title
        || windowTitle?.Contains(title, StringComparison.OrdinalIgnoreCase) == true;

    private bool IsGameWindow(CopyPress press) => IsGameTitle(press.ForegroundTitle);

    // Input thread: no logging or other slow work here.
    private void OnCopyPressed(CopyPress press)
    {
        _ = _queue.Post(() => Guard(() => HandleCopy(press)));
        if (IsGameWindow(press))
            CopyPressed?.Invoke(press);
    }

    private void OnHotkeyPressed(HotkeyPress press) =>
        _ = _queue.Post(() => Guard(() =>
        {
            _log.LogInformation("Hotkey {Action} at {Cursor}", press.Action, press.Cursor);
            _sink.OnHotkey(press);
        }));

    private void HandleCopy(CopyPress press)
    {
        _log.LogDebug("Ctrl+C at {Position}, clipboard seq {Seq}, window '{Title}', queued {Lag:F1} ms",
            press.Position, press.ClipboardSequence, press.ForegroundTitle, (DateTimeOffset.UtcNow - press.At).TotalMilliseconds);

        if (!IsGameWindow(press))
        {
            _log.LogDebug("Ignored: not the game window");
            _sink.OnCopyIgnored(press, CopyIgnoreReason.NotGameWindow);
            return;
        }

        var result = _waiter.Wait(press.ClipboardSequence, _consumedSequence, _options.ClipboardTimeout, _options.PollInterval);
        if (result.Status == ClipboardWaitStatus.Changed)
            _consumedSequence = result.Sequence;
        _log.LogDebug("Clipboard {Status} after {Elapsed:F1} ms (seq {Seq}, {Length} chars)",
            result.Status, result.Elapsed.TotalMilliseconds, result.Sequence, result.Text?.Length ?? 0);

        switch (result.Status)
        {
            case ClipboardWaitStatus.Timeout:
                _log.LogInformation("Nothing captured: clipboard did not change within {Timeout} ms", _options.ClipboardTimeout.TotalMilliseconds);
                _sink.OnCopyIgnored(press, CopyIgnoreReason.ClipboardTimeout);
                break;
            case ClipboardWaitStatus.Unreadable:
                _log.LogWarning("Nothing captured: clipboard changed but stayed locked");
                _sink.OnCopyIgnored(press, CopyIgnoreReason.ClipboardUnreadable);
                break;
            case ClipboardWaitStatus.Changed when !ItemText.IsItem(result.Text):
                _log.LogInformation("Nothing captured: clipboard text is not an item: {Text}", Truncate(result.Text));
                _sink.OnCopyIgnored(press, CopyIgnoreReason.NotItemText);
                break;
            default:
                _sink.OnItemCopied(new CopiedItem(result.Text!, press.Position, press.At, result.Elapsed));
                break;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Capture handling failed");
            Error?.Invoke(ex);
        }
    }

    private static string? Truncate(string? text) => text is { Length: > 200 } ? text[..200] + "…" : text;
}
