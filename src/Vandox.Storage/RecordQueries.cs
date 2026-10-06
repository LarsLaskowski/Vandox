using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Builds the SQL of the record queries and maps the rows to <see cref="StoredRecord"/>.
/// </summary>
internal static class RecordQueries
{
    #region Constants

    private const string RecordColumns = "SELECT r.id, r.kind, r.origin, r.source, r.agent_id, r.seq, r.captured_at, r.received_at, r.boot_id, r.clock_offset_ns, r.data, ";
    private const string MetricColumns = "m.name, m.value, m.unit, m.labels, ";
    private const string LogColumns = "l.log, l.program, l.pid, l.priority, l.message, l.truncated";
    private const string NoMetric = "NULL, NULL, NULL, NULL, ";
    private const string NoLog = "NULL, NULL, NULL, NULL, NULL, NULL";

    #endregion // Constants

    #region Fields

    private static readonly string[] _kinds = [
                                                  RecordKind.Metric,
                                                  RecordKind.ProcessSnapshot,
                                                  RecordKind.ConnectionSnapshot,
                                                  RecordKind.ServiceState,
                                                  RecordKind.MariaDbStatus,
                                                  RecordKind.KernelEvent,
                                                  RecordKind.LogLine,
                                                  RecordKind.Gap
                                              ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Checks a record query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(RecordQuery query)
    {
        ValidateWindow(query.From, query.To, query.Limit);
        Require(_kinds.Contains(query.Kind), "unknown kind");
        Require(query.Name.Length == 0 || query.Kind == RecordKind.Metric, "name needs the kind metric");
        Require(query.Name.Length == 0 || query.Source.Length > 0, "name needs a source");
    }

    /// <summary>
    /// Checks the time range and the limit shared by both queries.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <exception cref="InvalidQueryException">The range or the limit breaks a rule</exception>
    internal static void ValidateWindow(DateTimeOffset from, DateTimeOffset to, int limit)
    {
        Require(from != default && to != default, "from and to are required");
        Require(StorageTime.InStorableRange(from) && StorageTime.InStorableRange(to), "from and to must be within the storable range");
        Require(from < to, "from must be before to");
        Require(limit is >= 1 and <= StorageLimits.MaxQueryLimit, $"limit must be between 1 and {StorageLimits.MaxQueryLimit}");
    }

    /// <summary>
    /// Builds the SQL and the parameters of a validated record query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(RecordQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$from"] = StorageTime.ToNanoseconds(query.From),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };

        if (query.Name.Length > 0)
        {
            parameters["$source"] = query.Source;
            parameters["$name"] = query.Name;

            var metricSql = $"{SelectColumns(true, false)} FROM metrics m JOIN records r ON r.id = m.record_id WHERE m.source = $source AND m.name = $name AND m.captured_at >= $from AND m.captured_at < $to ORDER BY m.captured_at, m.record_id LIMIT $limit";

            return (metricSql, parameters);
        }

        var sql = SelectColumns(query.Kind == RecordKind.Metric, query.Kind == RecordKind.LogLine) + " FROM records r";

        if (query.Kind == RecordKind.Metric)
        {
            sql += " LEFT JOIN metrics m ON m.record_id = r.id";
        }
        else if (query.Kind == RecordKind.LogLine)
        {
            sql += " LEFT JOIN log_lines l ON l.record_id = r.id";
        }

        sql += " WHERE r.kind = $kind";
        parameters["$kind"] = query.Kind;

        if (query.Source.Length > 0)
        {
            sql += " AND r.source = $source";
            parameters["$source"] = query.Source;
        }

        return (sql + " AND r.captured_at >= $from AND r.captured_at < $to ORDER BY r.captured_at, r.id LIMIT $limit", parameters);
    }

    /// <summary>
    /// Builds the SQL and the parameters of a validated log search.
    /// </summary>
    /// <param name="search">The search</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(LogSearch search)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$match"] = FtsQuery.Build(search.Text),
                             ["$from"] = StorageTime.ToNanoseconds(search.From),
                             ["$to"] = StorageTime.ToNanoseconds(search.To),
                             ["$limit"] = (long)search.Limit
                         };
        var sql = $"{SelectColumns(false, true)} FROM log_fts JOIN log_lines l ON l.record_id = log_fts.rowid JOIN records r ON r.id = l.record_id WHERE log_fts MATCH $match AND r.captured_at >= $from AND r.captured_at < $to";

        if (search.Source.Length > 0)
        {
            sql += " AND r.source = $source";
            parameters["$source"] = search.Source;
        }

        return (sql + " ORDER BY r.captured_at, r.id LIMIT $limit", parameters);
    }

    /// <summary>
    /// Reads the current row of a query built with <see cref="SelectColumns"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The record</returns>
    internal static StoredRecord Read(SqliteDataReader reader)
    {
        var kind = reader.GetString(1);
        var record = new DataRecord
                     {
                         Origin = reader.GetString(2),
                         Source = reader.GetString(3),
                         Seq = reader.IsDBNull(5) ? 0UL : (ulong)reader.GetInt64(5),
                         CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(6)),
                         Data = ReadPayload(reader, kind)
                     };

        return new StoredRecord
               {
                   Id = reader.GetInt64(0),
                   Record = record,
                   AgentId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                   ReceivedAt = StorageTime.FromNanoseconds(reader.GetInt64(7)),
                   BootId = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                   ClockOffsetNs = reader.IsDBNull(9) ? null : reader.GetInt64(9)
               };
    }

    /// <summary>
    /// Throws an error for a rejected query unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">The rule that is broken</param>
    private static void Require(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidQueryException($"store: invalid query: {rule}");
    }

    /// <summary>
    /// Returns the column list <see cref="Read"/> reads; the columns of the metric or log line table are NULL when the
    /// query does not join it.
    /// </summary>
    /// <param name="metric">Whether the metric table is joined</param>
    /// <param name="logLine">Whether the log line table is joined</param>
    /// <returns>The column list</returns>
    private static string SelectColumns(bool metric, bool logLine)
    {
        if (logLine)
        {
            return RecordColumns + NoMetric + LogColumns;
        }

        return metric ? RecordColumns + MetricColumns + NoLog : RecordColumns + NoMetric + NoLog;
    }

    /// <summary>
    /// Builds the payload of a kind from the typed columns or the JSON column.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="kind">The kind</param>
    /// <returns>The payload</returns>
    private static IPayload ReadPayload(SqliteDataReader reader, string kind)
    {
        try
        {
            if (kind == RecordKind.Metric)
            {
                return new MetricPoint
                       {
                           Name = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                           Value = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                           Unit = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                           Labels = reader.IsDBNull(14) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(14))
                       };
            }

            if (kind == RecordKind.LogLine)
            {
                return new LogLine
                       {
                           Log = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                           Program = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                           Pid = reader.IsDBNull(17) ? 0 : (int)reader.GetInt64(17),
                           Priority = reader.IsDBNull(18) ? null : (byte)reader.GetInt64(18),
                           Message = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
                           Truncated = (reader.IsDBNull(20) ? 0 : reader.GetInt64(20)) != 0
                       };
            }

            if (PayloadRegistry.TypeOf(kind) is null)
            {
                throw new StoreException($"store: reading records: unknown record kind {FieldError.QuoteName(kind)}");
            }

            using var document = JsonDocument.Parse(reader.IsDBNull(10) ? "null" : reader.GetString(10));

            return PayloadRegistry.Deserialize(kind, document.RootElement)
                       ?? throw new StoreException($"store: reading records: decoding {kind.ToString(CultureInfo.InvariantCulture)} payload: no data");
        }
        catch (JsonException exception)
        {
            throw new StoreException($"store: reading records: decoding {kind} payload", exception);
        }
    }

    #endregion // Methods
}