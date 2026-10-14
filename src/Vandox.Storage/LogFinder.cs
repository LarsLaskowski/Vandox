using Microsoft.Data.Sqlite;

using SQLitePCL;

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

    #region Fields

    /// <summary>
    /// The time the index check of one request may take before the request runs in scan mode.
    /// </summary>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// The time one page of a scan may take before it ends with the lines found so far.
    /// </summary>
    internal static readonly TimeSpan PageBudget = TimeSpan.FromMilliseconds(1500);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Finds one page of log lines; an index check that outlasts the budget turns the request into a scan, and a scan ends with the page budget.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="filter">The validated filters</param>
    /// <param name="template">The template of <see cref="LogFilter.TemplateId"/>, or <c>null</c></param>
    /// <param name="options">The bounds and the clock of the call</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    internal static async Task<LogPage> FindAsync(SqliteConnection connection, LogFilter filter, LogTemplate? template, LogFindOptions options, CancellationToken cancellationToken)
    {
        var start = options.TimeProvider.GetTimestamp();
        var criteria = LogCriteria.Create(filter, template);

        if (criteria is null)
        {
            return new LogPage();
        }

        if (criteria.Text.Length == 0)
        {
            return await ScanAsync(connection, criteria, options, start, cancellationToken).ConfigureAwait(false);
        }

        var ids = await CheckIndexAsync(connection, FtsQuery.Build(criteria.Text), options, cancellationToken).ConfigureAwait(false);

        if (ids is null)
        {
            var stepwise = await ScanAsync(connection, criteria, options, start, cancellationToken).ConfigureAwait(false);

            stepwise.IndexCheckStopped = true;

            return stepwise;
        }

        if (ids.Count == 0)
        {
            return new LogPage();
        }

        return ids.Count < options.SelectiveMatches
                   ? await FindFromIndexAsync(connection, criteria, ids, cancellationToken).ConfigureAwait(false)
                   : await ScanAsync(connection, criteria, options, start, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the number of lines a scan examines for a search text.
    /// </summary>
    /// <param name="scanLimit">The scan bound of the call</param>
    /// <param name="tokens">The number of word parts of the search text</param>
    /// <returns>The scan bound for 0 or 1 parts, otherwise the bound divided by the parts, at least 1</returns>
    internal static int ExaminedLines(int scanLimit, int tokens)
    {
        return tokens <= 1 ? scanLimit : Math.Max(1, scanLimit / tokens);
    }

    /// <summary>
    /// Reads the record IDs of the index matches under the time budget.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="match">The FTS5 expression</param>
    /// <param name="options">The bound of the IDs, the time the check may take and the clock</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the record IDs, or <c>null</c> when the budget is used up</returns>
    private static async Task<List<long>?> CheckIndexAsync(SqliteConnection connection, string match, LogFindOptions options, CancellationToken cancellationToken)
    {
        if (options.ProbeBudget <= TimeSpan.Zero)
        {
            return null;
        }

        var state = ReaderQuery.AttachBudget(connection, options.TimeProvider, options.ProbeBudget, cancellationToken);

        try
        {
            return await ReadMatchesAsync(connection, match, options.SelectiveMatches, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (StoppedByBudget(exception, state, cancellationToken))
        {
            return null;
        }
        finally
        {
            ReaderQuery.EndBudget(connection, cancellationToken);
        }
    }

    /// <summary>
    /// Tells whether an error is the stop of the probe by its budget: an interruption that the budget handler asked for while the call is not cancelled.
    /// </summary>
    /// <param name="exception">The error</param>
    /// <param name="state">The state of the budget handler</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns><c>true</c> when the budget stopped the probe</returns>
    private static bool StoppedByBudget(SqliteException exception, ProbeBudgetState state, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception.SqliteErrorCode == raw.SQLITE_INTERRUPT && state.Exhausted;
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
    /// <param name="options">The page budget and the clock</param>
    /// <param name="start">The timestamp of the start of the call</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    private static async Task<LogPage> ScanAsync(SqliteConnection connection, LogCriteria criteria, LogFindOptions options, long start, CancellationToken cancellationToken)
    {
        using var index = criteria.ScanPhrases is null ? null : LineIndex.Open(cancellationToken);

        var state = await WalkAsync(connection, criteria, index, options, start, cancellationToken).ConfigureAwait(false);
        var kept = state.Kept;
        var lines = kept.Count > 0 ? await FetchAsync(connection, kept, cancellationToken).ConfigureAwait(false) : [];

        if (kept.Count >= criteria.Limit)
        {
            return new LogPage
                   {
                       Lines = lines,
                       Next = kept[^1]
                   };
        }

        var reached = state.Examined >= criteria.ExaminedLines || state.ClockStopped;

        return new LogPage
               {
                   Lines = lines,
                   Next = reached ? state.Last : null,
                   ScanLimitReached = reached
               };
    }

    /// <summary>
    /// Walks the examined lines until the page is full, the bound ends the walk or the page budget is used up; at least one line is examined.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="index">The line index for multi-part terms, or <c>null</c></param>
    /// <param name="options">The page budget and the clock</param>
    /// <param name="start">The timestamp of the start of the call</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns what the walk found</returns>
    private static async Task<ScanState> WalkAsync(SqliteConnection connection, LogCriteria criteria, LineIndex? index, LogFindOptions options, long start, CancellationToken cancellationToken)
    {
        var (sql, parameters) = LogFinderQueries.BuildScan(criteria);

        var state = new ScanState();

        using var command = CreateCommand(connection, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (state.Kept.Count < criteria.Limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            state.Examined++;
            state.Last = new LogPosition
                         {
                             CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                             Id = reader.GetInt64(0)
                         };

            if (reader.GetInt64(2) == 1)
            {
                await AddAsync(state, criteria, index, reader, cancellationToken).ConfigureAwait(false);
            }

            if (PageBudgetUsedUp(options, start))
            {
                state.ClockStopped = true;

                break;
            }
        }

        await FlushAsync(state, criteria, index, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// Tells whether the page budget is used up.
    /// </summary>
    /// <param name="options">The page budget and the clock</param>
    /// <param name="start">The timestamp of the start of the call</param>
    /// <returns><c>true</c> when a page budget is set and the time since the start has reached it</returns>
    private static bool PageBudgetUsedUp(LogFindOptions options, long start)
    {
        return options.PageBudget >= TimeSpan.Zero && options.TimeProvider.GetElapsedTime(start) >= options.PageBudget;
    }

    /// <summary>
    /// Handles a line that passes the filters and the one-part terms: the template decides at once, the multi-part terms wait for the line index.
    /// </summary>
    /// <param name="state">The state of the scan</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="index">The line index, or <c>null</c></param>
    /// <param name="reader">The reader at the line</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the line is handled</returns>
    private static async Task AddAsync(ScanState state, LogCriteria criteria, LineIndex? index, SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var position = state.Last!;
        var message = criteria.Template is null && index is null ? string.Empty : reader.GetString(3);

        if (criteria.Template is null || IsLineOf(criteria.Template, message))
        {
            await KeepAsync(state, criteria, index, (position, message), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps a line of the template at once, or lets it wait for the line index when the user's text has multi-part terms; a full chunk is matched.
    /// </summary>
    /// <param name="state">The state of the scan</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="index">The line index, or <c>null</c></param>
    /// <param name="line">The position and message of the line</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the line is handled</returns>
    private static async Task KeepAsync(ScanState state, LogCriteria criteria, LineIndex? index, (LogPosition Position, string Message) line, CancellationToken cancellationToken)
    {
        if (index is null)
        {
            state.Kept.Add(line.Position);

            return;
        }

        state.Pending.Add(line);
        state.PendingCharacters += line.Message.Length;

        if (state.Pending.Count >= LineIndex.ChunkLines || state.PendingCharacters >= LineIndex.ChunkCharacters)
        {
            await FlushAsync(state, criteria, index, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Matches the waiting lines against the multi-part terms and keeps those that match, up to the page size.
    /// </summary>
    /// <param name="state">The state of the scan</param>
    /// <param name="criteria">The criteria</param>
    /// <param name="index">The line index, or <c>null</c></param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the chunk is matched</returns>
    private static async Task FlushAsync(ScanState state, LogCriteria criteria, LineIndex? index, CancellationToken cancellationToken)
    {
        if (index is null || state.Pending.Count == 0)
        {
            return;
        }

        var matched = await index.MatchAsync(state.Pending.Select(line => (line.Position.Id, line.Message)).ToList(), criteria.ScanPhrases!, cancellationToken).ConfigureAwait(false);

        state.Kept.AddRange(state.Pending.Where(line => matched.Contains(line.Position.Id)).Select(line => line.Position).Take(criteria.Limit - state.Kept.Count));
        state.Pending.Clear();
        state.PendingCharacters = 0;
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