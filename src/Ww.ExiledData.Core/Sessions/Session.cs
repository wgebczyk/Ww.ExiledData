namespace Ww.ExiledData.Core.Sessions;

/// <summary>What a session records (MAPPING_PLAN.md 2). Stored as an integer: never renumber.</summary>
public enum SessionKind
{
    /// <summary>Captures, stackables and sweeps only (the original sessions).</summary>
    FreeRecording = 0,
    /// <summary>Map runs with setups, scans and loot (the mapping module).</summary>
    Mapping = 1,
}

public sealed class Session
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public SessionKind Kind { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool Interrupted { get; set; }

    public bool IsActive => EndedAt is null;
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;
}

public static class SessionLifecycle
{
    /// <summary>Starts a new session, closing <paramref name="previous"/> (if still active) at the same instant.</summary>
    public static Session Start(Session? previous, DateTimeOffset now, string? name = null, SessionKind kind = SessionKind.FreeRecording)
    {
        End(previous, now);

        return new Session
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName(now) : name.Trim(),
            Kind = kind,
            StartedAt = now,
        };
    }

    /// <summary>The name a session gets when none is given: its local start time.</summary>
    public static string DefaultName(DateTimeOffset startedAt) => startedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Ends <paramref name="session"/> at <paramref name="now"/>, if it is still active.</summary>
    public static void End(Session? session, DateTimeOffset now)
    {
        if (session is { IsActive: true })
            session.EndedAt = now;
    }

    /// <summary>
    /// Continues an ended session: it becomes active again (MAPPING_PLAN.md 3.1, ended sessions are no longer
    /// read-only). Its start stays, so the duration includes the pause. The interrupted flag is cleared, since the
    /// session gets a new end.
    /// </summary>
    public static void Continue(Session session)
    {
        if (session.IsActive)
            throw new InvalidOperationException($"Session '{session.Name}' is already active.");
        session.EndedAt = null;
        session.Interrupted = false;
    }

    /// <summary>
    /// Recovery on launch: a session left active by an app exit is closed at its last capture
    /// (or its start, if it has none) and marked interrupted.
    /// </summary>
    public static void CloseInterrupted(Session session, DateTimeOffset? lastCaptureAt)
    {
        if (!session.IsActive)
            return;
        session.EndedAt = lastCaptureAt ?? session.StartedAt;
        session.Interrupted = true;
    }
}
