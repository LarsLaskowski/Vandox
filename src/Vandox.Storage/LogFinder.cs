using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// Finds log lines by index or by a bounded scan, whichever the search text allows.
/// </summary>
internal static class LogFinder
{
    #region Constants

    /// <summary>
    /// The number of index matches below which a text search runs from the index.
    /// </summary>
    internal const int SelectiveMatches = 20000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Finds one page of log lines.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="filter">The validated filters</param>
    /// <param name="template">The template of <see cref="LogFilter.TemplateId"/>, or <c>null</c></param>
    /// <param name="selectiveMatches">The index match count below which the index is used</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    internal static Task<LogPage> FindAsync(SqliteConnection connection, LogFilter filter, LogTemplate? template, int selectiveMatches, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}