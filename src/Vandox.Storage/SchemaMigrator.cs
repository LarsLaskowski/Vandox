using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// Applies the schema steps of the database: every step above the stored <c>meta.schema_version</c> runs in its own
/// transaction, and a database with a newer version is refused.
/// </summary>
internal static class SchemaMigrator
{
    #region Fields

    private static readonly SchemaStep[] _steps = [
                                                      new(1, ["CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT"]),
                                                      new(2,
                                                          [
                                                              """
                                                              CREATE TABLE records (
                                                                id INTEGER PRIMARY KEY,
                                                                kind TEXT NOT NULL,
                                                                origin TEXT NOT NULL,
                                                                source TEXT NOT NULL,
                                                                agent_id TEXT,
                                                                seq INTEGER,
                                                                captured_at INTEGER NOT NULL,
                                                                received_at INTEGER NOT NULL,
                                                                boot_id TEXT,
                                                                clock_offset_ns INTEGER,
                                                                data TEXT
                                                              ) STRICT
                                                              """,
                                                              "CREATE UNIQUE INDEX records_agent_seq ON records(agent_id, seq) WHERE origin = 'agent'",
                                                              "CREATE INDEX records_kind_source_time ON records(kind, source, captured_at)",
                                                              "CREATE INDEX records_kind_time ON records(kind, captured_at)",
                                                              """
                                                              CREATE TABLE metrics (
                                                                record_id INTEGER PRIMARY KEY REFERENCES records(id),
                                                                source TEXT NOT NULL,
                                                                name TEXT NOT NULL,
                                                                captured_at INTEGER NOT NULL,
                                                                value REAL NOT NULL,
                                                                unit TEXT NOT NULL,
                                                                labels TEXT
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX metrics_source_name_time ON metrics(source, name, captured_at)",
                                                              """
                                                              CREATE TABLE log_lines (
                                                                record_id INTEGER PRIMARY KEY REFERENCES records(id),
                                                                log TEXT NOT NULL,
                                                                program TEXT NOT NULL,
                                                                pid INTEGER NOT NULL,
                                                                priority INTEGER,
                                                                message TEXT NOT NULL,
                                                                truncated INTEGER NOT NULL
                                                              ) STRICT
                                                              """,
                                                              """
                                                              CREATE VIRTUAL TABLE log_fts USING fts5(message, content='log_lines', content_rowid='record_id',
                                                                tokenize='unicode61 remove_diacritics 2')
                                                              """
                                                          ]),
                                                      new(3,
                                                          [
                                                              """
                                                              CREATE TABLE import_files (
                                                                id INTEGER PRIMARY KEY,
                                                                sha256 BLOB NOT NULL UNIQUE CHECK (length(sha256) = 32),
                                                                size INTEGER NOT NULL CHECK (size >= 0),
                                                                name TEXT NOT NULL,
                                                                file_name BLOB NOT NULL CHECK (length(file_name) <= 1024),
                                                                mod_time INTEGER,
                                                                source_type TEXT NOT NULL,
                                                                records INTEGER NOT NULL DEFAULT 0 CHECK (records >= 0),
                                                                complete INTEGER NOT NULL DEFAULT 0 CHECK (complete IN (0, 1)),
                                                                started_at INTEGER NOT NULL,
                                                                completed_at INTEGER
                                                              ) STRICT
                                                              """
                                                          ]),
                                                      new(4, ["ALTER TABLE log_lines ADD COLUMN host TEXT NOT NULL DEFAULT ''"]),
                                                      new(5, ["ALTER TABLE log_lines ADD COLUMN event TEXT NOT NULL DEFAULT ''"]),
                                                      new(6,
                                                          [
                                                              "ALTER TABLE import_files ADD COLUMN head_sha256 BLOB CHECK (head_sha256 IS NULL OR length(head_sha256) = 32)",
                                                              "ALTER TABLE import_files ADD COLUMN continues_id INTEGER",
                                                              "ALTER TABLE import_files ADD COLUMN start_offset INTEGER NOT NULL DEFAULT 0 CHECK (start_offset >= 0)",
                                                              "CREATE INDEX import_files_head ON import_files(head_sha256, source_type, size) WHERE complete = 1 AND head_sha256 IS NOT NULL"
                                                          ]),
                                                      new(7,
                                                          [
                                                              "ALTER TABLE import_files ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0)",
                                                              "ALTER TABLE import_files ADD COLUMN hashes_attempt INTEGER",
                                                              """
                                                              CREATE TABLE import_line_hashes (
                                                                file_id INTEGER NOT NULL,
                                                                attempt INTEGER NOT NULL CHECK (attempt > 0),
                                                                first_offset INTEGER NOT NULL CHECK (first_offset >= 0),
                                                                last_offset INTEGER NOT NULL CHECK (last_offset > first_offset),
                                                                hashes BLOB NOT NULL CHECK (length(hashes) BETWEEN 16 AND 65536 AND length(hashes) % 16 = 0),
                                                                PRIMARY KEY (file_id, attempt, last_offset)
                                                              ) STRICT
                                                              """
                                                          ]),
                                                      new(8,
                                                          [
                                                              """
                                                              CREATE TABLE log_templates (
                                                                id INTEGER PRIMARY KEY,
                                                                source TEXT NOT NULL,
                                                                program TEXT NOT NULL,
                                                                template TEXT NOT NULL,
                                                                first_seen INTEGER NOT NULL,
                                                                last_seen INTEGER NOT NULL,
                                                                count INTEGER NOT NULL CHECK (count > 0),
                                                                UNIQUE (source, program, template)
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX log_templates_source_first_seen ON log_templates(source, first_seen)",
                                                              """
                                                              CREATE TABLE log_template_hours (
                                                                template_id INTEGER NOT NULL REFERENCES log_templates(id),
                                                                hour INTEGER NOT NULL,
                                                                count INTEGER NOT NULL CHECK (count > 0),
                                                                PRIMARY KEY (template_id, hour)
                                                              ) STRICT, WITHOUT ROWID
                                                              """,
                                                              "CREATE INDEX log_template_hours_hour ON log_template_hours(hour)",
                                                              """
                                                              CREATE TABLE log_source_hours (
                                                                source TEXT NOT NULL,
                                                                hour INTEGER NOT NULL,
                                                                count INTEGER NOT NULL CHECK (count > 0),
                                                                PRIMARY KEY (source, hour)
                                                              ) STRICT, WITHOUT ROWID
                                                              """
                                                          ]),
                                                      new(9,
                                                          [
                                                              """
                                                              CREATE TABLE incidents (
                                                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                                                started_at INTEGER NOT NULL,
                                                                ended_at INTEGER NOT NULL CHECK (ended_at > started_at),
                                                                first_kill_at INTEGER NOT NULL,
                                                                kill_count INTEGER NOT NULL CHECK (kill_count > 0),
                                                                created_at INTEGER NOT NULL,
                                                                updated_at INTEGER NOT NULL,
                                                                data TEXT NOT NULL
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX incidents_started_at ON incidents(started_at)"
                                                          ]),
                                                      new(10,
                                                          [
                                                              """
                                                              CREATE TABLE incident_notes (
                                                                written_for INTEGER PRIMARY KEY,
                                                                incident_id INTEGER NOT NULL REFERENCES incidents(id),
                                                                text TEXT NOT NULL CHECK (length(CAST(text AS BLOB)) BETWEEN 1 AND 5400),
                                                                updated_at INTEGER NOT NULL
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX incident_notes_incident ON incident_notes(incident_id, written_for)"
                                                          ]),
                                                      new(11,
                                                          [
                                                              """
                                                              CREATE TABLE connection_marks (
                                                                record_id INTEGER PRIMARY KEY REFERENCES records(id),
                                                                agent_id TEXT NOT NULL,
                                                                seq INTEGER NOT NULL,
                                                                captured_at INTEGER NOT NULL,
                                                                listener_check TEXT NOT NULL,
                                                                new_count INTEGER NOT NULL CHECK (new_count >= 0),
                                                                new_listeners TEXT
                                                              ) STRICT
                                                              """,
                                                              "CREATE UNIQUE INDEX connection_marks_agent_seq ON connection_marks(agent_id, seq)",
                                                              "CREATE INDEX connection_marks_findings ON connection_marks(captured_at DESC, agent_id, seq DESC) WHERE new_count > 0",
                                                              """
                                                              CREATE TABLE listener_comparisons (
                                                                record_id INTEGER PRIMARY KEY REFERENCES records(id),
                                                                agent_id TEXT NOT NULL,
                                                                seq INTEGER NOT NULL,
                                                                captured_at INTEGER NOT NULL,
                                                                base_record_id INTEGER,
                                                                base_seq INTEGER,
                                                                base_captured_at INTEGER,
                                                                outcome TEXT NOT NULL,
                                                                new_count INTEGER NOT NULL CHECK (new_count >= 0),
                                                                new_listeners TEXT,
                                                                finding INTEGER NOT NULL CHECK (finding IN (0, 1))
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX listener_comparisons_findings ON listener_comparisons(captured_at DESC, agent_id, seq DESC) WHERE finding = 1"
                                                          ]),
                                                      new(12,
                                                          [
                                                              "ALTER TABLE records ADD COLUMN live INTEGER NOT NULL DEFAULT 0 CHECK (live IN (0, 1))",
                                                              """
                                                              CREATE TABLE agent_streams (
                                                                agent_id TEXT PRIMARY KEY,
                                                                floor_seq INTEGER NOT NULL,
                                                                max_seq INTEGER NOT NULL,
                                                                max_captured_at INTEGER NOT NULL,
                                                                fill_at INTEGER NOT NULL,
                                                                open_gaps INTEGER NOT NULL DEFAULT 0 CHECK (open_gaps >= 0),
                                                                offset_ns INTEGER,
                                                                measured_at INTEGER,
                                                                clock_off INTEGER NOT NULL DEFAULT 0 CHECK (clock_off IN (0, 1)),
                                                                last_bound_ns INTEGER,
                                                                last_captured_at INTEGER,
                                                                last_received_at INTEGER
                                                              ) STRICT, WITHOUT ROWID
                                                              """,
                                                              """
                                                              INSERT INTO agent_streams(agent_id, floor_seq, max_seq, max_captured_at, fill_at, open_gaps)
                                                              SELECT agent_id, seq, seq, captured_at, received_at, 0
                                                              FROM (SELECT agent_id, seq, captured_at, received_at, max(seq) FROM records WHERE origin = 'agent' AND agent_id IS NOT NULL GROUP BY agent_id)
                                                              """,
                                                              """
                                                              CREATE TABLE agent_clock_samples (
                                                                agent_id TEXT NOT NULL,
                                                                sort TEXT NOT NULL CHECK (sort IN ('counted', 'run')),
                                                                bucket INTEGER NOT NULL,
                                                                bound_ns INTEGER NOT NULL,
                                                                PRIMARY KEY (agent_id, sort, bucket)
                                                              ) STRICT, WITHOUT ROWID
                                                              """,
                                                              """
                                                              CREATE TABLE agent_sample_times (
                                                                agent_id TEXT NOT NULL,
                                                                captured_at INTEGER NOT NULL,
                                                                min_seq INTEGER NOT NULL,
                                                                max_seq INTEGER NOT NULL,
                                                                PRIMARY KEY (agent_id, captured_at)
                                                              ) STRICT, WITHOUT ROWID
                                                              """,
                                                              """
                                                              CREATE TABLE gaps (
                                                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                                                agent_id TEXT NOT NULL,
                                                                kind TEXT NOT NULL CHECK (kind IN ('sequence', 'time')),
                                                                cause TEXT NOT NULL,
                                                                state TEXT NOT NULL CHECK (state IN ('open', 'closed', 'final')),
                                                                first_seq INTEGER,
                                                                last_seq INTEGER,
                                                                from_seq INTEGER,
                                                                to_seq INTEGER,
                                                                from_at INTEGER NOT NULL,
                                                                to_at INTEGER NOT NULL,
                                                                detected_at INTEGER NOT NULL,
                                                                changed_at INTEGER NOT NULL,
                                                                CHECK (first_seq <= last_seq)
                                                              ) STRICT
                                                              """,
                                                              "CREATE INDEX gaps_seq ON gaps(agent_id, first_seq) WHERE kind = 'sequence' AND state <> 'closed'",
                                                              "CREATE INDEX gaps_seq_open ON gaps(agent_id, first_seq) WHERE kind = 'sequence' AND state = 'open'",
                                                              "CREATE UNIQUE INDEX gaps_time ON gaps(agent_id, from_at) WHERE kind = 'time' AND state <> 'closed'",
                                                              "CREATE INDEX gaps_agent ON gaps(agent_id, id)"
                                                          ])
                                                  ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Migrates the database to <see cref="StorageLimits.SchemaVersion"/>.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="cancellationToken">Cancels the migration</param>
    /// <returns>A task that completes when the schema is current</returns>
    /// <exception cref="StoreException">The database has a newer schema version, or a step fails</exception>
    internal static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var latest = _steps[^1].Version;

        foreach (var step in _steps)
        {
            await ApplyStepAsync(connection, step, latest, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the schema version stored in the meta table.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the version text</returns>
    internal static async Task<string> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";

        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value as string ?? throw new StoreException("store: reading schema version: no version is stored");
        }
        catch (SqliteException exception)
        {
            throw ReaderQuery.Translate(exception, "store: reading schema version", cancellationToken);
        }
    }

    /// <summary>
    /// Runs a step in one transaction unless the database is already at or above its version. The version is read inside
    /// the transaction, which starts as an immediate one, so concurrent openers apply a step once.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="step">The step</param>
    /// <param name="latest">The version this build uses</param>
    /// <param name="cancellationToken">Cancels the step</param>
    /// <returns>A task that completes when the step is applied or skipped</returns>
    private static async Task ApplyStepAsync(SqliteConnection connection, SchemaStep step, int latest, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var current = await CurrentVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            if (current > latest)
            {
                throw new StoreException($"store: schema version {current} is not supported, this build uses {latest}");
            }

            if (current >= step.Version)
            {
                return;
            }

            foreach (var statement in step.Statements)
            {
                using var command = connection.CreateCommand();

                command.Transaction = transaction;
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using var upsert = connection.CreateCommand();

            upsert.Transaction = transaction;
            upsert.CommandText = "INSERT INTO meta(key, value) VALUES ('schema_version', $version) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            upsert.Parameters.AddWithValue("$version", step.Version.ToString(CultureInfo.InvariantCulture));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: migration {step.Version}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Reads the schema version inside a transaction; it is 0 when the meta table or its row is missing.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the version</returns>
    private static async Task<int> CurrentVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var tables = connection.CreateCommand();

        tables.Transaction = transaction;
        tables.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";

        if (Convert.ToInt64(await tables.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        using var version = connection.CreateCommand();

        version.Transaction = transaction;
        version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";

        if (await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string text)
        {
            return 0;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new StoreException("store: schema version is not a version number");
    }

    #endregion // Methods
}