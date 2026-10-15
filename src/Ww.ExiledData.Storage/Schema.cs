using Microsoft.Data.Sqlite;

namespace Ww.ExiledData.Storage;

/// <summary>Forward-only schema migrations tracked with <c>PRAGMA user_version</c>.</summary>
internal static class Schema
{
    // Index i upgrades the database from version i to i + 1. Never edit a shipped migration; append a new one.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE sessions (
            id           TEXT    NOT NULL PRIMARY KEY,
            name         TEXT    NOT NULL,
            started_at   INTEGER NOT NULL,
            ended_at     INTEGER NULL,
            interrupted  INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_sessions_started ON sessions (started_at);

        -- Append-only raw capture log: the source of truth. Only withdrawn_at ever changes.
        CREATE TABLE captures (
            id            TEXT    NOT NULL PRIMARY KEY,
            session_id    TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            raw_text      TEXT    NOT NULL,
            pos_x         INTEGER NOT NULL,
            pos_y         INTEGER NOT NULL,
            captured_at   INTEGER NOT NULL,
            kind          INTEGER NOT NULL,
            identity_key  TEXT    NOT NULL,
            withdrawn_at  INTEGER NULL
        );
        CREATE INDEX ix_captures_session ON captures (session_id, captured_at);

        -- Derived current state.
        CREATE TABLE regular_items (
            capture_id          TEXT    NOT NULL PRIMARY KEY REFERENCES captures (id) ON DELETE CASCADE,
            session_id          TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            ordinal             INTEGER NOT NULL,
            identity_key        TEXT    NOT NULL,
            pos_x               INTEGER NOT NULL,
            pos_y               INTEGER NOT NULL,
            last_seen_at        INTEGER NOT NULL,
            possible_duplicate  INTEGER NOT NULL,
            needs_review        INTEGER NOT NULL
        );
        CREATE INDEX ix_regular_items_session ON regular_items (session_id, ordinal);

        CREATE TABLE stackables (
            session_id       TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            key              TEXT    NOT NULL,
            ordinal          INTEGER NOT NULL,
            item_class       TEXT    NOT NULL,
            rarity           TEXT    NOT NULL,
            name             TEXT    NOT NULL,
            max_stack        INTEGER NOT NULL,
            count            INTEGER NOT NULL,
            latest_raw_text  TEXT    NOT NULL,
            updated_at       INTEGER NOT NULL,
            parser_version   INTEGER NOT NULL,
            PRIMARY KEY (session_id, key)
        );

        -- Undo/redo entries in application order; undone = on the redo stack.
        CREATE TABLE change_entries (
            id            TEXT    NOT NULL PRIMARY KEY,
            session_id    TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            seq           INTEGER NOT NULL,
            timestamp     INTEGER NOT NULL,
            type          INTEGER NOT NULL,
            capture_id    TEXT    NOT NULL REFERENCES captures (id) ON DELETE CASCADE,
            identity_key  TEXT    NOT NULL,
            before_json   TEXT    NULL,
            after_json    TEXT    NULL,
            undone        INTEGER NOT NULL
        );
        CREATE INDEX ix_change_entries_session ON change_entries (session_id, seq);
        """,
        """
        -- Sweeps (PLAN §8). A capture inside a sweep's grid records the sweep and cell; no foreign key, since
        -- the log is append-only and must never cascade away.
        ALTER TABLE captures ADD COLUMN sweep_id TEXT NULL;
        ALTER TABLE captures ADD COLUMN cell_col INTEGER NULL;
        ALTER TABLE captures ADD COLUMN cell_row INTEGER NULL;

        -- JSON array of the capture ids of adjacent cells merged into the item.
        ALTER TABLE regular_items ADD COLUMN covered_json TEXT NULL;

        ALTER TABLE change_entries ADD COLUMN related_capture_id TEXT NULL;
        ALTER TABLE change_entries ADD COLUMN sweep_id TEXT NULL;
        ALTER TABLE change_entries ADD COLUMN children_json TEXT NULL;

        CREATE TABLE sweeps (
            id          TEXT    NOT NULL PRIMARY KEY,
            session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            grid_name   TEXT    NOT NULL,
            columns     INTEGER NOT NULL,
            rows        INTEGER NOT NULL,
            label       TEXT    NOT NULL,
            origin_x    REAL    NOT NULL,
            origin_y    REAL    NOT NULL,
            cell_size   REAL    NOT NULL,
            status      INTEGER NOT NULL,
            started_at  INTEGER NOT NULL,
            ended_at    INTEGER NULL,
            item_count  INTEGER NOT NULL
        );
        CREATE INDEX ix_sweeps_session ON sweeps (session_id, started_at);

        -- Derived state of non-pending cells.
        CREATE TABLE sweep_cells (
            sweep_id    TEXT    NOT NULL REFERENCES sweeps (id) ON DELETE CASCADE,
            session_id  TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            col         INTEGER NOT NULL,
            row         INTEGER NOT NULL,
            state       INTEGER NOT NULL,
            capture_id  TEXT    NULL,
            PRIMARY KEY (sweep_id, col, row)
        );
        CREATE INDEX ix_sweep_cells_session ON sweep_cells (session_id);

        -- Last grid rectangle per area kind and screen resolution, relative to the screen's top-left corner.
        CREATE TABLE grid_calibrations (
            grid_name      TEXT    NOT NULL,
            columns        INTEGER NOT NULL,
            rows           INTEGER NOT NULL,
            screen_width   INTEGER NOT NULL,
            screen_height  INTEGER NOT NULL,
            origin_x       REAL    NOT NULL,
            origin_y       REAL    NOT NULL,
            cell_size      REAL    NOT NULL,
            saved_at       INTEGER NOT NULL,
            PRIMARY KEY (grid_name, columns, rows, screen_width, screen_height)
        );
        """,
        """
        -- Session kind (MAPPING_PLAN.md M0): 0 = free recording (every earlier session), 1 = mapping.
        ALTER TABLE sessions ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;

        -- Continuing an ended session starts a fresh undo history; the earlier entries stay as archived history
        -- (no longer undoable or redoable) and are never rewritten.
        ALTER TABLE change_entries ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;
        """,
        """
        -- JSON array of a stackable's stacks, one per place it was copied (count = their sum). NULL in rows written
        -- before stacks were tracked per place: they load as one stack at the latest capture.
        ALTER TABLE stackables ADD COLUMN stacks_json TEXT NULL;
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public static void Migrate(SqliteConnection connection)
    {
        var version = Convert.ToInt32(connection.ExecuteScalar("PRAGMA user_version"));
        if (version > LatestVersion)
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this app supports ({LatestVersion}).");

        for (; version < LatestVersion; version++)
        {
            using var tx = connection.BeginTransaction();
            connection.Execute(Migrations[version], tx);
            connection.Execute($"PRAGMA user_version = {version + 1}", tx);
            tx.Commit();
        }
    }
}
