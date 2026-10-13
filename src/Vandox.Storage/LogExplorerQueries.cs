using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// The SQL of the context, sources, template list and source hours of the log exploration.
/// </summary>
internal static class LogExplorerQueries
{
    #region Methods

    /// <summary>
    /// Reads the context of a log line.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the context</returns>
    internal static Task<LogContext> ContextAsync(SqliteConnection connection, LogContextQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the SQL of the source list.
    /// </summary>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildSources()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the SQL of a template list.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateListQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the SQL of the source hours.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(SourceHourQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads one row of the template list.
    /// </summary>
    /// <param name="reader">The reader on a row</param>
    /// <returns>The template with its count</returns>
    internal static TemplateCount ReadTemplateCount(SqliteDataReader reader)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}