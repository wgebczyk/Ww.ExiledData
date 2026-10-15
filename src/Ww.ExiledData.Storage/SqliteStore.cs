using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;
using Ww.ExiledData.Core.Tracking;

namespace Ww.ExiledData.Storage;

/// <summary>
/// SQLite persistence for sessions, the raw capture log, derived state and change entries.
/// Holds one open connection; not thread-safe, so callers use the same queue as capture handling.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/>; use ":memory:" for a throwaway one.</summary>
    public static SqliteStore Open(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false, // one long-lived connection; also releases the file on Dispose
        }.ToString();

        var connection = new SqliteConnection(cs);
        try
        {
            connection.Open();
            if (path != ":memory:")
                connection.Execute("PRAGMA journal_mode = WAL");
            Schema.Migrate(connection);
            return new SqliteStore(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int SchemaVersion => Convert.ToInt32(_connection.ExecuteScalar("PRAGMA user_version"));

    public void Dispose() => _connection.Dispose();

    public void SaveSession(Session session)
    {
        using var tx = _connection.BeginTransaction();
        UpsertSession(session, tx);
        tx.Commit();
    }

    /// <summary>
    /// Writes the tracker's session, capture log, current state and undo/redo entries in one transaction.
    /// Captures are upserted (append-only apart from withdrawn_at); derived state is rewritten, which is
    /// cheap at session sizes (hundreds to a few thousand rows) and can't drift from the tracker.
    /// </summary>
    public void SaveTracker(SessionTracker tracker)
    {
        var sessionId = tracker.Session.Id.ToDb();
        using var tx = _connection.BeginTransaction();
        UpsertSession(tracker.Session, tx);

        using (var cmd = _connection.Command("""
            INSERT INTO captures (id, session_id, raw_text, pos_x, pos_y, captured_at, kind, identity_key, sweep_id, cell_col, cell_row, withdrawn_at)
            VALUES ($id, $session, $raw, $x, $y, $at, $kind, $key, $sweep, $col, $row, $withdrawn)
            ON CONFLICT (id) DO UPDATE SET withdrawn_at = excluded.withdrawn_at
            """, tx))
        {
            foreach (var c in tracker.Log)
            {
                cmd.Set("$id", c.Id.ToDb());
                cmd.Set("$session", sessionId);
                cmd.Set("$raw", c.RawText);
                cmd.Set("$x", c.Position.X);
                cmd.Set("$y", c.Position.Y);
                cmd.Set("$at", c.CapturedAt.ToDb());
                cmd.Set("$kind", (int)c.Kind);
                cmd.Set("$key", c.IdentityKey);
                cmd.Set("$sweep", c.SweepId?.ToDb());
                cmd.Set("$col", c.Cell?.Column);
                cmd.Set("$row", c.Cell?.Row);
                cmd.Set("$withdrawn", c.WithdrawnAt.ToDb());
                cmd.ExecuteNonQuery();
            }
        }

        // Sweeps before their cells (foreign key); upserted, since rows are never removed while the session exists.
        using (var cmd = _connection.Command("""
            INSERT INTO sweeps (id, session_id, grid_name, columns, rows, label, origin_x, origin_y, cell_size, status, started_at, ended_at, item_count)
            VALUES ($id, $session, $grid, $cols, $rows, $label, $x, $y, $cell, $status, $start, $end, $items)
            ON CONFLICT (id) DO UPDATE SET status = excluded.status, ended_at = excluded.ended_at, item_count = excluded.item_count,
                origin_x = excluded.origin_x, origin_y = excluded.origin_y, cell_size = excluded.cell_size
            """, tx))
        {
            foreach (var s in tracker.Sweeps)
            {
                cmd.Set("$id", s.Id.ToDb());
                cmd.Set("$session", sessionId);
                cmd.Set("$grid", s.Kind.Name);
                cmd.Set("$cols", s.Kind.Columns);
                cmd.Set("$rows", s.Kind.Rows);
                cmd.Set("$label", s.Label);
                cmd.Set("$x", s.Grid.X);
                cmd.Set("$y", s.Grid.Y);
                cmd.Set("$cell", s.Grid.CellSize);
                cmd.Set("$status", (int)s.Status);
                cmd.Set("$start", s.StartedAt.ToDb());
                cmd.Set("$end", s.EndedAt.ToDb());
                cmd.Set("$items", tracker.ItemCountOf(s.Id));
                cmd.ExecuteNonQuery();
            }
        }

        _connection.Execute("DELETE FROM sweep_cells WHERE session_id = $s", tx, ("$s", sessionId));
        using (var cmd = _connection.Command("""
            INSERT INTO sweep_cells (sweep_id, session_id, col, row, state, capture_id)
            VALUES ($sweep, $session, $col, $row, $state, $capture)
            """, tx))
        {
            foreach (var cell in tracker.Sweeps.SelectMany(s => tracker.CellsOf(s.Id)))
            {
                cmd.Set("$sweep", cell.SweepId.ToDb());
                cmd.Set("$session", sessionId);
                cmd.Set("$col", cell.Cell.Column);
                cmd.Set("$row", cell.Cell.Row);
                cmd.Set("$state", (int)cell.State);
                cmd.Set("$capture", cell.CaptureId?.ToDb());
                cmd.ExecuteNonQuery();
            }
        }

        _connection.Execute("DELETE FROM regular_items WHERE session_id = $s", tx, ("$s", sessionId));
        using (var cmd = _connection.Command("""
            INSERT INTO regular_items (capture_id, session_id, ordinal, identity_key, pos_x, pos_y, last_seen_at, possible_duplicate, needs_review, covered_json)
            VALUES ($capture, $session, $ord, $key, $x, $y, $seen, $dup, $review, $covered)
            """, tx))
        {
            var ordinal = 0;
            foreach (var i in tracker.Items)
            {
                cmd.Set("$capture", i.CaptureId.ToDb());
                cmd.Set("$session", sessionId);
                cmd.Set("$ord", ordinal++);
                cmd.Set("$key", i.IdentityKey);
                cmd.Set("$x", i.Position.X);
                cmd.Set("$y", i.Position.Y);
                cmd.Set("$seen", i.LastSeenAt.ToDb());
                cmd.Set("$dup", i.PossibleDuplicate);
                cmd.Set("$review", i.NeedsReview);
                cmd.Set("$covered", i.CoveredCaptureIds.Count == 0 ? null : JsonSerializer.Serialize(i.CoveredCaptureIds));
                cmd.ExecuteNonQuery();
            }
        }

        _connection.Execute("DELETE FROM stackables WHERE session_id = $s", tx, ("$s", sessionId));
        using (var cmd = _connection.Command("""
            INSERT INTO stackables (session_id, key, ordinal, item_class, rarity, name, max_stack, count, latest_raw_text, updated_at, parser_version, stacks_json)
            VALUES ($session, $key, $ord, $class, $rarity, $name, $max, $count, $raw, $at, $ver, $stacks)
            """, tx))
        {
            var ordinal = 0;
            foreach (var s in tracker.Stackables)
            {
                cmd.Set("$session", sessionId);
                cmd.Set("$key", s.Key);
                cmd.Set("$ord", ordinal++);
                cmd.Set("$class", s.ItemClass);
                cmd.Set("$rarity", s.Rarity);
                cmd.Set("$name", s.Name);
                cmd.Set("$max", s.MaxStack);
                cmd.Set("$count", s.Count);
                cmd.Set("$raw", s.LatestRawText);
                cmd.Set("$at", s.UpdatedAt.ToDb());
                cmd.Set("$ver", s.ParserVersion);
                cmd.Set("$stacks", JsonSerializer.Serialize(s.Stacks));
                cmd.ExecuteNonQuery();
            }
        }

        // Application order: applied changes oldest first, then the redo stack top-down
        // (its top is the earliest of the undone changes). Archived history (from before the session was
        // continued) stays as it is, and the tracker's entries follow it.
        var changes = tracker.History.Reverse().Select(c => (Change: c, Undone: false))
            .Concat(tracker.UndoneChanges.Select(c => (Change: c, Undone: true)));
        _connection.Execute("DELETE FROM change_entries WHERE session_id = $s AND archived = 0", tx, ("$s", sessionId));
        var firstSeq = Convert.ToInt32(_connection.ExecuteScalar(
            "SELECT COALESCE(MAX(seq) + 1, 0) FROM change_entries WHERE session_id = $s", tx, ("$s", sessionId)));
        using (var cmd = _connection.Command("""
            INSERT INTO change_entries (id, session_id, seq, timestamp, type, capture_id, identity_key, before_json, after_json,
                                        related_capture_id, sweep_id, children_json, undone)
            VALUES ($id, $session, $seq, $at, $type, $capture, $key, $before, $after, $related, $sweep, $children, $undone)
            """, tx))
        {
            var seq = firstSeq;
            foreach (var (c, undone) in changes)
            {
                cmd.Set("$id", c.Id.ToDb());
                cmd.Set("$session", sessionId);
                cmd.Set("$seq", seq++);
                cmd.Set("$at", c.Timestamp.ToDb());
                cmd.Set("$type", (int)c.Type);
                cmd.Set("$capture", c.CaptureId.ToDb());
                cmd.Set("$key", c.IdentityKey);
                cmd.Set("$before", c.Before is null ? null : JsonSerializer.Serialize(c.Before));
                cmd.Set("$after", c.After is null ? null : JsonSerializer.Serialize(c.After));
                cmd.Set("$related", c.RelatedCaptureId?.ToDb());
                cmd.Set("$sweep", c.SweepId?.ToDb());
                cmd.Set("$children", c.Children is null ? null : JsonSerializer.Serialize(c.Children));
                cmd.Set("$undone", undone);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Sessions newest first, optionally filtered by start time (inclusive range).</summary>
    public IReadOnlyList<SessionSummary> ListSessions(DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
    {
        using var cmd = _connection.Command("""
            SELECT s.id, s.name, s.started_at, s.ended_at, s.interrupted, s.kind,
                   (SELECT COUNT(*) FROM regular_items r WHERE r.session_id = s.id),
                   (SELECT COUNT(*) FROM stackables k WHERE k.session_id = s.id),
                   (SELECT COUNT(*) FROM captures c WHERE c.session_id = s.id),
                   (SELECT MAX(captured_at) FROM captures c WHERE c.session_id = s.id)
            FROM sessions s
            WHERE ($from IS NULL OR s.started_at >= $from) AND ($to IS NULL OR s.started_at <= $to)
            ORDER BY s.started_at DESC
            """);
        cmd.Set("$from", startedFrom.ToDb());
        cmd.Set("$to", startedTo.ToDb());

        var result = new List<SessionSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new SessionSummary(ReadSession(r), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetTimeOrNull(9)));
        return result;
    }

    public Session? GetSession(Guid id)
    {
        using var cmd = _connection.Command("SELECT id, name, started_at, ended_at, interrupted, kind FROM sessions WHERE id = $id");
        cmd.Set("$id", id.ToDb());
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public SessionData? LoadSession(Guid id)
    {
        var session = GetSession(id);
        if (session is null)
            return null;
        var sid = id.ToDb();

        var captures = new List<Capture>();
        using (var cmd = _connection.Command("""
            SELECT id, raw_text, pos_x, pos_y, captured_at, kind, identity_key, withdrawn_at, sweep_id, cell_col, cell_row
            FROM captures WHERE session_id = $s ORDER BY captured_at, rowid
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                captures.Add(new Capture
                {
                    Id = r.GetGuidText(0),
                    SessionId = id,
                    RawText = r.GetString(1),
                    Position = new ScreenPoint(r.GetInt32(2), r.GetInt32(3)),
                    CapturedAt = r.GetTime(4),
                    Kind = (CaptureKind)r.GetInt32(5),
                    IdentityKey = r.GetString(6),
                    WithdrawnAt = r.GetTimeOrNull(7),
                    SweepId = r.GetGuidOrNull(8),
                    Cell = ReadCell(r, 9),
                });
            }
        }

        var items = new List<RegularItem>();
        using (var cmd = _connection.Command("""
            SELECT i.capture_id, i.identity_key, c.raw_text, i.pos_x, i.pos_y, i.last_seen_at, i.possible_duplicate, i.needs_review,
                   c.sweep_id, c.cell_col, c.cell_row, i.covered_json
            FROM regular_items i JOIN captures c ON c.id = i.capture_id
            WHERE i.session_id = $s ORDER BY i.ordinal
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var raw = r.GetString(2);
                items.Add(new RegularItem
                {
                    CaptureId = r.GetGuidText(0),
                    IdentityKey = r.GetString(1),
                    Label = ItemLabel.From(raw),
                    RawText = raw,
                    Position = new ScreenPoint(r.GetInt32(3), r.GetInt32(4)),
                    LastSeenAt = r.GetTime(5),
                    PossibleDuplicate = r.GetBoolean(6),
                    NeedsReview = r.GetBoolean(7),
                    SweepId = r.GetGuidOrNull(8),
                    Cell = ReadCell(r, 9),
                    CoveredCaptureIds = r.GetStringOrNull(11) is { } covered ? JsonSerializer.Deserialize<List<Guid>>(covered)! : [],
                });
            }
        }

        var stackables = new List<StackableItem>();
        using (var cmd = _connection.Command("""
            SELECT key, item_class, rarity, name, max_stack, count, latest_raw_text, updated_at, parser_version, stacks_json
            FROM stackables WHERE session_id = $s ORDER BY ordinal
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                stackables.Add(new StackableItem
                {
                    Key = key,
                    ItemClass = r.GetString(1),
                    Rarity = r.GetString(2),
                    Name = r.GetString(3),
                    MaxStack = r.GetInt32(4),
                    Stacks = r.GetStringOrNull(9) is { } json
                        ? JsonSerializer.Deserialize<List<StackPlace>>(json)!
                        : [LegacyStack(captures, key, r.GetInt32(5))],
                    LatestRawText = r.GetString(6),
                    UpdatedAt = r.GetTime(7),
                    ParserVersion = r.GetInt32(8),
                });
            }
        }

        var changes = new List<StoredChange>();
        using (var cmd = _connection.Command("""
            SELECT id, timestamp, type, capture_id, identity_key, before_json, after_json, undone, related_capture_id, sweep_id, children_json,
                   archived
            FROM change_entries WHERE session_id = $s ORDER BY seq
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var change = new ChangeEntry(
                    r.GetGuidText(0), r.GetTime(1), (ChangeType)r.GetInt32(2), r.GetGuidText(3), r.GetString(4),
                    ReadSnapshot(r.GetStringOrNull(5)), ReadSnapshot(r.GetStringOrNull(6)),
                    r.GetGuidOrNull(8), r.GetGuidOrNull(9),
                    r.GetStringOrNull(10) is { } children ? JsonSerializer.Deserialize<List<ChangeEntry>>(children) : null);
                changes.Add(new StoredChange(change, r.GetBoolean(7), r.GetBoolean(11)));
            }
        }

        var sweeps = new List<StoredSweep>();
        using (var cmd = _connection.Command("""
            SELECT id, grid_name, columns, rows, label, origin_x, origin_y, cell_size, status, started_at, ended_at, item_count
            FROM sweeps WHERE session_id = $s ORDER BY started_at, rowid
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = new GridKind(r.GetString(1), r.GetInt32(2), r.GetInt32(3));
                sweeps.Add(new StoredSweep(new Sweep
                {
                    Id = r.GetGuidText(0),
                    SessionId = id,
                    Kind = kind,
                    Label = r.GetString(4),
                    Grid = new GridGeometry(r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), kind.Columns, kind.Rows),
                    Status = (SweepStatus)r.GetInt32(8),
                    StartedAt = r.GetTime(9),
                    EndedAt = r.GetTimeOrNull(10),
                }, r.GetInt32(11)));
            }
        }

        var cells = new List<SweepCell>();
        using (var cmd = _connection.Command("""
            SELECT sweep_id, col, row, state, capture_id FROM sweep_cells WHERE session_id = $s ORDER BY sweep_id, row, col
            """))
        {
            cmd.Set("$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                cells.Add(new SweepCell(r.GetGuidText(0), new GridCell(r.GetInt32(1), r.GetInt32(2)), (CellState)r.GetInt32(3), r.GetGuidOrNull(4)));
        }

        return new SessionData(session, captures, items, stackables, changes, sweeps, cells);
    }

    /// <summary>
    /// Continues an ended session (<see cref="SessionLifecycle.Continue"/>): archives its undo history, stores it as
    /// active and returns its rebuilt tracker. Null when the session does not exist.
    /// </summary>
    public SessionTracker? ContinueSession(Guid id, int positionTolerance, TimeProvider? time = null)
    {
        if (LoadSession(id) is not { } data)
            return null;

        SessionLifecycle.Continue(data.Session);
        var tracker = SessionTracker.Restore(data.Session, data.Captures, data.Items, data.Stackables,
            data.Sweeps.Select(s => s.Sweep), data.Cells, positionTolerance, time);
        _connection.Execute("UPDATE change_entries SET archived = 1 WHERE session_id = $s", null, ("$s", id.ToDb()));
        SaveTracker(tracker); // stores the session as active, and any sweep the restore closed
        return tracker;
    }

    /// <summary>Deletes a session and everything recorded in it.</summary>
    public bool DeleteSession(Guid id) =>
        _connection.Execute("DELETE FROM sessions WHERE id = $id", null, ("$id", id.ToDb())) > 0;

    /// <summary>
    /// Launch-time recovery: closes every session still marked active at its last capture and flags it interrupted,
    /// and does the same for sweeps still running. Returns the sessions it closed.
    /// </summary>
    public IReadOnlyList<SessionSummary> RecoverInterrupted()
    {
        var open = ListSessions().Where(s => s.Session.IsActive).ToList();
        foreach (var summary in open)
        {
            SessionLifecycle.CloseInterrupted(summary.Session, summary.LastCaptureAt);
            SaveSession(summary.Session);
        }

        _connection.Execute("""
            UPDATE sweeps SET status = $interrupted,
                ended_at = COALESCE((SELECT MAX(c.captured_at) FROM captures c WHERE c.sweep_id = sweeps.id), started_at)
            WHERE status = $active
            """, null, ("$interrupted", (int)SweepStatus.Interrupted), ("$active", (int)SweepStatus.Active));
        return open;
    }

    /// <summary>Remembers <paramref name="calibration"/> for its area kind and screen resolution.</summary>
    public void SaveCalibration(GridCalibration calibration)
    {
        var g = calibration.Grid;
        _connection.Execute("""
            INSERT INTO grid_calibrations (grid_name, columns, rows, screen_width, screen_height, origin_x, origin_y, cell_size, saved_at)
            VALUES ($name, $cols, $rows, $w, $h, $x, $y, $cell, $at)
            ON CONFLICT (grid_name, columns, rows, screen_width, screen_height) DO UPDATE SET
                origin_x = excluded.origin_x, origin_y = excluded.origin_y, cell_size = excluded.cell_size, saved_at = excluded.saved_at
            """, null,
            ("$name", calibration.GridName), ("$cols", g.Columns), ("$rows", g.Rows), ("$w", calibration.ScreenWidth),
            ("$h", calibration.ScreenHeight), ("$x", g.X), ("$y", g.Y), ("$cell", g.CellSize), ("$at", calibration.SavedAt.ToDb()));
    }

    /// <summary>
    /// The calibration for <paramref name="kind"/> at this screen resolution; failing that, the most recent one for
    /// the kind at any resolution (the caller should warn that it may be off).
    /// </summary>
    public GridCalibration? FindCalibration(GridKind kind, int screenWidth, int screenHeight)
    {
        using var cmd = _connection.Command("""
            SELECT screen_width, screen_height, origin_x, origin_y, cell_size, saved_at
            FROM grid_calibrations WHERE grid_name = $name AND columns = $cols AND rows = $rows
            ORDER BY (screen_width = $w AND screen_height = $h) DESC, saved_at DESC
            LIMIT 1
            """);
        cmd.Set("$name", kind.Name);
        cmd.Set("$cols", kind.Columns);
        cmd.Set("$rows", kind.Rows);
        cmd.Set("$w", screenWidth);
        cmd.Set("$h", screenHeight);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new GridCalibration(kind.Name, r.GetInt32(0), r.GetInt32(1),
                new GridGeometry(r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), kind.Columns, kind.Rows), r.GetTime(5))
            : null;
    }

    private void UpsertSession(Session s, SqliteTransaction tx) =>
        _connection.Execute("""
            INSERT INTO sessions (id, name, started_at, ended_at, interrupted, kind)
            VALUES ($id, $name, $start, $end, $interrupted, $kind)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, ended_at = excluded.ended_at, interrupted = excluded.interrupted
            """, tx,
            ("$id", s.Id.ToDb()), ("$name", s.Name), ("$start", s.StartedAt.ToDb()),
            ("$end", s.EndedAt.ToDb()), ("$interrupted", s.Interrupted), ("$kind", (int)s.Kind));

    private static Session ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetGuidText(0),
        Name = r.GetString(1),
        StartedAt = r.GetTime(2),
        EndedAt = r.GetTimeOrNull(3),
        Interrupted = r.GetBoolean(4),
        Kind = (SessionKind)r.GetInt32(5),
    };

    /// <summary>A stackable stored before stacks were tracked per place: its count as one stack at its latest capture.</summary>
    private static StackPlace LegacyStack(List<Capture> captures, string key, int count)
    {
        var latest = captures.LastOrDefault(c => c.IdentityKey == key && !c.IsWithdrawn) ?? captures.Last(c => c.IdentityKey == key);
        return new StackPlace(latest.Id, latest.Position, latest.SweepId, latest.Cell, count);
    }

    private static StackableSnapshot? ReadSnapshot(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<StackableSnapshot>(json);

    private static GridCell? ReadCell(SqliteDataReader r, int columnIndex) =>
        r.IsDBNull(columnIndex) ? null : new GridCell(r.GetInt32(columnIndex), r.GetInt32(columnIndex + 1));
}
