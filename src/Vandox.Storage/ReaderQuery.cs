using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
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
    internal static Task<IReadOnlyList<T>> ReadAsync<T>(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object> parameters, Func<SqliteDataReader, T> map, string context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    #endregion // Methods
}