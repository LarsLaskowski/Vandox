using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The per-program rollup reads: the top programs of a source per step and the list of sources.
/// </summary>
internal static class ProgramRollupQueries
{
    #region Constants

    /// <summary>
    /// The context of the errors of these reads.
    /// </summary>
    internal const string Context = "store: reading program rollups";

    #endregion // Constants

    #region Methods

#pragma warning disable RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Validates a per-program rollup query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query is invalid</exception>
    internal static void Validate(ProgramRollupQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the top programs of a source per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static Task<ProgramRollupResult> ReadAsync(SqliteConnection connection, ProgramRollupQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Validates a program source query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query is invalid</exception>
    internal static void ValidateSources(ProgramSourceQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the sources that have program rollups in a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sources</returns>
    internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003

    #endregion // Methods
}