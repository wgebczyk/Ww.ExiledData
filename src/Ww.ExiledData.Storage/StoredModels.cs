using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Tracking;

namespace Ww.ExiledData.Storage;

/// <summary>One row of the session browser.</summary>
public sealed record SessionSummary(
    Session Session,
    int RegularItemCount,
    int StackableCount,
    int CaptureCount,
    DateTimeOffset? LastCaptureAt)
{
    public bool IsEmpty => CaptureCount == 0;
}

/// <summary>A change entry with its place in the undo/redo history.</summary>
/// <param name="Archived">From before the session was continued: kept as history, no longer undoable or redoable.</param>
public sealed record StoredChange(ChangeEntry Change, bool Undone, bool Archived = false);

/// <summary>A sweep with the number of items it found (captured cells) when last saved.</summary>
public sealed record StoredSweep(Sweep Sweep, int ItemCount);

/// <summary>Everything stored for one session, as a read-only snapshot.</summary>
public sealed record SessionData(
    Session Session,
    IReadOnlyList<Capture> Captures,
    IReadOnlyList<RegularItem> Items,
    IReadOnlyList<StackableItem> Stackables,
    IReadOnlyList<StoredChange> Changes,
    IReadOnlyList<StoredSweep> Sweeps,
    IReadOnlyList<SweepCell> Cells);

/// <summary>
/// A saved grid rectangle for an area kind at a screen resolution. <see cref="Grid"/> is relative to the screen's
/// top-left corner, so it applies to whichever monitor the game is on.
/// </summary>
public sealed record GridCalibration(string GridName, int ScreenWidth, int ScreenHeight, GridGeometry Grid, DateTimeOffset SavedAt);
