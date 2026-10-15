namespace Ww.ExiledData.Core.Sweeps;

public enum SweepStatus
{
    Active,
    Finished,
    Canceled,
    /// <summary>The app exited while the sweep was running.</summary>
    Interrupted,
}

/// <summary>One manual pass over one grid (inventory or a stash tab) inside a session (PLAN §8).</summary>
public sealed class Sweep
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required GridKind Kind { get; init; }
    public required string Label { get; init; }
    /// <summary>Settable only to reposition a running sweep that has no cells yet (see SessionTracker.RepositionSweep).</summary>
    public required GridGeometry Grid { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public SweepStatus Status { get; set; } = SweepStatus.Active;
    public DateTimeOffset? EndedAt { get; set; }

    public bool IsActive => Status == SweepStatus.Active;
}

public enum CellState
{
    Pending,
    /// <summary>An item (or stack) was captured here.</summary>
    Captured,
    /// <summary>Same text as an adjacent captured cell: probably part of a multi-cell item.</summary>
    Covered,
    /// <summary>Ctrl+C here gave nothing (timeout or not an item); retryable.</summary>
    Failed,
    /// <summary>Marked by the user as nothing to capture.</summary>
    Skipped,
}

/// <summary>Derived state of one non-pending cell.</summary>
public sealed record SweepCell(Guid SweepId, GridCell Cell, CellState State, Guid? CaptureId);
