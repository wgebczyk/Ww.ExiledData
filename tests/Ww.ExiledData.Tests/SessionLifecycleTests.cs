using Ww.ExiledData.Core.Sessions;

namespace Ww.ExiledData.Tests;

public class SessionLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starting_new_session_closes_previous_at_same_instant()
    {
        var first = SessionLifecycle.Start(null, T0);
        var second = SessionLifecycle.Start(first, T0.AddMinutes(7));

        Assert.Equal(second.StartedAt, first.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(7), first.Duration(T0.AddHours(1)));
        Assert.True(second.IsActive);
        Assert.False(first.Interrupted);
    }

    [Fact]
    public void Interrupted_session_closes_at_last_capture()
    {
        var s = SessionLifecycle.Start(null, T0);

        SessionLifecycle.CloseInterrupted(s, T0.AddMinutes(3));

        Assert.Equal(T0.AddMinutes(3), s.EndedAt);
        Assert.True(s.Interrupted);
    }

    [Fact]
    public void Interrupted_session_without_captures_closes_at_start()
    {
        var s = SessionLifecycle.Start(null, T0);

        SessionLifecycle.CloseInterrupted(s, null);

        Assert.Equal(T0, s.EndedAt);
        Assert.True(s.Interrupted);
    }
}
