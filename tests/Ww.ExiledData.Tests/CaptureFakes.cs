using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;

namespace Ww.ExiledData.Tests;

internal sealed class FakeInputSource : IInputSource
{
    public event Action<CopyPress>? CopyPressed;
    public event Action<HotkeyPress>? HotkeyPressed;
    public event Action<GridKeyPress>? GridKeyPressed;
    public event Action<GridMousePress>? GridMousePressed;
    public bool GridEditing { get; set; }
    public IReadOnlyList<ScreenRect> GridChrome { get; set; } = [];
    public bool HotkeysSuspended { get; set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<HotkeyAction> Start(IReadOnlyDictionary<HotkeyAction, Hotkey> hotkeys) => [];
    public void Press(CopyPress press) => CopyPressed?.Invoke(press);
    public void Hotkey(HotkeyAction action, ScreenPoint cursor = default) => HotkeyPressed?.Invoke(new HotkeyPress(action, cursor));
    public void GridKey(GridKeyPress key) => GridKeyPressed?.Invoke(key);
    public void GridMouse(GridMousePress press) => GridMousePressed?.Invoke(press);
    public void Dispose() => Disposed = true;
}

/// <summary>Clipboard whose content changes are scheduled by elapsed fake time.</summary>
internal sealed class FakeClipboard(FakeTime time) : IClipboard
{
    private readonly List<(DateTimeOffset At, string? Text)> _writes = [];
    private (uint Seq, string? Text) _current = (100, "old text");

    public DateTimeOffset BusyUntil { get; set; } = DateTimeOffset.MinValue;

    public uint SequenceNumber
    {
        get
        {
            Apply();
            return _current.Seq;
        }
    }

    public void WriteAt(TimeSpan after, string? text) => _writes.Add((time.Now + after, text));

    public bool TryGetText(out string? text)
    {
        Apply();
        text = _current.Text;
        return time.Now >= BusyUntil;
    }

    private void Apply()
    {
        foreach (var w in _writes.Where(w => w.At <= time.Now).ToList())
        {
            _current = (_current.Seq + 1, w.Text);
            _writes.Remove(w);
        }
    }
}

internal sealed class FakeSleeper(FakeTime time) : ISleeper
{
    public int Sleeps { get; private set; }

    public void Sleep(TimeSpan duration)
    {
        Sleeps++;
        time.Advance(duration);
    }
}

internal sealed class RecordingSink : ICaptureSink
{
    public List<object> Events { get; } = [];
    public Action? OnNext { get; set; }

    public void OnItemCopied(CopiedItem item) => Record(item);
    public void OnCopyIgnored(CopyPress press, CopyIgnoreReason reason) => Record(reason);
    public void OnHotkey(HotkeyPress press) => Record(press.Action);

    private void Record(object e)
    {
        Events.Add(e);
        var next = OnNext;
        OnNext = null;
        next?.Invoke();
    }
}

internal static class CopyPresses
{
    public static CopyPress InGame(uint seq, DateTimeOffset at, int x = 500, int y = 300) =>
        new(new ScreenPoint(x, y), seq, "Path of Exile 2", at);
}
