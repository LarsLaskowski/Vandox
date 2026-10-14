using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Validates the template queries and builds their SQL statements with bound parameters.
/// </summary>
internal static class TemplateQueries
{
    #region Constants

    /// <summary>
    /// The columns of a template, read by <see cref="ReadTemplate"/> from the given offset.
    /// </summary>
    private const string TemplateColumns = "t.id, t.source, t.program, t.template, t.first_seen, t.last_seen, t.count";

    private const long NanosecondsPerHour = 3_600_000_000_000;

    // `rows` reads the template hours of the range and of the baseline before it in one pass per template and sums the counts of the
    // `baseline` hours before each row with a window function (a template's hours are distinct multiples of an hour, so `1 PRECEDING`
    // ends the window just before the row's own hour). The candidates are the rows of the range that reach the minimum count. The
    // window start and the covered hours are computed once per distinct (source, hour) of the candidates: SQLite would flatten a plain
    // CTE into the join and run the covered-hours subquery per candidate, so `covered` and `scored` are MATERIALIZED (record 0095).
    // Every value is bound.
    private const string SpikeSql = """
                                    WITH rows AS (
                                      SELECT h.template_id, h.hour, h.count, t.source,
                                             sum(h.count) OVER (PARTITION BY h.template_id ORDER BY h.hour RANGE BETWEEN $baselineNs PRECEDING AND 1 PRECEDING) AS baseline_sum
                                      FROM log_template_hours h JOIN log_templates t ON t.id = h.template_id
                                      WHERE h.hour >= $fromHour - $baselineNs AND h.hour < $to AND ($source = '' OR t.source = $source)),
                                    candidates AS (
                                      SELECT template_id, hour, count, source, coalesce(baseline_sum, 0) AS baseline_sum
                                      FROM rows
                                      WHERE hour >= $fromHour AND count >= $minCount),
                                    windows AS (
                                      SELECT source, hour,
                                             CASE WHEN hour < $minTime + $baselineNs THEN $minTime ELSE hour - $baselineNs END AS window_start
                                      FROM (SELECT DISTINCT source, hour FROM candidates)),
                                    covered AS MATERIALIZED (
                                      SELECT w.source, w.hour, w.window_start,
                                             (SELECT count(*) FROM log_source_hours s
                                              WHERE s.source = w.source AND s.hour >= w.window_start AND s.hour < w.hour) AS covered_hours
                                      FROM windows w),
                                    scored AS MATERIALIZED (
                                      SELECT c.template_id, c.hour, c.count, v.covered_hours, c.baseline_sum
                                      FROM candidates c JOIN covered v ON v.source = c.source AND v.hour = c.hour
                                      WHERE v.covered_hours >= $minBaselineHours)
                                    SELECT t.id, t.source, t.program, t.template, t.first_seen, t.last_seen, t.count, s.hour, s.count, s.covered_hours, s.baseline_sum
                                    FROM scored s JOIN log_templates t ON t.id = s.template_id
                                    WHERE s.count * s.covered_hours > $factor * s.baseline_sum
                                    ORDER BY s.hour, s.count DESC, s.template_id
                                    LIMIT $limit
                                    """;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a query for the hourly counts of a template.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateHourQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.ValidateWindow(query.From, query.To, query.Limit);
        RecordQueries.Require(query.TemplateId > 0, "template ID must be positive");
    }

    /// <summary>
    /// Checks a query for new templates.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(NewTemplateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Source);
        RecordQueries.ValidateWindow(query.From, query.To, query.Limit);
    }

    /// <summary>
    /// Checks a query for templates above the usual level.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateSpikeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Source);
        RecordQueries.ValidateWindow(query.From, query.To, query.Limit);
        RecordQueries.Require(query.To - query.From <= TimeSpan.FromHours(StorageLimits.MaxSpikeRangeHours), $"the range must not exceed {StorageLimits.MaxSpikeRangeHours} hours");
        RecordQueries.Require(query.BaselineHours is >= 1 and <= StorageLimits.MaxBaselineHours, $"baseline hours must be between 1 and {StorageLimits.MaxBaselineHours}");
        RecordQueries.Require(query.MinBaselineHours >= 1 && query.MinBaselineHours <= query.BaselineHours, "minimum baseline hours must be between 1 and the baseline hours");
        RecordQueries.Require(query.Factor >= 1 && query.Factor <= StorageLimits.MaxSpikeFactor, $"factor must be between 1 and {StorageLimits.MaxSpikeFactor}");
        RecordQueries.Require(query.MinCount >= 1, "minimum count must be at least 1");
    }

    /// <summary>
    /// Builds the statement for the hourly counts of a template.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateHourQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$id"] = query.TemplateId,
                             ["$fromHour"] = StorageTime.HourStart(StorageTime.ToNanoseconds(query.From)),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };

        return ("SELECT hour, count FROM log_template_hours WHERE template_id = $id AND hour >= $fromHour AND hour < $to ORDER BY hour LIMIT $limit", parameters);
    }

    /// <summary>
    /// Builds the statement for new templates.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(NewTemplateQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$from"] = StorageTime.ToNanoseconds(query.From),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };
        var sql = new StringBuilder($"SELECT {TemplateColumns} FROM log_templates t WHERE t.first_seen >= $from AND t.first_seen < $to");

        if (query.Source.Length > 0)
        {
            sql.Append(" AND t.source = $source");
            parameters["$source"] = query.Source;
        }

        sql.Append(" AND EXISTS (SELECT 1 FROM log_templates p WHERE p.source = t.source AND p.first_seen < $from) ORDER BY t.first_seen, t.id LIMIT $limit");

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds the statement for templates above the usual level.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateSpikeQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$fromHour"] = StorageTime.HourStart(StorageTime.ToNanoseconds(query.From)),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$minCount"] = query.MinCount,
                             ["$source"] = query.Source,
                             ["$minTime"] = long.MinValue,
                             ["$baselineNs"] = query.BaselineHours * NanosecondsPerHour,
                             ["$minBaselineHours"] = (long)query.MinBaselineHours,
                             ["$factor"] = query.Factor,
                             ["$limit"] = (long)query.Limit
                         };

        return (SpikeSql, parameters);
    }

    /// <summary>
    /// Checks the message of a template lookup.
    /// </summary>
    /// <param name="message">The message</param>
    /// <exception cref="InvalidQueryException">The message is longer than the model allows</exception>
    internal static void ValidateMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RecordQueries.Require(message.Length <= ModelLimits.MaxTextBytes / 3 || Encoding.UTF8.GetByteCount(message) <= ModelLimits.MaxTextBytes, $"message must be at most {ModelLimits.MaxTextBytes} UTF-8 bytes");
    }

    /// <summary>
    /// Builds the statement that reads a template by its key.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="template">The template text</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildFind(string source, string program, string template)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$source"] = source,
                             ["$program"] = program,
                             ["$template"] = template
                         };

        return ($"SELECT {TemplateColumns} FROM log_templates t WHERE t.source = $source AND t.program = $program AND t.template = $template", parameters);
    }

    /// <summary>
    /// Builds the statement that reads a template by its ID.
    /// </summary>
    /// <param name="id">The template ID</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildById(long id)
    {
        return ($"SELECT {TemplateColumns} FROM log_templates t WHERE t.id = $id", new Dictionary<string, object>
                                                                                   {
                                                                                       ["$id"] = id
                                                                                   });
    }

    /// <summary>
    /// Reads the template columns of the current row.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The template</returns>
    internal static LogTemplate ReadTemplate(SqliteDataReader reader)
    {
        return new LogTemplate
               {
                   Id = reader.GetInt64(0),
                   Source = reader.GetString(1),
                   Program = reader.GetString(2),
                   Template = reader.GetString(3),
                   FirstSeen = StorageTime.FromNanoseconds(reader.GetInt64(4)),
                   LastSeen = StorageTime.FromNanoseconds(reader.GetInt64(5)),
                   Count = reader.GetInt64(6)
               };
    }

    /// <summary>
    /// Reads the current row of the statement built for templates above the usual level.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The template, the hour and the usual level</returns>
    internal static TemplateSpike ReadSpike(SqliteDataReader reader)
    {
        var covered = reader.GetInt64(9);

        return new TemplateSpike
               {
                   Template = ReadTemplate(reader),
                   Hour = StorageTime.FromNanoseconds(reader.GetInt64(7)),
                   Count = reader.GetInt64(8),
                   UsualLevel = (double)reader.GetInt64(10) / covered,
                   CoveredHours = (int)covered
               };
    }

    /// <summary>
    /// Reads the current row of the statement built for the hourly counts.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The hour and its count</returns>
    internal static TemplateHour ReadHour(SqliteDataReader reader)
    {
        return new TemplateHour
               {
                   Hour = StorageTime.FromNanoseconds(reader.GetInt64(0)),
                   Count = reader.GetInt64(1)
               };
    }

    #endregion // Methods
}