using System.Globalization;
using System.Text;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The SQL of the log finder.
/// </summary>
internal static class LogFinderQueries
{
    #region Constants

    private const string Exists = "EXISTS (SELECT 1 FROM log_fts WHERE log_fts MATCH $match AND rowid = r.id)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the statement that counts the index matches of a text up to a bound.
    /// </summary>
    /// <param name="match">The FTS5 expression</param>
    /// <param name="selectiveMatches">The bound</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildProbe(string match, int selectiveMatches)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$match"] = match,
                             ["$selective"] = (long)selectiveMatches
                         };

        return ("SELECT count(*) FROM (SELECT rowid FROM log_fts WHERE log_fts MATCH $match LIMIT $selective)", parameters);
    }

    /// <summary>
    /// Builds the statement of the index mode: the lines the index finds, filtered and in order.
    /// </summary>
    /// <param name="criteria">The criteria</param>
    /// <param name="match">The FTS5 expression</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildIndex(LogCriteria criteria, string match)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$match"] = match
                         };
        var sql = new StringBuilder(RecordQueries.SelectColumns(false, true));

        sql.Append(" FROM log_fts JOIN log_lines l ON l.record_id = log_fts.rowid JOIN records r ON r.id = l.record_id WHERE log_fts MATCH $match");
        AppendRange(sql, parameters, criteria);

        if (criteria.Program is not null)
        {
            sql.Append(" AND l.program = $program");
            parameters["$program"] = criteria.Program;
        }

        if (criteria.MaxPriority is not null)
        {
            sql.Append(" AND l.priority <= $maxPriority");
            parameters["$maxPriority"] = (long)criteria.MaxPriority.Value;
        }

        sql.Append(" ORDER BY r.captured_at, r.id");

        if (criteria.Template is null)
        {
            sql.Append(" LIMIT $limit");
            parameters["$limit"] = (long)criteria.Limit;
        }

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds the statement of the scan mode: for each of the first lines of the range in order, its ID, capture time and whether it passes every filter
    /// (and, with a template, its message when it does).
    /// </summary>
    /// <param name="criteria">The criteria</param>
    /// <param name="match">The FTS5 expression, or <c>null</c> without a text</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildScan(LogCriteria criteria, string? match)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$kind"] = RecordKind.LogLine,
                             ["$scan"] = (long)criteria.ScanLimit
                         };
        var sql = new StringBuilder($"SELECT r.id AS id, r.captured_at AS ts, {Keep(criteria, match, parameters)} AS keep FROM records r JOIN log_lines l ON l.record_id = r.id WHERE r.kind = $kind");

        AppendRange(sql, parameters, criteria);
        sql.Append(" ORDER BY r.captured_at, r.id LIMIT $scan");

        if (criteria.Template is null)
        {
            return (sql.ToString(), parameters);
        }

        // The outer LIMIT keeps SQLite from flattening the subquery, which would evaluate the filter expression a second time for the message.
        return ($"SELECT x.id, x.ts, x.keep, CASE WHEN x.keep = 1 THEN m.message END FROM ({sql}) x LEFT JOIN log_lines m ON m.record_id = x.id LIMIT $scan", parameters);
    }

    /// <summary>
    /// Builds the statement that reads the lines with the given record IDs in order.
    /// </summary>
    /// <param name="ids">The record IDs</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildFetch(IReadOnlyList<long> ids)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$ids"] = "[" + string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]"
                         };

        return ($"{RecordQueries.SelectColumns(false, true)} FROM records r JOIN log_lines l ON l.record_id = r.id WHERE r.id IN (SELECT value FROM json_each($ids)) ORDER BY r.captured_at, r.id", parameters);
    }

    /// <summary>
    /// Appends the range, the paging position and the source to a statement that has a WHERE clause.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="criteria">The criteria</param>
    private static void AppendRange(StringBuilder sql, Dictionary<string, object> parameters, LogCriteria criteria)
    {
        sql.Append(" AND r.captured_at >= $start AND r.captured_at < $to");
        parameters["$start"] = criteria.StartNs;
        parameters["$to"] = criteria.ToNs;

        if (criteria.AfterNs is not null)
        {
            sql.Append(" AND (r.captured_at > $at OR r.id > $afterId)");
            parameters["$at"] = criteria.AfterNs.Value;
            parameters["$afterId"] = criteria.AfterId;
        }

        if (criteria.Source is not null)
        {
            sql.Append(" AND r.source = $source");
            parameters["$source"] = criteria.Source;
        }
    }

    /// <summary>
    /// Builds the expression of a scan that tells whether a line passes the filters the range does not apply.
    /// </summary>
    /// <param name="criteria">The criteria</param>
    /// <param name="match">The FTS5 expression, or <c>null</c></param>
    /// <param name="parameters">The parameters</param>
    /// <returns>The expression, 1 or 0</returns>
    private static string Keep(LogCriteria criteria, string? match, Dictionary<string, object> parameters)
    {
        var conditions = new List<string>();

        if (criteria.Program is not null)
        {
            conditions.Add("l.program = $program");
            parameters["$program"] = criteria.Program;
        }

        if (criteria.MaxPriority is not null)
        {
            conditions.Add("l.priority <= $maxPriority");
            parameters["$maxPriority"] = (long)criteria.MaxPriority.Value;
        }

        if (match is not null)
        {
            conditions.Add(Exists);
            parameters["$match"] = match;
        }

        return conditions.Count == 0 ? "1" : $"CASE WHEN {string.Join(" AND ", conditions)} THEN 1 ELSE 0 END";
    }

    #endregion // Methods
}