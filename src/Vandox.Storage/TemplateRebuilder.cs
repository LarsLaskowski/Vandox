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

    // The hours reference the templates, so they go first; Microsoft.Data.Sqlite runs the statements of one text in order.
    private const string ClearStatements = "DELETE FROM log_template_hours; DELETE FROM log_templates; DELETE FROM log_source_hours";

    // A partial rebuild clears the hours from a time on, drops the templates left without an hour, and sets the others back to the
    // sum of their kept hours; the counting then adds the recounted hours.
    private const string ClearFromStatements = "DELETE FROM log_template_hours WHERE hour >= $keep; DELETE FROM log_source_hours WHERE hour >= $keep; "
                                               + "DELETE FROM log_templates WHERE NOT EXISTS (SELECT 1 FROM log_template_hours AS h WHERE h.template_id = log_templates.id); "
                                               + "UPDATE log_templates SET count = (SELECT sum(h.count) FROM log_template_hours AS h WHERE h.template_id = log_templates.id), "
                                               + "last_seen = min(last_seen, (SELECT max(h.hour) FROM log_template_hours AS h WHERE h.template_id = log_templates.id) + 3599999999999)";

    #endregion // Constants

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
    internal static Task<long> ClearAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken)
    {
        return ClearAsync(connection, transaction, marker, null, cancellationToken);
    }

    /// <summary>
    /// Empties the three template tables, or only their hours from a time on, writes the run marker and reads the high-water record ID.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="marker">The run marker</param>
    /// <param name="keepBefore">The start of the hours to recount in nanoseconds; earlier hours stay; <c>null</c> clears everything</param>
    /// <param name="cancellationToken">Cancels the clear</param>
    /// <returns>A task that returns the highest record ID</returns>
    internal static async Task<long> ClearAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, long? keepBefore, CancellationToken cancellationToken)
    {
        var delete = connection.CreateCommand();

        await using (delete.ConfigureAwait(false))
        {
            delete.Transaction = transaction;
#pragma warning disable CA2100 // constant statements, the time is bound as a parameter
            delete.CommandText = keepBefore is null ? ClearStatements : ClearFromStatements;
#pragma warning restore CA2100

            if (keepBefore is not null)
            {
                delete.Parameters.AddWithValue("$keep", keepBefore.Value);
            }

            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var write = connection.CreateCommand();
        await using var writeDisposal = write.ConfigureAwait(false);

        write.Transaction = transaction;
        write.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $marker) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        write.Parameters.AddWithValue("$key", MarkerKey);
        write.Parameters.AddWithValue("$marker", marker);
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var high = connection.CreateCommand();
        await using var highDisposal = high.ConfigureAwait(false);

        high.Transaction = transaction;
        high.CommandText = "SELECT coalesce(max(id), 0) FROM records";

        return Convert.ToInt64(await high.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
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
        var command = connection.CreateCommand();
        await using var commandDisposal = command.ConfigureAwait(false);

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
    internal static Task<(long LastId, int Lines)> CountBatchAsync(SqliteConnection connection, SqliteTransaction transaction, long afterId, long highId, int batchLines, CancellationToken cancellationToken)
    {
        return CountBatchAsync(connection, transaction, afterId, highId, batchLines, null, cancellationToken);
    }

    /// <summary>
    /// Counts the next batch of stored log lines captured from a time on into the template tables.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="afterId">The record ID after which the batch starts</param>
    /// <param name="highId">The high-water record ID</param>
    /// <param name="batchLines">The most lines the batch counts</param>
    /// <param name="keepBefore">The capture time in nanoseconds from which lines are counted; <c>null</c> counts every line</param>
    /// <param name="cancellationToken">Cancels the batch</param>
    /// <returns>A task that returns the last record ID counted (<paramref name="afterId"/> when no line is left) and the lines counted</returns>
    internal static async Task<(long LastId, int Lines)> CountBatchAsync(SqliteConnection connection, SqliteTransaction transaction, long afterId, long highId, int batchLines, long? keepBefore, CancellationToken cancellationToken)
    {
        using var tally = new TemplateTally(connection, transaction);
        var lastId = afterId;
        var lines = 0;

        var command = connection.CreateCommand();

        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT l.record_id, r.source, l.program, l.message, r.captured_at FROM log_lines l JOIN records r ON r.id = l.record_id "
                                  + "WHERE l.record_id > $after AND l.record_id <= $high AND r.captured_at >= $keep ORDER BY l.record_id LIMIT $limit";
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$high", highId);
            command.Parameters.AddWithValue("$limit", batchLines);
            command.Parameters.AddWithValue("$keep", keepBefore ?? long.MinValue);

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerDisposal = reader.ConfigureAwait(false);

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
        var delete = connection.CreateCommand();

        await using (delete.ConfigureAwait(false))
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM meta WHERE key = $key";
            delete.Parameters.AddWithValue("$key", MarkerKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = connection.CreateCommand();
        await using var countDisposal = count.ConfigureAwait(false);

        count.Transaction = transaction;
        count.CommandText = "SELECT count(*) FROM log_templates";

        return Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    #endregion // Methods
}