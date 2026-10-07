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

    private const string InsertImportFile = "INSERT INTO import_files(sha256, size, name, file_name, mod_time, source_type, started_at) "
                                            + "VALUES ($sha, $size, $name, $file, $mod, $type, $started) ON CONFLICT(sha256) DO NOTHING";

    private const string SelectImportFile = "SELECT id, size, name, file_name, mod_time, source_type, records, complete, started_at, completed_at "
                                            + "FROM import_files WHERE sha256 = $sha";

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
                       CompletedAt = reader.IsDBNull(9) ? null : StorageTime.FromNanoseconds(reader.GetInt64(9))
                   };
        }

        throw new StoreException("store: beginning import: the import state was not found after it was created");
    }

    #endregion // Methods
}