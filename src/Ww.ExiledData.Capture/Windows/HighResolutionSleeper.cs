using System.Runtime.Versioning;
using static Ww.ExiledData.Capture.Windows.NativeMethods;

namespace Ww.ExiledData.Capture.Windows;

/// <summary>
/// Sub-millisecond sleeps via a high-resolution waitable timer (Windows 10 1803+), without raising the
/// system-wide timer resolution. Falls back to <see cref="Thread.Sleep(TimeSpan)"/> (~15 ms granularity).
/// Use from one thread at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class HighResolutionSleeper : ISleeper, IDisposable
{
    private nint _timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

    public bool IsHighResolution => _timer != 0;

    public void Sleep(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        long dueTime = -duration.Ticks; // negative = relative, in 100 ns units (= TimeSpan ticks)
        if (_timer != 0 && SetWaitableTimer(_timer, dueTime, 0, 0, 0, false))
            WaitForSingleObject(_timer, INFINITE);
        else
            Thread.Sleep(duration);
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }
    }
}
