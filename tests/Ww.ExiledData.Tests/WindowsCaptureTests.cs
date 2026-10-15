using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Capture.Windows;

namespace Ww.ExiledData.Tests;

/// <summary>
/// Touches the real Win32 APIs. The hotkey test injects Ctrl+Shift+F23 into the current desktop session;
/// the registered hotkey consumes F23, so no application receives it.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsCaptureTests
{
    [Fact]
    public void High_resolution_sleeper_is_much_finer_than_default_timer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var sleeper = new HighResolutionSleeper();
        Assert.True(sleeper.IsHighResolution);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 25; i++)
            sleeper.Sleep(TimeSpan.FromMilliseconds(4));
        var average = sw.Elapsed / 25;

        Assert.InRange(average.TotalMilliseconds, 3.5, 10); // Thread.Sleep would be ~15.6 ms
    }

    [Fact]
    public void Clipboard_sequence_number_is_available()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.NotEqual(0u, new WindowsClipboard().SequenceNumber);
    }

    [Fact]
    public async Task Input_source_installs_hook_and_receives_registered_hotkey()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var input = new WindowsInputSource();
        var pressed = new TaskCompletionSource<HotkeyPress>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.HotkeyPressed += a => pressed.TrySetResult(a);

        var failed = input.Start(new Dictionary<HotkeyAction, Hotkey> { [HotkeyAction.Redo] = Hotkey.Parse("Ctrl+Shift+F23") });
        Assert.Empty(failed);
        Assert.Throws<InvalidOperationException>(() => new WindowsInputSource().Start(new Dictionary<HotkeyAction, Hotkey>()));

        SendKeys(0x11, 0x10, 0x86); // VK_CONTROL, VK_SHIFT, VK_F23

        Assert.Equal(HotkeyAction.Redo, (await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Action);
    }

    [Fact]
    public async Task Input_source_reports_grid_keys_only_while_grid_editing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var input = new WindowsInputSource();
        var keys = new List<GridKeyPress>();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input.GridKeyPressed += k =>
        {
            lock (keys)
                keys.Add(k);
            got.TrySetResult();
        };
        input.Start(new Dictionary<HotkeyAction, Hotkey>());

        SendKeys(0x25); // VK_LEFT, not editing: ignored (and passed on)
        input.GridEditing = true;
        SendKeys(0x10, 0x25); // Shift+Left, swallowed

        await got.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        input.GridEditing = false;
        lock (keys)
        {
            var key = Assert.Single(keys);
            Assert.Equal((GridKey.Left, true, false), (key.Key, key.Shift, key.Ctrl));
        }
    }

    private static void SendKeys(params ushort[] vks)
    {
        var inputs = vks.Select(vk => Key(vk, up: false)).Concat(vks.Reverse().Select(vk => Key(vk, up: true))).ToArray();
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()));
    }

    private static INPUT Key(ushort vk, bool up) =>
        new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x2u : 0u } } };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // largest member; sets the union size
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}
