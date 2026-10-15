using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ww.ExiledData.Core.Identity;
using static Ww.ExiledData.Capture.Windows.NativeMethods;

namespace Ww.ExiledData.Capture.Windows;

/// <summary>
/// Global input on a dedicated message-loop thread: a low-level keyboard hook that observes (never blocks)
/// plain Ctrl+C, and <c>RegisterHotKey</c> hotkeys, which Windows consumes so the game doesn't see them. While the
/// grid editor is open, a low-level mouse hook also takes left presses on the editor's chrome away from the game.
/// Only one instance can be started per process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsInputSource(TimeProvider? time = null) : IInputSource
{
    private static WindowsInputSource? s_active;

    /// <summary>Posted to the input thread: wParam 1 unregisters the hotkeys, 0 registers them again.</summary>
    private const uint WM_SUSPEND_HOTKEYS = WM_APP + 1;

    /// <summary>Posted to the input thread: wParam 1 installs the mouse hook (grid editing), 0 removes it.</summary>
    private const uint WM_GRID_MOUSE = WM_APP + 2;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<int, HotkeyAction> _hotkeyIds = [];
    private IReadOnlyDictionary<HotkeyAction, Hotkey> _hotkeys = new Dictionary<HotkeyAction, Hotkey>();
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private nint _mouseHook;
    private bool _dragging;
    private volatile IReadOnlyList<ScreenRect> _gridChrome = [];
    private bool _cDown;
    private volatile bool _gridEditing;
    private volatile bool _hotkeysSuspended;

    public event Action<CopyPress>? CopyPressed;
    public event Action<HotkeyPress>? HotkeyPressed;
    public event Action<GridKeyPress>? GridKeyPressed;
    public event Action<GridMousePress>? GridMousePressed;

    // The mouse hook is installed only while editing: a low-level mouse hook sits in front of every mouse event.
    public bool GridEditing
    {
        get => _gridEditing;
        set
        {
            if (_gridEditing == value)
                return;
            _gridEditing = value;
            if (_thread is { IsAlive: true })
                PostThreadMessageW(_threadId, WM_GRID_MOUSE, value ? 1u : 0u, 0);
        }
    }

    public IReadOnlyList<ScreenRect> GridChrome
    {
        get => _gridChrome;
        set => _gridChrome = value;
    }

    // RegisterHotKey binds a hotkey to the registering thread, so the input thread does the (un)registering.
    public bool HotkeysSuspended
    {
        get => _hotkeysSuspended;
        set
        {
            if (_hotkeysSuspended == value)
                return;
            _hotkeysSuspended = value;
            if (_thread is { IsAlive: true })
                PostThreadMessageW(_threadId, WM_SUSPEND_HOTKEYS, value ? 1u : 0u, 0);
        }
    }

    public IReadOnlyList<HotkeyAction> Start(IReadOnlyDictionary<HotkeyAction, Hotkey> hotkeys)
    {
        if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
            throw new InvalidOperationException("A WindowsInputSource is already running in this process.");

        var failed = new List<HotkeyAction>();
        Exception? startError = null;
        using var started = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                PeekMessageW(out _, 0, 0, 0, PM_NOREMOVE); // create the queue so WM_QUIT can be posted

                _hook = SetWindowsHookExW(WH_KEYBOARD_LL, &HookProc, GetModuleHandleW(null), 0);
                if (_hook == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowsHookEx failed.");

                _hotkeys = hotkeys;
                failed.AddRange(RegisterHotkeys());
                if (_gridEditing)
                    SetMouseHook(true);
            }
            catch (Exception ex)
            {
                startError = ex;
            }
            started.Set();
            if (startError is null)
                MessageLoop();
        })
        { IsBackground = true, Name = "Ww input" };

        _thread.Start();
        started.Wait();
        if (startError is not null)
        {
            Dispose();
            throw startError;
        }
        return failed;
    }

    public void Dispose()
    {
        if (_thread is { IsAlive: true })
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join();
        }
        _thread = null;
        Interlocked.CompareExchange(ref s_active, null, this);
    }

    private void MessageLoop()
    {
        try
        {
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && _hotkeyIds.TryGetValue((int)msg.wParam, out var action))
                {
                    GetPhysicalCursorPos(out var pt);
                    HotkeyPressed?.Invoke(new HotkeyPress(action, new ScreenPoint(pt.X, pt.Y)));
                }
                else if (msg.message == WM_SUSPEND_HOTKEYS)
                {
                    UnregisterHotkeys();
                    if (msg.wParam == 0)
                        RegisterHotkeys();
                }
                else if (msg.message == WM_GRID_MOUSE)
                {
                    SetMouseHook(msg.wParam == 1);
                }
            }
        }
        finally
        {
            UnregisterHotkeys();
            SetMouseHook(false);
            if (_hook != 0)
                UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    /// <summary>Input thread only. A failure to install is not fatal: the editor still works with the keys.</summary>
    private void SetMouseHook(bool on)
    {
        if (on && _mouseHook == 0)
        {
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, &MouseHookProc, GetModuleHandleW(null), 0);
        }
        else if (!on && _mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            _dragging = false;
        }
    }

    /// <summary>Input thread only. Returns the hotkeys that could not be registered.</summary>
    private List<HotkeyAction> RegisterHotkeys()
    {
        var failed = new List<HotkeyAction>();
        var id = 1;
        foreach (var (action, hotkey) in _hotkeys)
        {
            if (RegisterHotKey(0, id, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
                _hotkeyIds[id++] = action;
            else
                failed.Add(action);
        }
        return failed;
    }

    /// <summary>Input thread only.</summary>
    private void UnregisterHotkeys()
    {
        foreach (var id in _hotkeyIds.Keys)
            UnregisterHotKey(0, id);
        _hotkeyIds.Clear();
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nuint wParam, nint lParam)
    {
        var self = s_active;
        if (nCode == HC_ACTION && self is not null)
        {
            try
            {
                if (self.OnKey((uint)wParam, ((KBDLLHOOKSTRUCT*)lParam)->vkCode))
                    return 1; // swallowed: grid editor key
            }
            catch
            {
                // Never let an exception cross the native boundary; keep the hook alive.
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint MouseHookProc(int nCode, nuint wParam, nint lParam)
    {
        var self = s_active;
        if (nCode == HC_ACTION && self is not null)
        {
            try
            {
                if (self.OnMouse((uint)wParam))
                    return 1; // swallowed: a press on the grid editor's chrome
            }
            catch
            {
                // Never let an exception cross the native boundary; keep the hook alive.
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    // Runs inside the hook: keep it fast. Only the left button is looked at; moves always reach the game (the editor
    // follows the cursor itself). A press on the chrome and its matching release are swallowed, so the game never
    // sees a click there (it would pick up or drop an item).
    private bool OnMouse(uint message)
    {
        if (!_gridEditing)
            return false;
        if (message == WM_LBUTTONDOWN)
        {
            GetPhysicalCursorPos(out var at);
            var cursor = new ScreenPoint(at.X, at.Y);
            foreach (var rect in _gridChrome)
            {
                if (!rect.Contains(cursor))
                    continue;
                _dragging = true;
                GridMousePressed?.Invoke(new GridMousePress(true, cursor));
                return true;
            }
            return false;
        }
        if (message == WM_LBUTTONUP && _dragging)
        {
            _dragging = false;
            GetPhysicalCursorPos(out var at);
            GridMousePressed?.Invoke(new GridMousePress(false, new ScreenPoint(at.X, at.Y)));
            return true;
        }
        return false;
    }

    // Runs inside the hook: keep it fast (Windows drops slow LL hooks). Returns true to swallow the key.
    private bool OnKey(uint message, uint vk)
    {
        if (_gridEditing && GridKeyOf(vk) is { } gridKey)
        {
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN) // auto-repeat included: holding an arrow keeps nudging
            {
                var withShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                var withCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                GetPhysicalCursorPos(out var at);
                GridKeyPressed?.Invoke(new GridKeyPress(gridKey, withShift, withCtrl, new ScreenPoint(at.X, at.Y)));
            }
            return true;
        }

        if (vk != VK_C)
            return false;

        if (message is WM_KEYUP or WM_SYSKEYUP)
        {
            _cDown = false;
            return false;
        }
        if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN) || _cDown)
            return false; // auto-repeat while held

        _cDown = true;
        var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        if (!ctrl || alt)
            return false; // plain Ctrl+C only; Ctrl+Alt+C (and AltGr+C) are ignored

        GetPhysicalCursorPos(out var pt);
        var press = new CopyPress(new ScreenPoint(pt.X, pt.Y), GetClipboardSequenceNumber(), ForegroundTitle(), _time.GetUtcNow());
        CopyPressed?.Invoke(press);
        return false; // observed only: the game must still see its Ctrl+C
    }

    private static GridKey? GridKeyOf(uint vk) => vk switch
    {
        VK_LEFT => GridKey.Left,
        VK_RIGHT => GridKey.Right,
        VK_UP => GridKey.Up,
        VK_DOWN => GridKey.Down,
        VK_RETURN => GridKey.Confirm,
        VK_ESCAPE => GridKey.Cancel,
        VK_SPACE => GridKey.Corner,
        _ => null,
    };

    private static string? ForegroundTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return null;
        // Top-level window of another process: Windows returns the cached caption without messaging it.
        var buffer = stackalloc char[256];
        var length = GetWindowTextW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : null;
    }
}
