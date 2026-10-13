using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The SQL of the context, sources, template list and source hours of the log exploration.
/// </summary>
internal static class LogExplorerQueries
{
    #region Constants

    private const string Context = "store: reading logs";

    private const string Neighbours = " FROM (SELECT r.id AS id FROM records r WHERE r.kind = $kind AND r.source = $source AND {0} ORDER BY r.captured_at {1}, r.id {1} LIMIT $examine) c JOIN records r ON r.id = c.id JOIN log_lines l ON l.record_id = r.id WHERE l.log = $log AND l.host = $host ORDER BY r.captured_at {1}, r.id {1} LIMIT $count";

    private const string BeforeCondition = "r.captured_at <= $at AND (r.captured_at < $at OR r.id < $id)";

    private const string AfterCondition = "r.captured_at >= $at AND (r.captured_at > $at OR r.id > $id)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the context of a log line.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the context</returns>
    /// <exception cref="InvalidQueryException">The record is not a stored log line</exception>
    internal static async Task<LogContext> ContextAsync(SqliteConnection connection, LogContextQuery query, CancellationToken cancellationToken)
    {
        var lineSql = $"{RecordQueries.SelectColumns(false, true)} FROM records r JOIN log_lines l ON l.record_id = r.id WHERE r.id = $id";
        var lineParameters = new Dictionary<string, object>
                             {
                                 ["$id"] = query.RecordId
                             };
        var found = await ReaderQuery.ReadAsync(connection, lineSql, lineParameters, RecordQueries.Read, Context, cancellationToken).ConfigureAwait(false);

        if (found.Count == 0)
        {
            throw new InvalidQueryException("store: invalid query: unknown log line");
        }

        var line = found[0];
        var before = query.Before > 0 ? await NeighboursAsync(connection, line, query.Before, true, cancellationToken).ConfigureAwait(false) : [];
        var after = query.After > 0 ? await NeighboursAsync(connection, line, query.After, false, cancellationToken).ConfigureAwait(false) : [];

        return new LogContext
               {
                   Line = line,
                   Before = before.Reverse().ToList(),
                   After = after
               };
    }

    /// <summary>
    /// Reads a template by its ID.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="id">The template ID</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the template</returns>
    /// <exception cref="InvalidQueryException">There is no such template</exception>
    internal static async Task<LogTemplate> TemplateAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        var (sql, parameters) = TemplateQueries.BuildById(id);

        var rows = await ReaderQuery.ReadAsync(connection, sql, parameters, TemplateQueries.ReadTemplate, "store: reading templates", cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : throw new InvalidQueryException("store: invalid query: unknown template");
    }

    /// <summary>
    /// Builds the SQL of the source list.
    /// </summary>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildSources()
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$kind"] = RecordKind.LogLine,
                             ["$max"] = (long)StorageLimits.MaxLogSources
                         };

        return ("""
                WITH RECURSIVE sources(source) AS (
                  SELECT min(source) FROM records WHERE kind = $kind
                  UNION ALL
                  SELECT (SELECT min(source) FROM records WHERE kind = $kind AND source > sources.source) FROM sources WHERE sources.source IS NOT NULL
                  LIMIT $max)
                SELECT source FROM sources WHERE source IS NOT NULL
                """, parameters);
    }

    /// <summary>
    /// Builds the SQL of a template list.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateListQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$fromHour"] = StorageTime.HourStart(StorageTime.ToNanoseconds(query.From)),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };
        var sql = "SELECT t.id, t.source, t.program, t.template, t.first_seen, t.last_seen, t.count, sum(h.count) AS in_range FROM log_template_hours h JOIN log_templates t ON t.id = h.template_id WHERE h.hour >= $fromHour AND h.hour < $to";

        if (query.Source.Length > 0)
        {
            sql += " AND t.source = $source";
            parameters["$source"] = query.Source;
        }

        if (query.Program.Length > 0)
        {
            sql += " AND t.program = $program";
            parameters["$program"] = query.Program;
        }

        return (sql + " GROUP BY t.id ORDER BY in_range DESC, t.id LIMIT $limit", parameters);
    }

    /// <summary>
    /// Builds the SQL of the source hours.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(SourceHourQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$source"] = query.Source,
                             ["$fromHour"] = StorageTime.HourStart(StorageTime.ToNanoseconds(query.From)),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };

        return ("SELECT hour FROM log_source_hours WHERE source = $source AND hour >= $fromHour AND hour < $to ORDER BY hour LIMIT $limit", parameters);
    }

    /// <summary>
    /// Reads one row of the template list.
    /// </summary>
    /// <param name="reader">The reader on a row</param>
    /// <returns>The template with its count</returns>
    internal static TemplateCount ReadTemplateCount(SqliteDataReader reader)
    {
        return new TemplateCount
               {
                   Template = TemplateQueries.ReadTemplate(reader),
                   CountInRange = reader.GetInt64(7)
               };
    }

    /// <summary>
    /// Reads the lines on one side of a line that belong to the same log and host, examining a bounded number of lines of the source.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="line">The line</param>
    /// <param name="count">The number of lines wanted</param>
    /// <param name="before">Whether to read the lines before the line instead of after it</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the lines, nearest first</returns>
    private static Task<IReadOnlyList<StoredRecord>> NeighboursAsync(SqliteConnection connection, StoredRecord line, int count, bool before, CancellationToken cancellationToken)
    {
        var data = (LogLine)line.Record.Data!;
        var sql = RecordQueries.SelectColumns(false, true) + Neighbours.Replace("{0}", before ? BeforeCondition : AfterCondition, StringComparison.Ordinal).Replace("{1}", before ? "DESC" : "ASC", StringComparison.Ordinal);
        var parameters = new Dictionary<string, object>
                         {
                             ["$kind"] = RecordKind.LogLine,
                             ["$source"] = line.Record.Source,
                             ["$at"] = StorageTime.ToNanoseconds(line.Record.CapturedAt),
                             ["$id"] = line.Id,
                             ["$examine"] = (long)StorageLimits.ContextScanRows,
                             ["$log"] = data.Log,
                             ["$host"] = data.Host,
                             ["$count"] = (long)count
                         };

        return ReaderQuery.ReadAsync(connection, sql, parameters, RecordQueries.Read, Context, cancellationToken);
    }

    #endregion // Methods
}