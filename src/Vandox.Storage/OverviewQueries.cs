using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The statements and the row logic of the overview reads: the freshness of the agent's data, the newest values of series, the newest
/// MariaDB status and the newest state of each service unit. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class OverviewQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of an overview read.
    /// </summary>
    internal const string Context = "store: reading overview";

    /// <summary>
    /// The largest number of metric records the live scan reads.
    /// </summary>
    internal const int MaxLiveScan = 20000;

    /// <summary>
    /// The largest number of series of one value read.
    /// </summary>
    internal const int MaxSeries = 16;

    /// <summary>
    /// The largest number of label sets kept per series.
    /// </summary>
    internal const int MaxKeptLabelSets = 64;

    /// <summary>
    /// The largest number of service states returned.
    /// </summary>
    internal const int MaxServiceStates = 1024;

    /// <summary>
    /// The number of rows of one series a value read examines.
    /// </summary>
    internal const int ScanRows = 10000;

    /// <summary>
    /// The most digits of a stored <c>max_connections</c> that are parsed.
    /// </summary>
    internal const int MaxConnectionDigits = 20;

    /// <summary>
    /// Reads the agent whose highest-numbered record was received last; an agent whose record was removed comes after the others, the one
    /// with the latest stored capture time first.
    /// </summary>
    internal const string LastAgent = "SELECT s.agent_id, (SELECT r.received_at FROM records AS r WHERE r.origin = 'agent' AND r.agent_id = s.agent_id AND r.seq = s.max_seq) AS received_at FROM agent_streams AS s "
                                      + "ORDER BY received_at DESC NULLS LAST, s.max_captured_at DESC, s.agent_id LIMIT 1";

    /// <summary>
    /// Reads the number and the span of the open sequence gaps of an agent.
    /// </summary>
    internal const string OpenGaps = "SELECT count(*), min(from_at), max(to_at) FROM gaps WHERE agent_id = $agent AND kind = 'sequence' AND state = 'open'";

    /// <summary>
    /// Reads the newest metric records not after the bound, newest first.
    /// </summary>
    internal const string LiveScan = "SELECT captured_at, live FROM records WHERE kind = 'metric' AND captured_at <= $until ORDER BY captured_at DESC, id DESC LIMIT $limit";

    /// <summary>
    /// Reads the newest capture time of a series not after the bound.
    /// </summary>
    internal const string NewestOfSeries = "SELECT max(captured_at) FROM metrics WHERE source = $source AND name = $name AND captured_at <= $until";

    /// <summary>
    /// Reads the rows of a series in a window, newest first.
    /// </summary>
    internal const string SeriesRows = "SELECT captured_at, labels, value FROM metrics WHERE source = $source AND name = $name AND captured_at >= $from AND captured_at <= $newest ORDER BY captured_at DESC, record_id DESC LIMIT $limit";

    /// <summary>
    /// Reads the newest MariaDB status record not after the bound with the members the overview shows; the payload is never decoded whole.
    /// </summary>
    internal const string MariaDbStatusRecord = "SELECT captured_at, json_extract(data, '$.availability'), json_extract(data, '$.status.Threads_connected'), json_extract(data, '$.status.Memory_used'), "
                                                + "CAST(json_extract(data, '$.variables.max_connections') AS TEXT) FROM records WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at <= $until ORDER BY captured_at DESC, id DESC LIMIT 1";

    /// <summary>
    /// Reads the newest capture time of a service state not after the bound.
    /// </summary>
    internal const string NewestServiceState = "SELECT max(captured_at) FROM records WHERE kind = 'service_state' AND captured_at <= $until";

    /// <summary>
    /// Reads the latest row per unit in a window over the covering index, the not active ones first, then by unit, at most the limit, with the number of
    /// units; the sub state is read for the returned rows only.
    /// </summary>
    internal const string ServiceStates = "SELECT t.unit, t.active_state, (SELECT json_extract(r.data, '$.sub_state') FROM records AS r WHERE r.id = t.id), t.enter, t.restarts, t.captured_at, t.total FROM ("
                                          + "SELECT json_extract(data, '$.unit') AS unit, json_extract(data, '$.active_state') AS active_state, json_extract(data, '$.active_enter_at') AS enter, COALESCE(json_extract(data, '$.restarts'), 0) AS restarts, "
                                          + "id, max(captured_at) AS captured_at, count(*) OVER () AS total FROM records INDEXED BY records_service_state WHERE kind = 'service_state' AND captured_at >= $from AND captured_at <= $newest GROUP BY json_extract(data, '$.unit') "
                                          + "ORDER BY json_extract(data, '$.active_state') <> 'active' DESC, json_extract(data, '$.unit') LIMIT $limit) AS t "
                                          + "ORDER BY t.active_state <> 'active' DESC, t.unit";

    private const string UntilParameter = "$until";

    private const string SourceParameter = "$source";

    private const string NameParameter = "$name";

    private const string LimitParameter = "$limit";

    private const string NewestParameter = "$newest";

    private const string FromParameter = "$from";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a freshness read.
    /// </summary>
    /// <param name="liveScanLimit">The number of records to scan</param>
    /// <exception cref="InvalidQueryException">The read breaks a rule</exception>
    internal static void ValidateFreshness(int liveScanLimit)
    {
        RecordQueries.Require(liveScanLimit >= 1 && liveScanLimit <= MaxLiveScan, $"live scan limit must be 1 to {MaxLiveScan}");
    }

    /// <summary>
    /// Checks a latest-value read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateValues(LatestValueQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.Series.Count >= 1, "at least one series is required");
        RecordQueries.Require(query.Series.Count <= MaxSeries, $"at most {MaxSeries} series");
        RecordQueries.Require(query.Window > TimeSpan.Zero && query.Window <= TimeSpan.FromDays(1), "window must be positive and at most one day");
        RecordQueries.Require(query.MaxLabelSets >= 1 && query.MaxLabelSets <= MaxKeptLabelSets, $"label sets must be 1 to {MaxKeptLabelSets}");
    }

    /// <summary>
    /// Checks a service read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateServices(LatestServiceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.Retirement > TimeSpan.Zero && query.Retirement <= TimeSpan.FromDays(1), "retirement must be positive and at most one day");
        RecordQueries.Require(query.Limit >= 1 && query.Limit <= MaxServiceStates, $"limit must be 1 to {MaxServiceStates}");
    }

    /// <summary>
    /// Reads the freshness of the agent's data.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="liveScanLimit">The number of newest metric records to scan</param>
    /// <param name="until">The latest capture time that counts</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the freshness</returns>
    internal static async Task<AgentFreshness> ReadFreshnessAsync(SqliteConnection connection, int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var agents = await ReaderQuery.ReadAsync(connection,
                                                 LastAgent,
                                                 new Dictionary<string, object>(),
                                                 reader => (AgentId: reader.GetString(0), ReceivedAt: ReadTime(reader, 1)),
                                                 Context,
                                                 cancellationToken).ConfigureAwait(false);
        var (lastLive, scannedBack) = await ReadLiveScanAsync(connection, liveScanLimit, until, cancellationToken).ConfigureAwait(false);

        if (agents.Count == 0)
        {
            return new AgentFreshness
                   {
                       LastLiveAt = lastLive,
                       ScannedBackTo = scannedBack
                   };
        }

        var gaps = await ReadOpenGapsAsync(connection, agents[0].AgentId, cancellationToken).ConfigureAwait(false);

        return new AgentFreshness
               {
                   AgentId = agents[0].AgentId,
                   ReceivedAt = agents[0].ReceivedAt,
                   LastLiveAt = lastLive,
                   ScannedBackTo = scannedBack,
                   OpenSequenceGaps = gaps.Count,
                   MissingStart = gaps.Start,
                   MissingEnd = gaps.End
               };
    }

    /// <summary>
    /// Reads the newest values of the series of a query.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the newest values per series, in the order of the query</returns>
    internal static async Task<IReadOnlyList<LatestSeries>> ReadValuesAsync(SqliteConnection connection, LatestValueQuery query, CancellationToken cancellationToken)
    {
        var result = new List<LatestSeries>();

        foreach (var series in query.Series)
        {
            result.Add(await ReadSeriesAsync(connection, series, query, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Reads the newest MariaDB status record.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="until">The latest capture time that counts</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the status, or <c>null</c> when there is none</returns>
    internal static async Task<MariaDbLatest?> ReadMariaDbAsync(SqliteConnection connection, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection,
                                               MariaDbStatusRecord,
                                               new Dictionary<string, object>
                                               {
                                                   [UntilParameter] = StorageTime.ToNanoseconds(until)
                                               },
                                               MapMariaDb,
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Reads the newest state of each current service unit.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the page of states</returns>
    internal static async Task<ServiceStatePage> ReadServicesAsync(SqliteConnection connection, LatestServiceQuery query, CancellationToken cancellationToken)
    {
        var newest = await ReadNewestAsync(connection,
                                           NewestServiceState,
                                           new Dictionary<string, object>
                                           {
                                               [UntilParameter] = StorageTime.ToNanoseconds(query.Until)
                                           },
                                           cancellationToken).ConfigureAwait(false);

        if (newest is null)
        {
            return new ServiceStatePage();
        }

        var rows = await ReaderQuery.ReadAsync(connection,
                                               ServiceStates,
                                               new Dictionary<string, object>
                                               {
                                                   [FromParameter] = newest.Value - (query.Retirement.Ticks * 100),
                                                   [NewestParameter] = newest.Value,
                                                   [LimitParameter] = query.Limit
                                               },
                                               MapServiceState,
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        return new ServiceStatePage
               {
                   States = rows.Select(row => row.State).ToList(),
                   Total = rows.Count > 0 ? rows[0].Total : 0,
                   Newest = StorageTime.FromNanoseconds(newest.Value)
               };
    }

    /// <summary>
    /// Parses the stored text of a connection limit: at most 20 ASCII digits that fit a <see cref="long"/>.
    /// </summary>
    /// <param name="text">The stored text, or <c>null</c></param>
    /// <returns>The number, or <c>null</c> when the text is none</returns>
    internal static long? ParseMaxConnections(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxConnectionDigits || text.All(char.IsAsciiDigit) == false)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    /// <summary>
    /// Reads the number and the span of the open sequence gaps of an agent.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the count, the earliest start and the latest end</returns>
    private static async Task<(int Count, DateTimeOffset? Start, DateTimeOffset? End)> ReadOpenGapsAsync(SqliteConnection connection, string agentId, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection,
                                               OpenGaps,
                                               new Dictionary<string, object>
                                               {
                                                   ["$agent"] = agentId
                                               },
                                               reader => (Count: reader.GetInt32(0), Start: ReadTime(reader, 1), End: ReadTime(reader, 2)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        return rows[0];
    }

    /// <summary>
    /// Scans the newest metric records for a live one.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="liveScanLimit">The number of records to scan</param>
    /// <param name="until">The latest capture time that counts</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the capture time of the newest live record, or, when none is live, the capture time of the oldest record read</returns>
    private static async Task<(DateTimeOffset? LastLive, DateTimeOffset? ScannedBack)> ReadLiveScanAsync(SqliteConnection connection, int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statement, values bound as parameters
        command.CommandText = LiveScan;
#pragma warning restore CA2100
        command.Parameters.AddWithValue(UntilParameter, StorageTime.ToNanoseconds(until));
        command.Parameters.AddWithValue(LimitParameter, liveScanLimit);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        DateTimeOffset? oldest = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            oldest = StorageTime.FromNanoseconds(reader.GetInt64(0));

            if (reader.GetInt64(1) == 1)
            {
                return (oldest, null);
            }
        }

        return (null, oldest);
    }

    /// <summary>
    /// Reads the newest values of one series.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="series">The series</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the values</returns>
    private static async Task<LatestSeries> ReadSeriesAsync(SqliteConnection connection, SeriesKey series, LatestValueQuery query, CancellationToken cancellationToken)
    {
        var newest = await ReadNewestAsync(connection,
                                           NewestOfSeries,
                                           new Dictionary<string, object>
                                           {
                                               [SourceParameter] = series.Source,
                                               [NameParameter] = series.Name,
                                               [UntilParameter] = StorageTime.ToNanoseconds(query.Until)
                                           },
                                           cancellationToken).ConfigureAwait(false);

        if (newest is null)
        {
            return new LatestSeries
                   {
                       Series = series
                   };
        }

        var tally = new LabelSetTally(query.MaxLabelSets);

        using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statement, values bound as parameters
        command.CommandText = SeriesRows;
#pragma warning restore CA2100
        command.Parameters.AddWithValue(SourceParameter, series.Source);
        command.Parameters.AddWithValue(NameParameter, series.Name);
        command.Parameters.AddWithValue(FromParameter, newest.Value - (query.Window.Ticks * 100));
        command.Parameters.AddWithValue(NewestParameter, newest.Value);
        command.Parameters.AddWithValue(LimitParameter, ScanRows);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var labels = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            tally.Add(labels == "{}" ? string.Empty : labels, StorageTime.FromNanoseconds(reader.GetInt64(0)), reader.GetDouble(2));
        }

        return new LatestSeries
               {
                   Series = series,
                   Values = tally.Kept.Select(kept => new LatestValue
                                                      {
                                                          Labels = ParseLabels(kept.Labels),
                                                          CapturedAt = kept.CapturedAt,
                                                          Value = kept.Value
                                                      }).ToList(),
                   LabelSets = tally.Count
               };
    }

    /// <summary>
    /// Reads a single newest capture time.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the nanoseconds, or <c>null</c> when there is no record</returns>
    private static async Task<long?> ReadNewestAsync(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection, sql, parameters, reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0), Context, cancellationToken).ConfigureAwait(false);

        return rows[0];
    }

    /// <summary>
    /// Parses the stored text of a label set.
    /// </summary>
    /// <param name="labels">The JSON object, or an empty text for none</param>
    /// <returns>The labels</returns>
    private static Dictionary<string, string> ParseLabels(string labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (labels.Length == 0)
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(labels);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new StoreException($"{Context}: stored labels are not a JSON object of texts", exception);
        }

        return result;
    }

    /// <summary>
    /// Maps a MariaDB status row.
    /// </summary>
    /// <param name="reader">The reader at the row</param>
    /// <returns>The status</returns>
    private static MariaDbLatest MapMariaDb(SqliteDataReader reader)
    {
        return new MariaDbLatest
               {
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(0)),
                   Availability = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                   ThreadsConnected = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                   MemoryUsed = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                   MaxConnections = ParseMaxConnections(reader.IsDBNull(4) ? null : reader.GetString(4))
               };
    }

    /// <summary>
    /// Maps a service state row.
    /// </summary>
    /// <param name="reader">The reader at the row</param>
    /// <returns>The state and the number of units</returns>
    private static (LatestServiceState State, int Total) MapServiceState(SqliteDataReader reader)
    {
        return (new LatestServiceState
                {
                    Unit = reader.GetString(0),
                    ActiveState = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    SubState = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ActiveEnterAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Restarts = reader.GetInt64(4),
                    CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(5))
                },
                reader.GetInt32(6));
    }

    /// <summary>
    /// Reads a nullable time column.
    /// </summary>
    /// <param name="reader">The reader at the row</param>
    /// <param name="ordinal">The column</param>
    /// <returns>The time, or <c>null</c></returns>
    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : StorageTime.FromNanoseconds(reader.GetInt64(ordinal));
    }

    #endregion // Methods
}