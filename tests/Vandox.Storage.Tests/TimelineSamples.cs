using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds agent records and batches by hand and reads the stored classification and rows directly for the timeline tests.
/// </summary>
internal static class TimelineSamples
{
    #region Constants

    /// <summary>
    /// The agent ID of the hand-built batches.
    /// </summary>
    internal const string AgentId = "agent-1";

    /// <summary>
    /// The boot ID of the hand-built batches.
    /// </summary>
    internal const string BootId = "0123abcd-0123-0123-0123-0123456789ab";

    /// <summary>
    /// The boot ID of the boot after a reboot.
    /// </summary>
    internal const string OtherBootId = "89abcdef-0123-0123-0123-0123456789ab";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates an agent <c>system</c> metric.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(ulong seq, DateTimeOffset capturedAt)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "system",
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new MetricPoint
                          {
                              Name = "cpu_user_percent",
                              Value = 1,
                              Unit = "percent"
                          }
               };
    }

    /// <summary>
    /// Creates an agent <c>self</c> sample of the send lag.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="lagSeconds">The send lag the sample reports</param>
    /// <returns>The record</returns>
    internal static DataRecord SelfSample(ulong seq, DateTimeOffset capturedAt, double lagSeconds)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "agent",
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new MetricPoint
                          {
                              Name = "agent_send_lag_seconds",
                              Value = lagSeconds,
                              Unit = "seconds"
                          }
               };
    }

    /// <summary>
    /// Creates an agent log line.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord LogLine(ulong seq, DateTimeOffset capturedAt)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "syslog",
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new LogLine
                          {
                              Log = "syslog",
                              Host = "web-1",
                              Program = "sshd",
                              Message = "session opened"
                          }
               };
    }

    /// <summary>
    /// Creates an agent <c>gap</c> record that names numbers.
    /// </summary>
    /// <param name="seq">The sequence number of the gap record itself</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="cause">The cause</param>
    /// <param name="firstSeq">The first number the gap names</param>
    /// <param name="lastSeq">The last number the gap names</param>
    /// <returns>The record</returns>
    internal static DataRecord AgentGap(ulong seq, DateTimeOffset capturedAt, string cause, ulong firstSeq, ulong lastSeq)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "agent",
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new Gap
                          {
                              From = capturedAt.AddMinutes(-1),
                              To = capturedAt,
                              Cause = cause,
                              FirstSeq = firstSeq,
                              LastSeq = lastSeq
                          }
               };
    }

    /// <summary>
    /// Creates a batch of the default agent.
    /// </summary>
    /// <param name="receivedAt">The receive time</param>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    internal static RecordBatch Batch(DateTimeOffset receivedAt, params DataRecord[] records)
    {
        return BatchOf(AgentId, BootId, receivedAt, records);
    }

    /// <summary>
    /// Creates a batch.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="bootId">The boot ID</param>
    /// <param name="receivedAt">The receive time</param>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    internal static RecordBatch BatchOf(string agentId, string bootId, DateTimeOffset receivedAt, IReadOnlyList<DataRecord> records)
    {
        return new RecordBatch
               {
                   AgentId = agentId,
                   BootId = bootId,
                   ReceivedAt = receivedAt,
                   Records = records
               };
    }

    /// <summary>
    /// Creates a batch of consecutive metrics, one second apart, the last one captured a second before the batch is received.
    /// </summary>
    /// <param name="firstSeq">The number of the first record</param>
    /// <param name="count">The number of records</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    internal static RecordBatch Run(ulong firstSeq, int count, DateTimeOffset receivedAt)
    {
        var records = new DataRecord[count];

        for (var index = 0; index < count; index++)
        {
            records[index] = Metric(firstSeq + (ulong)index, receivedAt.AddSeconds(index - count));
        }

        return Batch(receivedAt, records);
    }

    /// <summary>
    /// Creates a batch of consecutive metrics captured long before they are received, as a backfill brings them.
    /// </summary>
    /// <param name="firstSeq">The number of the first record</param>
    /// <param name="count">The number of records</param>
    /// <param name="firstCapturedAt">The capture time of the first record</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    internal static RecordBatch OldRun(ulong firstSeq, int count, DateTimeOffset firstCapturedAt, DateTimeOffset receivedAt)
    {
        var records = new DataRecord[count];

        for (var index = 0; index < count; index++)
        {
            records[index] = Metric(firstSeq + (ulong)index, firstCapturedAt.AddSeconds(index));
        }

        return Batch(receivedAt, records);
    }

    /// <summary>
    /// Creates the batch an agent sends again because it lost the acknowledgement.
    /// </summary>
    /// <param name="batch">The batch sent before</param>
    /// <param name="receivedAt">The receive time of the second request</param>
    /// <returns>The batch</returns>
    internal static RecordBatch Resend(RecordBatch batch, DateTimeOffset receivedAt)
    {
        return BatchOf(batch.AgentId, batch.BootId, receivedAt, batch.Records);
    }

    /// <summary>
    /// Reads every gap of an agent.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="state">The state, or an empty text for every one</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the gaps by ID</returns>
    internal static async Task<List<AgentGap>> GapsOfAsync(SqliteStore store, string agentId, string state, CancellationToken cancellationToken)
    {
        var all = new List<AgentGap>();
        var after = 0L;

        while (true)
        {
            var page = await store.GapsAsync(new GapQuery
                                             {
                                                 AgentId = agentId,
                                                 State = state,
                                                 AfterId = after,
                                                 Limit = 1000
                                             },
                                             cancellationToken);

            all.AddRange(page);

            if (page.Count < 1000)
            {
                return all;
            }

            after = page[^1].Id;
        }
    }

    /// <summary>
    /// Describes the sequence gaps of an agent by number range, state and cause, lowest first.
    /// </summary>
    /// <param name="gaps">The gaps of the agent</param>
    /// <returns>The descriptions, for example <c>4-9 open sequence_missing</c></returns>
    internal static List<string> SequenceGaps(IEnumerable<AgentGap> gaps)
    {
        return gaps.Where(gap => gap.Kind == GapKind.Sequence)
                   .OrderBy(gap => gap.FirstSeq)
                   .Select(gap => $"{gap.FirstSeq}-{gap.LastSeq} {gap.State} {gap.Cause}")
                   .ToList();
    }

    /// <summary>
    /// Gets the time gaps of an agent, earliest first.
    /// </summary>
    /// <param name="gaps">The gaps of the agent</param>
    /// <returns>The time gaps</returns>
    internal static List<AgentGap> TimeGaps(IEnumerable<AgentGap> gaps)
    {
        return gaps.Where(gap => gap.Kind == GapKind.Time).OrderBy(gap => gap.From).ToList();
    }

    /// <summary>
    /// Describes every field of every gap, to compare two readings.
    /// </summary>
    /// <param name="gaps">The gaps</param>
    /// <returns>One text</returns>
    internal static string Fingerprint(IEnumerable<AgentGap> gaps)
    {
        return string.Join(";", gaps.Select(gap => $"{gap.Id}|{gap.AgentId}|{gap.Kind}|{gap.Cause}|{gap.State}|{gap.FirstSeq}|{gap.LastSeq}|{gap.From.UtcTicks}|{gap.To.UtcTicks}|{gap.DetectedAt.UtcTicks}|{gap.ChangedAt.UtcTicks}"));
    }

    /// <summary>
    /// Reads the classification the store wrote for the records of an agent.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns whether each stored number is live</returns>
    internal static async Task<Dictionary<ulong, bool>> LiveOfAsync(string path, string agentId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, bool>();

        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT seq, live FROM records WHERE agent_id = $agent AND origin = 'agent'";
        command.Parameters.AddWithValue("$agent", agentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result[(ulong)reader.GetInt64(0)] = reader.GetInt64(1) == 1;
        }

        return result;
    }

    /// <summary>
    /// Reads the first value of a query as a number.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the value</returns>
    internal static async Task<long> CountAsync(string path, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs statements on the database file.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The statements</param>
    /// <param name="cancellationToken">Cancels the statements</param>
    /// <returns>A task that completes when the statements ran</returns>
    internal static async Task ExecuteAsync(string path, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    #endregion // Methods
}