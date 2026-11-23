using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The connection rollup reads: the states and top programs per step, the top addresses and the list of sources.
/// </summary>
internal static class ConnectionRollupQueries
{
    #region Constants

    /// <summary>
    /// The context of the errors of these reads.
    /// </summary>
    internal const string Context = "store: reading connection rollups";

    #endregion // Constants

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Reads the states and the top programs of a source per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static Task<ConnectionRollupResult> ReadAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the top addresses of a source with the figures per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static Task<ConnectionAddressResult> ReadAddressesAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the sources that have connection rollups in a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sources</returns>
    internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    // Skeleton: removed with the implementation
#pragma warning restore S2325, RH2003

    #endregion // Methods
}