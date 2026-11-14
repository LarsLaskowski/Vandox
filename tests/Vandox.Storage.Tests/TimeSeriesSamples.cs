using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds records and rows and runs bulk statements for the tests of the time-series reads.
/// </summary>
internal static class TimeSeriesSamples
{
    #region Constants

    /// <summary>
    /// The source of the metrics the samples write.
    /// </summary>
    internal const string MetricSource = "system";

    /// <summary>
    /// The largest limit of the event projections and of the gap rows.
    /// </summary>
    internal const int EventLimit = 1001;

    /// <summary>
    /// The largest limit of the service changes and the most units of one previous-state read.
    /// </summary>
    internal const int ServiceLimit = 2001;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a metric record.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="value">The value</param>
    /// <param name="labels">The labels, or <c>null</c> for none</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(string source, string name, DateTimeOffset capturedAt, double value, Dictionary<string, string>? labels = null)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = source,
                   CapturedAt = capturedAt,
                   Data = new MetricPoint
                          {
                              Name = name,
                              Value = value,
                              Unit = "percent",
                              Labels = labels
                          }
               };
    }

    /// <summary>
    /// Creates a service state record.
    /// </summary>
    /// <param name="unit">The unit</param>
    /// <param name="activeState">The active state</param>
    /// <param name="activeEnterAt">The time the unit became active, or <c>null</c> for none</param>
    /// <param name="restarts">The restart counter</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord Service(string unit, string activeState, DateTimeOffset? activeEnterAt, uint restarts, DateTimeOffset capturedAt)
    {
        return Wrap("services",
                    capturedAt,
                    new ServiceState
                    {
                        Unit = unit,
                        LoadState = "loaded",
                        ActiveState = activeState,
                        SubState = "running",
                        ActiveEnterAt = activeEnterAt,
                        Restarts = restarts
                    });
    }

    /// <summary>
    /// Creates a kernel event of a boot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="bootId">The boot ID</param>
    /// <returns>The record</returns>
    internal static DataRecord Boot(DateTimeOffset capturedAt, string bootId)
    {
        return Wrap("kernel",
                    capturedAt,
                    new KernelEvent
                    {
                        Type = KernelEvent.TypeBoot,
                        Boot = new Boot
                               {
                                   BootId = bootId
                               }
                    });
    }

    /// <summary>
    /// Creates a kernel event of an OOM kill.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="command">The victim command</param>
    /// <param name="pid">The victim process ID</param>
    /// <param name="bootId">The boot ID of the kill</param>
    /// <param name="kernelTimeNs">The kernel time, or <c>null</c> for none</param>
    /// <param name="message">The kernel message</param>
    /// <returns>The record</returns>
    internal static DataRecord Kill(DateTimeOffset capturedAt, string command, int pid, string bootId, long? kernelTimeNs, string message)
    {
        return Wrap("kernel",
                    capturedAt,
                    new KernelEvent
                    {
                        Type = KernelEvent.TypeOomKill,
                        Message = message,
                        OomKill = new OomKill
                                  {
                                      VictimPid = pid,
                                      VictimCommand = command,
                                      BootId = bootId,
                                      KernelTimeNs = kernelTimeNs
                                  }
                    });
    }

    /// <summary>
    /// Creates a gap record.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    /// <param name="cause">The cause</param>
    /// <param name="collector">The collector, or an empty text for none</param>
    /// <param name="firstSeq">The first missing number, 0 for none</param>
    /// <param name="lastSeq">The last missing number, 0 for none</param>
    /// <returns>The record</returns>
    internal static DataRecord GapRecord(DateTimeOffset capturedAt, DateTimeOffset from, DateTimeOffset to, string cause, string collector, ulong firstSeq, ulong lastSeq)
    {
        return Wrap("agent",
                    capturedAt,
                    new Gap
                    {
                        From = from,
                        To = to,
                        Cause = cause,
                        Collector = collector,
                        FirstSeq = firstSeq,
                        LastSeq = lastSeq
                    });
    }

    /// <summary>
    /// Creates the start record of a backup run.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="bootId">The boot ID</param>
    /// <param name="startedAt">The start of the run</param>
    /// <param name="command">The command</param>
    /// <returns>The record</returns>
    internal static DataRecord BackupStart(DateTimeOffset capturedAt, string bootId, DateTimeOffset startedAt, string command)
    {
        return Wrap("backups",
                    capturedAt,
                    new BackupRun
                    {
                        Phase = BackupRun.PhaseStart,
                        BootId = bootId,
                        StartedAt = startedAt,
                        Pid = 5150,
                        Command = command,
                        Processes = 1
                    });
    }

    /// <summary>
    /// Creates the end record of a backup run.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="bootId">The boot ID</param>
    /// <param name="startedAt">The start of the run</param>
    /// <param name="lastSeenAt">The last time the run was seen</param>
    /// <param name="goneBy">The time the run was found gone</param>
    /// <returns>The record</returns>
    internal static DataRecord BackupEnd(DateTimeOffset capturedAt, string bootId, DateTimeOffset startedAt, DateTimeOffset lastSeenAt, DateTimeOffset goneBy)
    {
        return Wrap("backups",
                    capturedAt,
                    new BackupRun
                    {
                        Phase = BackupRun.PhaseEnd,
                        BootId = bootId,
                        StartedAt = startedAt,
                        LastSeenAt = lastSeenAt,
                        GoneBy = goneBy,
                        MaxProcesses = 1
                    });
    }

    /// <summary>
    /// Writes records as one batch of an agent ID and a boot ID, and checks that every record was stored.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="agentId">The agent ID of the batch, or an empty text for none</param>
    /// <param name="bootId">The boot ID of the batch, or an empty text for none</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task WriteAsync(SqliteStore store, string agentId, string bootId, CancellationToken cancellationToken, params DataRecord[] records)
    {
        var batch = Samples.Batch(records);

        batch.AgentId = agentId;
        batch.BootId = bootId;

        var result = await store.WriteBatchAsync(batch, cancellationToken);

        Assert.AreEqual(records.Length, result.Stored, "every record of the arranged batch is stored");
    }

    /// <summary>
    /// Creates a query of a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <param name="afterId">The ID after which rows at the start time are returned</param>
    /// <returns>The query</returns>
    internal static TimeRangeQuery Range(DateTimeOffset from, DateTimeOffset to, int limit = EventLimit, long afterId = 0)
    {
        return new TimeRangeQuery
               {
                   From = from,
                   To = to,
                   Limit = limit,
                   AfterId = afterId
               };
    }

    /// <summary>
    /// Creates the labels of one series line.
    /// </summary>
    /// <param name="mode">The value of the label <c>mode</c></param>
    /// <returns>The labels</returns>
    internal static Dictionary<string, string> Mode(string mode)
    {
        return new Dictionary<string, string>
               {
                   ["mode"] = mode
               };
    }

    /// <summary>
    /// Describes the points of a line as time offsets from <see cref="Samples.Base"/> in seconds, with the sample count.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The description, for example <c>0s x2, 30s x1</c></returns>
    internal static string Describe(MetricLine line)
    {
        return string.Join(", ", line.Points.Select(point => $"{(point.At - Samples.Base).TotalSeconds:0}s x{point.Count}"));
    }

    /// <summary>
    /// Inserts one 5-minute bucket directly, as a test of the readers needs it.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="labels">The label text, empty for none</param>
    /// <param name="bucket">The start of the bucket</param>
    /// <param name="sum">The sum of the values</param>
    /// <param name="count">The number of values</param>
    /// <param name="value">The minimum, maximum and last value of the bucket</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the bucket is stored</returns>
    internal static async Task InsertBucketAsync(string directory, string labels, DateTimeOffset bucket, double sum, long count, double value, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO metric_rollups_5m(source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                              + "VALUES ($source, 'load', $labels, $bucket, $value, $value, $sum, $count, $value, $bucket, 1)";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$source", MetricSource);
        command.Parameters.AddWithValue("$labels", labels);
        command.Parameters.AddWithValue("$bucket", StorageTime.ToNanoseconds(bucket));
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$sum", sum);
        command.Parameters.AddWithValue("$count", count);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts many label sets of one series at one bucket directly, named <c>{"set":"0000"}</c> upward.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="count">The number of label sets</param>
    /// <param name="bucket">The start of their bucket</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the buckets are stored</returns>
    internal static async Task InsertLabelSetsAsync(string directory, int count, DateTimeOffset bucket, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) "
                              + "INSERT INTO metric_rollups_5m(source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                              + "SELECT $source, 'load', printf('{\"set\":\"%04d\"}', i), $bucket, 1, 1, 1, 1, 1, $bucket, 1 FROM n";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$source", MetricSource);
        command.Parameters.AddWithValue("$bucket", StorageTime.ToNanoseconds(bucket));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts <c>service_state</c> records of one unit name pattern directly, one per step.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="pattern">The unit name, or a <c>printf</c> pattern with one <c>%d</c> for the index of the record</param>
    /// <param name="first">The capture time of the first record</param>
    /// <param name="count">The number of records</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task InsertServiceStatesAsync(string directory, string pattern, DateTimeOffset first, int count, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) "
                              + "INSERT INTO records(kind, origin, source, captured_at, received_at, data) "
                              + "SELECT 'service_state', 'backend', 'services', $first + i * 1000000000, $first + i * 1000000000, "
                              + "json_object('unit', printf($pattern, i), 'load_state', 'loaded', 'active_state', 'active', 'sub_state', 'running', 'restarts', 0) FROM n";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$first", StorageTime.ToNanoseconds(first));
        command.Parameters.AddWithValue("$pattern", pattern);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a record directly with a payload text that no validated write would store.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="kind">The record kind</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="data">The payload text</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the record is stored</returns>
    internal static async Task InsertRecordAsync(string directory, string kind, DateTimeOffset capturedAt, string data, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO records(kind, origin, source, captured_at, received_at, data) VALUES ($kind, 'backend', 'forged', $at, $at, $data)";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$at", StorageTime.ToNanoseconds(capturedAt));
        command.Parameters.AddWithValue("$data", data);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a gap row directly.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="row">The row</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the row is stored</returns>
    internal static async Task InsertGapAsync(string directory, SampleGap row, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO gaps(agent_id, kind, cause, state, first_seq, last_seq, from_seq, to_seq, from_at, to_at, detected_at, changed_at) "
                              + "VALUES ($agent, $kind, 'no_data', $state, $first, $last, $fromSeq, $toSeq, $from, $to, $from, $to)";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$agent", row.AgentId);
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$state", row.State);
        command.Parameters.AddWithValue("$first", row.FirstSeq is null ? DBNull.Value : row.FirstSeq);
        command.Parameters.AddWithValue("$last", row.LastSeq is null ? DBNull.Value : row.LastSeq);
        command.Parameters.AddWithValue("$fromSeq", row.FromSeq is null ? DBNull.Value : row.FromSeq);
        command.Parameters.AddWithValue("$toSeq", row.ToSeq is null ? DBNull.Value : row.ToSeq);
        command.Parameters.AddWithValue("$from", StorageTime.ToNanoseconds(row.From));
        command.Parameters.AddWithValue("$to", StorageTime.ToNanoseconds(row.To));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the first column of the first row of a statement as text.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The statement, a test-owned literal</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the text, empty for NULL</returns>
    internal static async Task<string> TextAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal passed by the calling test
        command.CommandText = sql;
#pragma warning restore CA2100

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Wraps a payload in a backend record.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="payload">The payload</param>
    /// <returns>The record</returns>
    private static DataRecord Wrap(string source, DateTimeOffset capturedAt, IPayload payload)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = source,
                   CapturedAt = capturedAt,
                   Data = payload
               };
    }

    #endregion // Methods
}