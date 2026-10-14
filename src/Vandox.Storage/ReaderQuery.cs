using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace Vandox.Storage;

/// <summary>
/// Runs statements on reader connections so that a cancelled token interrupts them.
/// </summary>
internal static class ReaderQuery
{
    #region Constants

    /// <summary>
    /// The number of SQLite virtual-machine instructions between two checks of the token.
    /// </summary>
    internal const int ProgressInstructions = 1000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Installs the progress handler that interrupts the connection's running statement once the token is cancelled;
    /// does nothing for a token that cannot be cancelled.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="cancellationToken">The token whose cancellation interrupts the statement</param>
    internal static void Attach(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            raw.sqlite3_progress_handler(connection.Handle, ProgressInstructions, Progress, cancellationToken);
        }
    }

    /// <summary>
    /// Runs a query and maps its rows; an interrupted statement ends in <see cref="OperationCanceledException"/>, any other
    /// SQLite error in <see cref="StoreException"/> with the context.
    /// </summary>
    /// <typeparam name="T">The mapped row type</typeparam>
    /// <param name="connection">The open reader connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The statement parameters by name</param>
    /// <param name="map">Maps the current row of the reader</param>
    /// <param name="context">The operation named in an error message</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that returns the mapped rows</returns>
    internal static async Task<IReadOnlyList<T>> ReadAsync<T>(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object> parameters, Func<SqliteDataReader, T> map, string context, CancellationToken cancellationToken)
    {
        SqliteException? failure = null;
        SqliteDataReader? reader = null;
        var rows = new List<T>();

        using var command = connection.CreateCommand();

        try
        {
            command.CommandText = sql;

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(map(reader));
            }
        }
        catch (SqliteException exception)
        {
            failure = exception;
        }
        finally
        {
            failure = Dispose(reader, failure);
        }

        if (failure is not null)
        {
            throw Translate(failure, context, cancellationToken);
        }

        return rows;
    }

    /// <summary>
    /// Maps a SQLite error of a reader call to the exception the store throws.
    /// </summary>
    /// <param name="exception">The SQLite error</param>
    /// <param name="context">The operation named in the message</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>The exception to throw</returns>
    internal static Exception Translate(SqliteException exception, string context, CancellationToken cancellationToken)
    {
        if (exception.SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException($"{context}: cancelled", exception, cancellationToken);
        }

        return new StoreException($"{context}: {exception.Message}", exception);
    }

#pragma warning disable RH2003
    /// <summary>
    /// Installs a progress handler that interrupts the running statement when the token is cancelled or the time budget, counted
    /// from this call, is used up.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="timeProvider">The clock of the budget</param>
    /// <param name="budget">The time the statement may take</param>
    /// <param name="cancellationToken">The token whose cancellation interrupts the statement</param>
    internal static void AttachBudget(SqliteConnection connection, TimeProvider timeProvider, TimeSpan budget, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    /// <summary>
    /// The progress callback of SQLite: asks it to stop the running statement once the token is cancelled. It must never
    /// throw, because SQLite calls it from native code.
    /// </summary>
    /// <param name="state">The boxed token</param>
    /// <returns>1 to interrupt the statement, 0 to continue</returns>
    private static int Progress(object state)
    {
        return state is CancellationToken { IsCancellationRequested: true } ? 1 : 0;
    }

    /// <summary>
    /// Disposes the reader. An error of the disposal never replaces an earlier error, so the interruption keeps its
    /// meaning; without an earlier error it is the error of the call.
    /// </summary>
    /// <param name="reader">The reader, or <c>null</c> when it was never opened</param>
    /// <param name="failure">The error of the read, or <c>null</c></param>
    /// <returns>The error to report, or <c>null</c></returns>
    private static SqliteException? Dispose(SqliteDataReader? reader, SqliteException? failure)
    {
        try
        {
            reader?.Dispose();
        }
        catch (SqliteException exception)
        {
            return failure ?? exception;
        }

        return failure;
    }

    #endregion // Methods
}