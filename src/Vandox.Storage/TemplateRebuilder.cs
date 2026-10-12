using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325
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

    #region Methods

    /// <summary>
    /// Creates a run marker: 16 random bytes as 32 upper-case hexadecimal characters.
    /// </summary>
    /// <returns>The marker</returns>
    internal static string NewMarker()
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks that the run marker in <c>meta</c> is the given one.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="marker">The run marker</param>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that returns <c>false</c> when the row is missing or holds another value</returns>
    internal static Task<bool> HoldsMarkerAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Deletes the run marker.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="cancellationToken">Cancels the finish</param>
    /// <returns>A task that returns the number of rows of <c>log_templates</c></returns>
    internal static Task<long> FinishAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}