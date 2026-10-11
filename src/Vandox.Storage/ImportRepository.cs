using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.LogParsing;

namespace Vandox.Storage;

/// <summary>
/// Reads and writes the import state of file contents.
/// </summary>
internal static class ImportRepository
{
    #region Constants

    private const string InsertImportFile = "INSERT INTO import_files(sha256, size, name, file_name, mod_time, source_type, started_at, head_sha256, continues_id, start_offset) "
                                            + "VALUES ($sha, $size, $name, $file, $mod, $type, $started, $head, $continues, $offset) ON CONFLICT(sha256) DO NOTHING";

    private const string SelectImportFile = "SELECT f.id, f.size, f.name, f.file_name, f.mod_time, f.source_type, f.records, f.complete, f.started_at, f.completed_at, "
                                            + "f.continues_id, f.start_offset, b.name "
                                            + "FROM import_files f LEFT JOIN import_files b ON b.id = f.continues_id WHERE f.sha256 = $sha";

    private const string SelectExisting = "SELECT 1 FROM import_files WHERE sha256 = $sha";

    private const string SelectBase = "SELECT id FROM import_files WHERE sha256 = $sha AND size = $size AND source_type = $type AND complete = 1";

    private const string SelectBases = "SELECT sha256, size FROM import_files WHERE head_sha256 = $head AND source_type = $type AND complete = 1 "
                                       + "ORDER BY size DESC LIMIT 64";

    private const int Sha256Bytes = 32;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Refuses an import start the store must not keep.
    /// </summary>
    /// <param name="start">The import start</param>
    /// <exception cref="InvalidImportException">The start breaks a rule</exception>
    internal static void Validate(ImportFileStart start)
    {
        Require(start.StartedAt != default, "started_at is required");
        Require(start.StartedAt.Offset == TimeSpan.Zero, "started_at must be UTC");
        Require(StorageTime.InStorableRange(start.StartedAt), "started_at is outside the storable range");
        Require(start.Sha256.Length == Sha256Bytes, "sha256 must be 32 bytes");
        Require(start.Size >= 0, "size must not be negative");
        Require(Encoding.UTF8.GetByteCount(start.FileName) <= StorageLimits.MaxImportNameBytes, $"file name is longer than {StorageLimits.MaxImportNameBytes} bytes");
        Require(ParserTypes.IsValid(start.SourceType), "source type must match ^[a-z][a-z0-9._-]{0,63}$");
        Require(start.HeadSha256.Length == Sha256Bytes, "head_sha256 must be 32 bytes");
        Require(start.Prefixes.Count <= StorageLimits.MaxImportBases, $"at most {StorageLimits.MaxImportBases} prefixes");

        foreach (var prefix in start.Prefixes)
        {
            Require(prefix.Sha256.Length == Sha256Bytes, "prefix sha256 must be 32 bytes");
            Require(prefix.Size > 0 && prefix.Size < start.Size, "prefix size must be positive and smaller than size");
            Require(prefix.Offset > 0 && prefix.Offset <= prefix.Size, "prefix offset must be positive and at most the prefix size");
        }
    }

    /// <summary>
    /// Refuses a base lookup the store must not run.
    /// </summary>
    /// <param name="headSha256">The head key</param>
    /// <param name="sourceType">The parser type</param>
    /// <exception cref="InvalidImportException">The head key or the source type breaks a rule</exception>
    internal static void ValidateLookup(byte[] headSha256, string sourceType)
    {
        Require(headSha256.Length == Sha256Bytes, "head_sha256 must be 32 bytes");
        Require(ParserTypes.IsValid(sourceType), "source type must match ^[a-z][a-z0-9._-]{0,63}$");
    }

    /// <summary>
    /// Returns the completely imported contents with a head key and source type, largest first, at most
    /// <see cref="StorageLimits.MaxImportBases"/>.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="headSha256">The head key; 32 bytes</param>
    /// <param name="sourceType">The parser type</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the contents</returns>
    internal static async Task<IReadOnlyList<ImportBase>> FindBasesAsync(SqliteConnection connection, byte[] headSha256, string sourceType, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = SelectBases;
        command.Parameters.AddWithValue("$head", headSha256);
        command.Parameters.AddWithValue("$type", sourceType);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var bases = new List<ImportBase>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bases.Add(new ImportBase
                      {
                          Sha256 = (byte[])reader.GetValue(0),
                          Size = reader.GetInt64(1)
                      });
        }

        return bases;
    }

    /// <summary>
    /// Returns the import state of a content, creating it when it is unknown.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="start">The import start</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the import state</returns>
    internal static async Task<ImportFile> BeginAsync(SqliteConnection connection, ImportFileStart start, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var (continuesId, offset) = await ChooseBaseAsync(connection, transaction, start, cancellationToken).ConfigureAwait(false);

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = InsertImportFile;
            insert.Parameters.AddWithValue("$sha", start.Sha256);
            insert.Parameters.AddWithValue("$size", start.Size);
            insert.Parameters.AddWithValue("$name", CutName(start.Name));
            insert.Parameters.AddWithValue("$file", Encoding.UTF8.GetBytes(start.FileName));
            insert.Parameters.AddWithValue("$mod", start.ModTime is { } mod && StorageTime.InStorableRange(mod) ? StorageTime.ToNanoseconds(mod) : DBNull.Value);
            insert.Parameters.AddWithValue("$type", start.SourceType);
            insert.Parameters.AddWithValue("$started", StorageTime.ToNanoseconds(start.StartedAt));
            insert.Parameters.AddWithValue("$head", start.HeadSha256);
            insert.Parameters.AddWithValue("$continues", continuesId is { } id ? id : DBNull.Value);
            insert.Parameters.AddWithValue("$offset", offset);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var file = await SelectAsync(connection, transaction, start.Sha256, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return file;
    }

    /// <summary>
    /// Applies an import step to the import state in a transaction, for a batch of <paramref name="count"/> records.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction of the batch</param>
    /// <param name="step">The step</param>
    /// <param name="count">The number of records of the batch</param>
    /// <param name="receivedAt">The instant the batch was received</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the step is applied</returns>
    /// <exception cref="ImportConflictException">The stored count differs from the step's, or the file is complete</exception>
    internal static async Task AdvanceAsync(SqliteConnection connection, SqliteTransaction transaction, ImportStep step, int count, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "UPDATE import_files SET records = records + $count, complete = $complete, completed_at = $completed "
                              + "WHERE id = $id AND records = $done AND complete = 0";
        command.Parameters.AddWithValue("$count", (long)count);
        command.Parameters.AddWithValue("$complete", step.Complete ? 1L : 0L);
        command.Parameters.AddWithValue("$completed", step.Complete ? StorageTime.ToNanoseconds(receivedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$id", step.FileId);
        command.Parameters.AddWithValue("$done", step.Done);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ImportConflictException($"store: import state changed: file {step.FileId} does not have {step.Done} records stored and is not complete");
        }
    }

    /// <summary>
    /// Cuts a name to <see cref="StorageLimits.MaxImportNameBytes"/> UTF-8 bytes at a character boundary.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns>The name, cut when needed</returns>
    internal static string CutName(string name)
    {
        var bytes = 0;
        var builder = new StringBuilder();

        foreach (var rune in name.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;

            if (bytes > StorageLimits.MaxImportNameBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Chooses the base a new content continues: the largest prefix whose content is stored complete with the same size and
    /// source type. An existing state keeps the continuation it has.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="start">The import start</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the id of the base row and the continuation point; no base and 0 when there is none</returns>
    private static async Task<(long? ContinuesId, long Offset)> ChooseBaseAsync(SqliteConnection connection, SqliteTransaction transaction, ImportFileStart start, CancellationToken cancellationToken)
    {
        if (start.Prefixes.Count == 0)
        {
            return (null, 0);
        }

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = SelectExisting;
            existing.Parameters.AddWithValue("$sha", start.Sha256);

            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return (null, 0);
            }
        }

        foreach (var prefix in start.Prefixes.OrderByDescending(item => item.Size))
        {
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = SelectBase;
            command.Parameters.AddWithValue("$sha", prefix.Sha256);
            command.Parameters.AddWithValue("$size", prefix.Size);
            command.Parameters.AddWithValue("$type", start.SourceType);

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long id)
            {
                return (id, prefix.Offset);
            }
        }

        return (null, 0);
    }

    /// <summary>
    /// Throws an error for a refused import start unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">The rule that is broken</param>
    private static void Require(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidImportException($"store: invalid import: {rule}");
    }

    /// <summary>
    /// Reads the import state of a content.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="sha256">The SHA-256 of the content</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the state</returns>
    private static async Task<ImportFile> SelectAsync(SqliteConnection connection, SqliteTransaction transaction, byte[] sha256, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = SelectImportFile;
        command.Parameters.AddWithValue("$sha", sha256);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ImportFile
                   {
                       Id = reader.GetInt64(0),
                       Sha256 = sha256,
                       Size = reader.GetInt64(1),
                       Name = reader.GetString(2),
                       FileName = Encoding.UTF8.GetString((byte[])reader.GetValue(3)),
                       ModTime = reader.IsDBNull(4) ? null : StorageTime.FromNanoseconds(reader.GetInt64(4)),
                       SourceType = reader.GetString(5),
                       Records = reader.GetInt64(6),
                       Complete = reader.GetInt64(7) == 1,
                       StartedAt = StorageTime.FromNanoseconds(reader.GetInt64(8)),
                       CompletedAt = reader.IsDBNull(9) ? null : StorageTime.FromNanoseconds(reader.GetInt64(9)),
                       ContinuesId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                       Offset = reader.GetInt64(11),
                       ContinuedFrom = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
                   };
        }

        throw new StoreException("store: beginning import: the import state was not found after it was created");
    }

    #endregion // Methods
}