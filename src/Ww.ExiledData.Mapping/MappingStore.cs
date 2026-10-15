using Microsoft.Data.Sqlite;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Mapping;

/// <summary>A mapping session in the session list: its run count and session tags.</summary>
public sealed record MappingSummary(Guid SessionId, int RunCount, IReadOnlyList<MapTag> Tags);

/// <summary>
/// The mapping module's own tables (MAPPING_PLAN.md 6), in the app's database next to the capture tables, through
/// its own connection. Rows hang off the shared <c>sessions</c> table, so deleting a session deletes its runs, scans,
/// tags and notes. Captures stay in the raw capture log and are referenced by id only. Not thread-safe.
/// </summary>
public sealed class MappingStore : IDisposable
{
    /// <summary>
    /// Bump when the tables change, adding an upgrade step in <see cref="CreateSchema"/>. Versions before 4 (none,
    /// unknown, or the scan-role design) create the tables from scratch, dropping any old ones.
    /// </summary>
    public const int SchemaVersion = 4;
    private const string Module = "mapping";

    private readonly SqliteConnection _connection;

    private MappingStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens the database at <paramref name="path"/>, whose capture tables (<c>sessions</c>) must exist.</summary>
    public static MappingStore Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            CreateSchema(connection);
            return new MappingStore(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Rewrites everything stored for the session in one transaction (small: runs and scans of one session).</summary>
    public void Save(MapSession session)
    {
        var sid = Db(session.SessionId);
        using var tx = _connection.BeginTransaction();
        Execute("DELETE FROM map_runs WHERE session_id = $s", tx, ("$s", sid)); // cascades to setup, captures, adjustments, scans
        Execute("DELETE FROM map_tags WHERE session_id = $s", tx, ("$s", sid));
        Execute("DELETE FROM map_notes WHERE session_id = $s", tx, ("$s", sid));

        using (var runs = Command("INSERT INTO map_runs (id, session_id, number, started_at, ended_at, excluded) VALUES ($id, $s, $n, $start, $end, $excluded)", tx))
        using (var setup = Command("""
            INSERT INTO map_setup_items (id, run_id, ordinal, kind, capture_id, raw_text, copied_at, recalled_from_run)
            VALUES ($id, $run, $ord, $kind, $capture, $raw, $at, $recalled)
            """, tx))
        using (var captures = Command("INSERT INTO map_run_captures (run_id, capture_id, ordinal) VALUES ($run, $capture, $ord)", tx))
        using (var adjustments = Command("""
            INSERT INTO map_adjustments (id, run_id, ordinal, currency_key, currency_name, amount, source_scan_id, applied_at)
            VALUES ($id, $run, $ord, $key, $name, $amount, $scan, $at)
            """, tx))
        {
            foreach (var run in session.Runs)
            {
                Set(runs, "$id", Db(run.Id));
                Set(runs, "$s", sid);
                Set(runs, "$n", run.Number);
                Set(runs, "$start", run.StartedAt.UtcTicks);
                Set(runs, "$end", run.EndedAt?.UtcTicks);
                Set(runs, "$excluded", run.Excluded);
                runs.ExecuteNonQuery();

                var ordinal = 0;
                foreach (var item in run.Setup)
                {
                    Set(setup, "$id", Db(item.Id));
                    Set(setup, "$run", Db(run.Id));
                    Set(setup, "$ord", ordinal++);
                    Set(setup, "$kind", (int)item.Kind);
                    Set(setup, "$capture", item.CaptureId is { } c ? Db(c) : null);
                    Set(setup, "$raw", item.RawText);
                    Set(setup, "$at", item.CopiedAt.UtcTicks);
                    Set(setup, "$recalled", item.RecalledFromRun);
                    setup.ExecuteNonQuery();
                }

                ordinal = 0;
                foreach (var captureId in run.CaptureIds)
                {
                    Set(captures, "$run", Db(run.Id));
                    Set(captures, "$capture", Db(captureId));
                    Set(captures, "$ord", ordinal++);
                    captures.ExecuteNonQuery();
                }

                ordinal = 0;
                foreach (var a in run.Adjustments)
                {
                    Set(adjustments, "$id", Db(a.Id));
                    Set(adjustments, "$run", Db(run.Id));
                    Set(adjustments, "$ord", ordinal++);
                    Set(adjustments, "$key", a.CurrencyKey);
                    Set(adjustments, "$name", a.CurrencyName);
                    Set(adjustments, "$amount", a.Amount);
                    Set(adjustments, "$scan", a.SourceScanId is { } s ? Db(s) : null);
                    Set(adjustments, "$at", a.AppliedAt.UtcTicks);
                    adjustments.ExecuteNonQuery();
                }
            }
        }

        using (var scans = Command("""
            INSERT INTO map_scans (id, session_id, run_id, ordinal, sweep_id, label, kind, status, started_at, ended_at, check_dismissed)
            VALUES ($id, $s, $run, $ord, $sweep, $label, $kind, $status, $start, $end, $dismissed)
            """, tx))
        using (var cells = Command("""
            INSERT INTO map_scan_cells (scan_id, col, row, capture_id, currency_key, currency_name, count, raw_text)
            VALUES ($scan, $col, $row, $capture, $key, $name, $count, $raw)
            """, tx))
        {
            var ordinal = 0;
            foreach (var scan in session.Scans)
            {
                Set(scans, "$id", Db(scan.Id));
                Set(scans, "$s", sid);
                Set(scans, "$run", Db(scan.RunId));
                Set(scans, "$ord", ordinal++);
                Set(scans, "$sweep", Db(scan.SweepId));
                Set(scans, "$label", scan.Label);
                Set(scans, "$kind", (int)scan.Kind);
                Set(scans, "$status", (int)scan.Status);
                Set(scans, "$start", scan.StartedAt.UtcTicks);
                Set(scans, "$end", scan.EndedAt?.UtcTicks);
                Set(scans, "$dismissed", scan.CheckDismissed);
                scans.ExecuteNonQuery();

                foreach (var cell in scan.Cells.Values)
                {
                    Set(cells, "$scan", Db(scan.Id));
                    Set(cells, "$col", cell.Cell.Column);
                    Set(cells, "$row", cell.Cell.Row);
                    Set(cells, "$capture", cell.CaptureId is { } c ? Db(c) : null);
                    Set(cells, "$key", cell.Stack?.Key);
                    Set(cells, "$name", cell.Stack?.Name);
                    Set(cells, "$count", cell.Stack?.Count ?? 0);
                    Set(cells, "$raw", cell.RawText);
                    cells.ExecuteNonQuery();
                }
            }
        }

        using (var tags = Command("INSERT INTO map_tags (session_id, owner_id, ordinal, name, value) VALUES ($s, $owner, $ord, $name, $value)", tx))
        {
            foreach (var (owner, list) in session.Tags)
            {
                var ordinal = 0;
                foreach (var tag in list)
                {
                    Set(tags, "$s", sid);
                    Set(tags, "$owner", Db(owner));
                    Set(tags, "$ord", ordinal++);
                    Set(tags, "$name", tag.Name);
                    Set(tags, "$value", tag.Value);
                    tags.ExecuteNonQuery();
                }
            }
        }

        using (var notes = Command("INSERT INTO map_notes (owner_id, session_id, text) VALUES ($owner, $s, $text)", tx))
        {
            foreach (var (owner, text) in session.Notes)
            {
                Set(notes, "$owner", Db(owner));
                Set(notes, "$s", sid);
                Set(notes, "$text", text);
                notes.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Everything stored for the session; an empty one when nothing is (a new or a free recording session).</summary>
    public MapSession Load(Guid sessionId)
    {
        var sid = Db(sessionId);
        var session = new MapSession(sessionId);

        using (var cmd = Command("SELECT id, number, started_at, ended_at, excluded FROM map_runs WHERE session_id = $s ORDER BY number"))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                session.Runs.Add(new MapRun
                {
                    Id = Guid.Parse(r.GetString(0)), Number = r.GetInt32(1), StartedAt = Time(r, 2), EndedAt = TimeOrNull(r, 3), Excluded = r.GetBoolean(4),
                });
        }
        var runs = session.Runs.ToDictionary(r => r.Id);

        using (var cmd = Command("""
            SELECT i.run_id, i.id, i.kind, i.capture_id, i.raw_text, i.copied_at, i.recalled_from_run
            FROM map_setup_items i JOIN map_runs r ON r.id = i.run_id WHERE r.session_id = $s ORDER BY i.run_id, i.ordinal
            """))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                runs[Guid.Parse(r.GetString(0))].Setup.Add(new SetupItem
                {
                    Id = Guid.Parse(r.GetString(1)),
                    Kind = (SetupItemKind)r.GetInt32(2),
                    CaptureId = GuidOrNull(r, 3),
                    RawText = r.GetString(4),
                    CopiedAt = Time(r, 5),
                    RecalledFromRun = r.IsDBNull(6) ? null : r.GetInt32(6),
                });
            }
        }

        using (var cmd = Command("""
            SELECT c.run_id, c.capture_id FROM map_run_captures c JOIN map_runs r ON r.id = c.run_id
            WHERE r.session_id = $s ORDER BY c.run_id, c.ordinal
            """))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                runs[Guid.Parse(r.GetString(0))].CaptureIds.Add(Guid.Parse(r.GetString(1)));
        }

        using (var cmd = Command("""
            SELECT a.run_id, a.id, a.currency_key, a.currency_name, a.amount, a.source_scan_id, a.applied_at
            FROM map_adjustments a JOIN map_runs r ON r.id = a.run_id WHERE r.session_id = $s ORDER BY a.run_id, a.ordinal
            """))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                runs[Guid.Parse(r.GetString(0))].Adjustments.Add(new LootAdjustment(Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                    r.GetInt32(4), GuidOrNull(r, 5), Time(r, 6)));
        }

        using (var cmd = Command("""
            SELECT id, run_id, sweep_id, label, kind, status, started_at, ended_at, check_dismissed FROM map_scans WHERE session_id = $s ORDER BY ordinal
            """))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                session.Scans.Add(new Scan
                {
                    Id = Guid.Parse(r.GetString(0)),
                    RunId = Guid.Parse(r.GetString(1)),
                    SweepId = Guid.Parse(r.GetString(2)),
                    Label = r.GetString(3),
                    Kind = (ScanKind)r.GetInt32(4),
                    Status = (ScanStatus)r.GetInt32(5),
                    StartedAt = Time(r, 6),
                    EndedAt = TimeOrNull(r, 7),
                    CheckDismissed = r.GetBoolean(8),
                });
            }
        }
        var scans = session.Scans.ToDictionary(s => s.Id);

        using (var cmd = Command("""
            SELECT c.scan_id, c.col, c.row, c.capture_id, c.currency_key, c.currency_name, c.count, c.raw_text
            FROM map_scan_cells c JOIN map_scans s ON s.id = c.scan_id WHERE s.session_id = $s
            """))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cell = new GridCell(r.GetInt32(1), r.GetInt32(2));
                var stack = r.IsDBNull(4) ? null : new CurrencyStack(r.GetString(4), r.GetString(5), r.GetInt32(6));
                scans[Guid.Parse(r.GetString(0))].Cells[cell] = new ScanCell(cell, GuidOrNull(r, 3), stack, r.IsDBNull(7) ? null : r.GetString(7));
            }
        }

        using (var cmd = Command("SELECT owner_id, name, value FROM map_tags WHERE session_id = $s ORDER BY owner_id, ordinal"))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var owner = Guid.Parse(r.GetString(0));
                if (!session.Tags.TryGetValue(owner, out var list))
                    session.Tags[owner] = list = [];
                list.Add(new MapTag(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        using (var cmd = Command("SELECT owner_id, text FROM map_notes WHERE session_id = $s"))
        {
            Set(cmd, "$s", sid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                session.Notes[Guid.Parse(r.GetString(0))] = r.GetString(1);
        }

        return session;
    }

    /// <summary>Run count and session tags of every session with mapping data.</summary>
    public IReadOnlyDictionary<Guid, MappingSummary> ListSummaries()
    {
        var runCounts = new Dictionary<Guid, int>();
        using (var cmd = Command("SELECT session_id, COUNT(*) FROM map_runs GROUP BY session_id"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                runCounts[Guid.Parse(r.GetString(0))] = r.GetInt32(1);
        }

        var tags = new Dictionary<Guid, List<MapTag>>();
        using (var cmd = Command("SELECT session_id, name, value FROM map_tags WHERE owner_id = session_id ORDER BY session_id, ordinal"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var id = Guid.Parse(r.GetString(0));
                if (!tags.TryGetValue(id, out var list))
                    tags[id] = list = [];
                list.Add(new MapTag(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        return runCounts.Keys.Union(tags.Keys).ToDictionary(id => id,
            id => new MappingSummary(id, runCounts.GetValueOrDefault(id), tags.GetValueOrDefault(id) ?? []));
    }

    /// <summary>Tags used before, most used first, for the tag box's suggestions (MAPPING_PLAN.md 3.5).</summary>
    public IReadOnlyList<MapTag> KnownTags()
    {
        using var cmd = Command("SELECT name, value FROM map_tags GROUP BY name, value ORDER BY COUNT(*) DESC, name, value");
        using var r = cmd.ExecuteReader();
        var result = new List<MapTag>();
        while (r.Read())
            result.Add(new MapTag(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return result;
    }

    /// <summary>Ids of the sessions with a scan left active (by an app exit).</summary>
    public IReadOnlyList<Guid> SessionsWithActiveScans()
    {
        using var cmd = Command("SELECT DISTINCT session_id FROM map_scans WHERE status = $active");
        Set(cmd, "$active", (int)ScanStatus.Active);
        using var r = cmd.ExecuteReader();
        var result = new List<Guid>();
        while (r.Read())
            result.Add(Guid.Parse(r.GetString(0)));
        return result;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        Execute(connection, "CREATE TABLE IF NOT EXISTS module_schema (module TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL)", tx);
        long version;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT version FROM module_schema WHERE module = $m";
            cmd.Parameters.AddWithValue("$m", Module);
            version = cmd.ExecuteScalar() as long? ?? 0;
        }
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Mapping schema version {version} is newer than this app supports ({SchemaVersion}).");
        if (version == SchemaVersion)
        {
            tx.Commit();
            return;
        }

        // Version 4 replaced scan roles and legs with scan kinds (swipe, stash, final): older mapping data is dropped.
        if (version < 4)
            CreateTables(connection, tx);

        Execute(connection, "INSERT INTO module_schema (module, version) VALUES ($m, $v) ON CONFLICT (module) DO UPDATE SET version = excluded.version",
            tx, ("$m", Module), ("$v", SchemaVersion));
        tx.Commit();
    }

    /// <summary>Version 4, from scratch (an unknown or missing mapping schema is dropped first).</summary>
    private static void CreateTables(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, """
            DROP TABLE IF EXISTS map_adjustments;
            DROP TABLE IF EXISTS map_notes;
            DROP TABLE IF EXISTS map_tags;
            DROP TABLE IF EXISTS map_scan_cells;
            DROP TABLE IF EXISTS map_scans;
            DROP TABLE IF EXISTS map_run_captures;
            DROP TABLE IF EXISTS map_setup_items;
            DROP TABLE IF EXISTS map_runs;

            CREATE TABLE map_runs (
                id          TEXT    NOT NULL PRIMARY KEY,
                session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                number      INTEGER NOT NULL,
                started_at  INTEGER NOT NULL,
                ended_at    INTEGER NULL,
                excluded    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_map_runs_session ON map_runs (session_id, number);

            -- Waystone and tablets, raw text only (mods are parsed later, MAPPING_PLAN.md 3.6).
            -- recalled_from_run: the run number of the earlier copy it was recalled from; NULL = copied in this setup.
            CREATE TABLE map_setup_items (
                id          TEXT    NOT NULL PRIMARY KEY,
                run_id      TEXT    NOT NULL REFERENCES map_runs (id) ON DELETE CASCADE,
                ordinal     INTEGER NOT NULL,
                kind        INTEGER NOT NULL,
                capture_id  TEXT    NULL,
                raw_text    TEXT    NOT NULL,
                copied_at   INTEGER NOT NULL,
                recalled_from_run INTEGER NULL
            );
            CREATE INDEX ix_map_setup_items_run ON map_setup_items (run_id, ordinal);

            -- Captures of the raw capture log made during the run; no foreign key, the log belongs to the capture code.
            CREATE TABLE map_run_captures (
                run_id      TEXT    NOT NULL REFERENCES map_runs (id) ON DELETE CASCADE,
                capture_id  TEXT    NOT NULL,
                ordinal     INTEGER NOT NULL,
                PRIMARY KEY (run_id, capture_id)
            );

            -- kind: 0 swipe, 1 stash, 2 final (ScanKind).
            CREATE TABLE map_scans (
                id          TEXT    NOT NULL PRIMARY KEY,
                session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                run_id      TEXT    NOT NULL REFERENCES map_runs (id) ON DELETE CASCADE,
                ordinal     INTEGER NOT NULL,
                sweep_id    TEXT    NOT NULL,
                label       TEXT    NOT NULL,
                kind        INTEGER NOT NULL,
                status      INTEGER NOT NULL,
                started_at  INTEGER NOT NULL,
                ended_at    INTEGER NULL,
                check_dismissed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_map_scans_session ON map_scans (session_id, ordinal);

            -- What each copied cell held; currency_key NULL = an item that is not stackable (raw_text keeps it).
            CREATE TABLE map_scan_cells (
                scan_id        TEXT    NOT NULL REFERENCES map_scans (id) ON DELETE CASCADE,
                col            INTEGER NOT NULL,
                row            INTEGER NOT NULL,
                capture_id     TEXT    NULL,
                currency_key   TEXT    NULL,
                currency_name  TEXT    NULL,
                count          INTEGER NOT NULL,
                raw_text       TEXT    NULL,
                PRIMARY KEY (scan_id, col, row)
            );

            -- Loot corrections confirmed by the user (from a Final scan's check).
            CREATE TABLE map_adjustments (
                id              TEXT    NOT NULL PRIMARY KEY,
                run_id          TEXT    NOT NULL REFERENCES map_runs (id) ON DELETE CASCADE,
                ordinal         INTEGER NOT NULL,
                currency_key    TEXT    NOT NULL,
                currency_name   TEXT    NOT NULL,
                amount          INTEGER NOT NULL,
                source_scan_id  TEXT    NULL,
                applied_at      INTEGER NOT NULL
            );
            CREATE INDEX ix_map_adjustments_run ON map_adjustments (run_id, ordinal);

            -- owner_id: the session id (session tags and note) or a run id.
            CREATE TABLE map_tags (
                session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                owner_id    TEXT    NOT NULL,
                ordinal     INTEGER NOT NULL,
                name        TEXT    NOT NULL,
                value       TEXT    NULL,
                PRIMARY KEY (owner_id, ordinal)
            );
            CREATE INDEX ix_map_tags_session ON map_tags (session_id);

            CREATE TABLE map_notes (
                owner_id    TEXT    NOT NULL PRIMARY KEY,
                session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
                text        TEXT    NOT NULL
            );
            CREATE INDEX ix_map_notes_session ON map_notes (session_id);
            """, tx);
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Execute(string sql, SqliteTransaction tx, params (string Name, object? Value)[] args) => Execute(_connection, sql, tx, args);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction tx, params (string Name, object? Value)[] args)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Set(SqliteCommand cmd, string name, object? value)
    {
        if (cmd.Parameters.Contains(name))
            cmd.Parameters[name].Value = value ?? DBNull.Value;
        else
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    // Same conventions as the capture tables: GUIDs as "D" text, timestamps as UTC ticks.
    private static string Db(Guid id) => id.ToString("D");
    private static DateTimeOffset Time(SqliteDataReader r, int i) => new(r.GetInt64(i), TimeSpan.Zero);
    private static DateTimeOffset? TimeOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Time(r, i);
    private static Guid? GuidOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
}
