using Ww.ExiledData.Core.Identity;

namespace Ww.ExiledData.Capture;

public sealed record CaptureOptions
{
    /// <summary>How long to wait for the game to put the item text on the clipboard (PLAN §1). Returns as soon as it changes, so a longer value only delays the "nothing captured" result.</summary>
    public TimeSpan ClipboardTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(4);
    /// <summary>Copies are only taken when the foreground window title contains this; null accepts any window.</summary>
    public string? GameWindowTitle { get; init; } = "Path of Exile 2";

    public IReadOnlyDictionary<HotkeyAction, Hotkey> Hotkeys { get; init; } = new Dictionary<HotkeyAction, Hotkey>
    {
        [HotkeyAction.NewSession] = Hotkey.Parse("Ctrl+Alt+N"),
        [HotkeyAction.Undo] = Hotkey.Parse("Ctrl+Alt+Z"),
        [HotkeyAction.Redo] = Hotkey.Parse("Ctrl+Alt+Y"),
        [HotkeyAction.SweepStart] = Hotkey.Parse("Ctrl+Alt+G"),
        [HotkeyAction.SweepFinish] = Hotkey.Parse("Ctrl+Alt+F"),
        [HotkeyAction.SkipCell] = Hotkey.Parse("Ctrl+Alt+K"),
        [HotkeyAction.MapStashScan] = Hotkey.Parse("Ctrl+Alt+D"),
        [HotkeyAction.MapSwipe] = Hotkey.Parse("Ctrl+Alt+W"),
        [HotkeyAction.MapFinalScan] = Hotkey.Parse("Ctrl+Alt+Q"),
        [HotkeyAction.MapStartRun] = Hotkey.Parse("Ctrl+Alt+R"),
        [HotkeyAction.MapEndRun] = Hotkey.Parse("Ctrl+Alt+T"),
    };
}

/// <summary>A registered hotkey went down; <paramref name="Cursor"/> is where the mouse was (physical pixels).</summary>
public sealed record HotkeyPress(HotkeyAction Action, ScreenPoint Cursor);

public enum GridKey
{
    Left,
    Right,
    Up,
    Down,
    Confirm,
    Cancel,
    /// <summary>Space: sets a grid corner at the cursor.</summary>
    Corner,
}

/// <summary>
/// A key pressed while the grid editor is open (arrows nudge, Space sets a corner at <paramref name="Cursor"/>,
/// Enter confirms, Escape cancels). The cursor is in physical pixels.
/// </summary>
public sealed record GridKeyPress(GridKey Key, bool Shift, bool Ctrl, ScreenPoint Cursor = default);

/// <summary>A screen rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public bool Contains(ScreenPoint p) => p.X >= X && p.X < X + Width && p.Y >= Y && p.Y < Y + Height;
}

/// <summary>
/// The left button went down on the grid editor's chrome (<see cref="IInputSource.GridChrome"/>), or came up after
/// such a press. Both are kept from the game. <paramref name="Cursor"/> is in physical pixels.
/// </summary>
public sealed record GridMousePress(bool Down, ScreenPoint Cursor);

/// <summary>State snapshotted at the instant plain Ctrl+C went down, before the game handles it.</summary>
public sealed record CopyPress(ScreenPoint Position, uint ClipboardSequence, string? ForegroundTitle, DateTimeOffset At);

public sealed record CopiedItem(string Text, ScreenPoint Position, DateTimeOffset PressedAt, TimeSpan ClipboardDelay);

public enum CopyIgnoreReason
{
    /// <summary>Ctrl+C outside the game; usually not worth any feedback.</summary>
    NotGameWindow,
    ClipboardTimeout,
    ClipboardUnreadable,
    NotItemText,
}

/// <summary>Receives capture results. All calls happen one at a time on the capture queue thread.</summary>
public interface ICaptureSink
{
    void OnItemCopied(CopiedItem item);
    void OnCopyIgnored(CopyPress press, CopyIgnoreReason reason);
    void OnHotkey(HotkeyPress press);
}

/// <summary>Global keyboard input. Events are raised on the input thread and must return immediately.</summary>
public interface IInputSource : IDisposable
{
    event Action<CopyPress>? CopyPressed;
    event Action<HotkeyPress>? HotkeyPressed;
    event Action<GridKeyPress>? GridKeyPressed;
    event Action<GridMousePress>? GridMousePressed;

    /// <summary>
    /// While true, Space, arrows, Enter and Escape are reported through <see cref="GridKeyPressed"/> and kept from other
    /// apps (the game keeps focus while the grid editor is open, and Enter/Escape there would open chat/menu). Left
    /// presses on <see cref="GridChrome"/> are reported through <see cref="GridMousePressed"/> and kept from the game.
    /// </summary>
    bool GridEditing { get; set; }

    /// <summary>Where a left press starts a grid editor drag (the title and the handles), physical pixels.</summary>
    IReadOnlyList<ScreenRect> GridChrome { get; set; }

    /// <summary>
    /// While true, the hotkeys are unregistered, so their key presses reach the focused window (the Settings key
    /// recorder) instead of firing.
    /// </summary>
    bool HotkeysSuspended { get; set; }

    /// <summary>Starts listening; returns the hotkeys that could not be registered (taken by another app).</summary>
    IReadOnlyList<HotkeyAction> Start(IReadOnlyDictionary<HotkeyAction, Hotkey> hotkeys);
}

public interface IClipboard
{
    /// <summary>Changes on every clipboard write, even of identical text.</summary>
    uint SequenceNumber { get; }

    /// <summary>False when the clipboard is busy (retry). True with null text when it holds no text.</summary>
    bool TryGetText(out string? text);
}

public interface ISleeper
{
    void Sleep(TimeSpan duration);
}
