using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Templates;

namespace Vandox.Storage;

/// <summary>
/// Finds log lines by index or by a bounded scan, whichever the search text allows.
/// </summary>
internal static class LogFinder
{
    #region Constants

    /// <summary>
    /// The number of index matches below which a text search runs from the index.
    /// </summary>
    internal const int SelectiveMatches = 12000;

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
    internal static async Task<LogPage> FindAsync(SqliteConnection connection, LogFilter filter, LogTemplate? template, int selectiveMatches, CancellationToken cancellationToken)
    {
        var criteria = LogCriteria.Create(filter, template);

        if (criteria is null)
        {
            return new LogPage();
        }

        var match = criteria.Text.Length > 0 ? FtsQuery.Build(criteria.Text) : null;

        if (match is not null)
        {
            var ids = await ReadMatchesAsync(connection, match, selectiveMatches, cancellationToken).ConfigureAwait(false);

            if (ids.Count == 0)
            {
                return new LogPage();
            }

            if (ids.Count < selectiveMatches)
            {
                return await FindFromIndexAsync(connection, criteria, ids, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ScanAsync(connection, criteria, match, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the record IDs of the index matches of a text up to a bound.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="match">The FTS5 expression</param>
    /// <param name="selectiveMatches">The bound</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the record IDs, at most the bound</returns>
    private static async Task<List<long>> ReadMatchesAsync(SqliteConnection connection, string match, int selectiveMatches, CancellationToken cancellationToken)
    {
        var (sql, parameters) = LogFinderQueries.BuildProbe(match, selectiveMatches);

        var ids = new List<long>();

        using var command = CreateCommand(connection, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// Finds a page from the record IDs the index matched.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="ids">The record IDs the index matched</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    private static async Task<LogPage> FindFromIndexAsync(SqliteConnection connection, LogCriteria criteria, List<long> ids, CancellationToken cancellationToken)
    {
        var (sql, parameters) = LogFinderQueries.BuildIndex(criteria, ids);

        var lines = new List<StoredRecord>();

        using var command = CreateCommand(connection, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (lines.Count < criteria.Limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var line = RecordQueries.Read(reader);

            if (criteria.Template is null || IsLineOf(criteria.Template, ((LogLine)line.Record.Data!).Message))
            {
                lines.Add(line);
            }
        }

        return new LogPage
               {
                   Lines = lines,
                   Next = lines.Count >= criteria.Limit ? PositionOf(lines[^1]) : null
               };
    }

    /// <summary>
    /// Finds a page by walking the lines of the range in order.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="match">The FTS5 expression, or <c>null</c></param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    private static async Task<LogPage> ScanAsync(SqliteConnection connection, LogCriteria criteria, string? match, CancellationToken cancellationToken)
    {
        var (kept, last, examined) = await WalkAsync(connection, criteria, match, cancellationToken).ConfigureAwait(false);

        var lines = kept.Count > 0 ? await FetchAsync(connection, kept, cancellationToken).ConfigureAwait(false) : [];

        if (kept.Count >= criteria.Limit)
        {
            return new LogPage
                   {
                       Lines = lines,
                       Next = kept[^1]
                   };
        }

        var reached = examined >= criteria.ScanLimit;

        return new LogPage
               {
                   Lines = lines,
                   Next = reached ? last : null,
                   ScanLimitReached = reached
               };
    }

    /// <summary>
    /// Walks the examined lines until the page is full or the bound ends the walk.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="match">The FTS5 expression, or <c>null</c></param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the positions of the lines to return, the position of the last examined line and the number of examined lines</returns>
    private static async Task<(List<LogPosition> Kept, LogPosition? Last, int Examined)> WalkAsync(SqliteConnection connection, LogCriteria criteria, string? match, CancellationToken cancellationToken)
    {
        var (sql, parameters) = LogFinderQueries.BuildScan(criteria, match);

        var kept = new List<LogPosition>();
        LogPosition? last = null;
        var examined = 0;

        using var command = CreateCommand(connection, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (kept.Count < criteria.Limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            examined++;
            last = new LogPosition
                   {
                       CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                       Id = reader.GetInt64(0)
                   };

            if (reader.GetInt64(2) == 1 && (criteria.Template is null || IsLineOf(criteria.Template, reader.GetString(3))))
            {
                kept.Add(last);
            }
        }

        return (kept, last, examined);
    }

    /// <summary>
    /// Reads the lines at the given positions.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="positions">The positions in order</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the lines in order</returns>
    private static async Task<IReadOnlyList<StoredRecord>> FetchAsync(SqliteConnection connection, List<LogPosition> positions, CancellationToken cancellationToken)
    {
        var (sql, parameters) = LogFinderQueries.BuildFetch(positions.Select(position => position.Id).ToList());

        return await ReaderQuery.ReadAsync(connection, sql, parameters, RecordQueries.Read, "store: reading logs", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells whether a message belongs to a template.
    /// </summary>
    /// <param name="template">The template</param>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> when the message normalizes to the template text</returns>
    private static bool IsLineOf(LogTemplate template, string message)
    {
        return string.Equals(LogTemplateNormalizer.Normalize(message), template.Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the paging position of a line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The position</returns>
    private static LogPosition PositionOf(StoredRecord line)
    {
        return new LogPosition
               {
                   CapturedAt = line.Record.CapturedAt,
                   Id = line.Id
               };
    }

    /// <summary>
    /// Creates a command with bound parameters.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters</param>
    /// <returns>The command</returns>
    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, Dictionary<string, object> parameters)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    #endregion // Methods
}