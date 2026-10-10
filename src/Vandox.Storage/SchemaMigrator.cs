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
                                                      new(5, ["ALTER TABLE log_lines ADD COLUMN event TEXT NOT NULL DEFAULT ''"])
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
            throw new StoreException("store: reading schema version: " + exception.Message, exception);
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