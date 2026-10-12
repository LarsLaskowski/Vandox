using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The SQL of a template rebuild: the clear, the run marker check, the counting of a batch and the finish.
/// </summary>
internal static class TemplateRebuilder
{
    #region Constants

    /// <summary>
    /// The key of the run marker in <c>meta</c>.
    /// </summary>
    internal const string MarkerKey = "template_rebuild";

    #endregion // Constants

    #region Fields

    // The hours reference the templates, so they go first.
    private static readonly string[] _clearStatements = ["DELETE FROM log_template_hours", "DELETE FROM log_templates", "DELETE FROM log_source_hours"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates a run marker: 16 random bytes as 32 upper-case hexadecimal characters.
    /// </summary>
    /// <returns>The marker</returns>
    internal static string NewMarker()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// Empties the three template tables, writes the run marker and reads the high-water record ID.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="marker">The run marker</param>
    /// <param name="cancellationToken">Cancels the clear</param>
    /// <returns>A task that returns the highest record ID</returns>
    internal static async Task<long> ClearAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken)
    {
        foreach (var sql in _clearStatements)
        {
            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;
            delete.CommandText = sql;
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var write = connection.CreateCommand();

        write.Transaction = transaction;
        write.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $marker) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        write.Parameters.AddWithValue("$key", MarkerKey);
        write.Parameters.AddWithValue("$marker", marker);
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await ScalarAsync(connection, transaction, "SELECT coalesce(max(id), 0) FROM records", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that the run marker in <c>meta</c> is the given one.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="marker">The run marker</param>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that returns <c>false</c> when the row is missing or holds another value</returns>
    internal static async Task<bool> HoldsMarkerAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", MarkerKey);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string text && string.Equals(text, marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counts the next batch of stored log lines into the template tables.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="afterId">The record ID after which the batch starts</param>
    /// <param name="highId">The high-water record ID</param>
    /// <param name="batchLines">The most lines the batch counts</param>
    /// <param name="cancellationToken">Cancels the batch</param>
    /// <returns>A task that returns the last record ID counted (<paramref name="afterId"/> when no line is left) and the lines counted</returns>
    internal static async Task<(long LastId, int Lines)> CountBatchAsync(SqliteConnection connection, SqliteTransaction transaction, long afterId, long highId, int batchLines, CancellationToken cancellationToken)
    {
        using var tally = new TemplateTally(connection, transaction);
        var lastId = afterId;
        var lines = 0;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT l.record_id, r.source, l.program, l.message, r.captured_at FROM log_lines l JOIN records r ON r.id = l.record_id "
                                  + "WHERE l.record_id > $after AND l.record_id <= $high ORDER BY l.record_id LIMIT $limit";
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$high", highId);
            command.Parameters.AddWithValue("$limit", batchLines);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lastId = reader.GetInt64(0);
                tally.Add(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
                lines++;
            }
        }

        tally.Write(cancellationToken);

        return (lastId, lines);
    }

    /// <summary>
    /// Deletes the run marker.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="cancellationToken">Cancels the finish</param>
    /// <returns>A task that returns the number of rows of <c>log_templates</c></returns>
    internal static async Task<long> FinishAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM meta WHERE key = $key";
            delete.Parameters.AddWithValue("$key", MarkerKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ScalarAsync(connection, transaction, "SELECT count(*) FROM log_templates", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a query that returns one integer.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="sql">The SQL</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that returns the value</returns>
    private static async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    #endregion // Methods
}