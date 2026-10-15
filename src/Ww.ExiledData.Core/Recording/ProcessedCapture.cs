using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Core.Recording;

/// <summary>Stackable data read from a copied item.</summary>
/// <param name="Key">Identity key: item class + rarity + name + max stack size.</param>
public sealed record CapturedStack(string Key, string ItemClass, string Rarity, string Name, int Count, int MaxStack);

/// <summary>
/// One item copy after the capture pipeline processed it, published to other modules (MAPPING_PLAN.md 0, the seam).
/// Every copy of item text is published, including ones that changed nothing (copying the same cell again in a scan
/// still matters to a subscriber); copies that were not item text are not.
/// </summary>
/// <param name="SessionId">The recording session the copy went to.</param>
/// <param name="CaptureId">The entry added to the raw capture log, or null when the copy changed nothing and was not logged.</param>
/// <param name="RawText">Normalized item text.</param>
/// <param name="Position">Cursor position at the copy, in physical pixels.</param>
/// <param name="Stack">Set when the item is a stackable that parsed.</param>
/// <param name="SweepId">The running sweep, when the copy was inside its grid.</param>
/// <param name="Cell">The sweep cell the copy was taken in.</param>
public sealed record ProcessedCapture(
    Guid SessionId,
    SessionKind SessionKind,
    Guid? CaptureId,
    string RawText,
    ScreenPoint Position,
    DateTimeOffset CapturedAt,
    CapturedStack? Stack,
    Guid? SweepId,
    GridCell? Cell);

/// <summary>
/// A sweep of the recording session started or ended (a scan, to the mapping module). Captures inside it arrive on
/// <see cref="ICaptureStream.CaptureProcessed"/> between its <see cref="SweepStatus.Active"/> event and the one that
/// ends it (<see cref="SweepStatus.Finished"/> or <see cref="SweepStatus.Canceled"/>: a canceled sweep's changes are
/// withdrawn, so it must not count).
/// </summary>
/// <param name="At">The start for an active sweep, the end otherwise.</param>
public sealed record SweepChanged(Guid SessionId, SessionKind SessionKind, Guid SweepId, string Label, SweepStatus Status, DateTimeOffset At);

/// <summary>
/// The capture stream other modules subscribe to. Its owner is the recording coordinator: only one session records
/// at a time, and each capture goes to exactly that session. Events are raised one at a time on the capture queue
/// thread, after the capture is stored; handlers must not block.
/// </summary>
public interface ICaptureStream
{
    event Action<ProcessedCapture>? CaptureProcessed;
    event Action<SweepChanged>? SweepChanged;
}
