using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Holds the commands of one write transaction and inserts the records of a batch with their typed rows and their
/// full-text index entries. It is the only code that writes log lines, which keeps <c>log_fts</c> in step with
/// <c>log_lines</c>.
/// </summary>
internal sealed class BatchWriter : IDisposable
{
    #region Fields

    private readonly SqliteCommand _record;
    private readonly SqliteCommand _metric;
    private readonly SqliteCommand _logLine;
    private readonly SqliteCommand _logFts;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchWriter"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    internal BatchWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _record = Create(connection,
                         transaction,
                         "INSERT INTO records(kind, origin, source, agent_id, seq, captured_at, received_at, boot_id, clock_offset_ns, data) "
                         + "VALUES ($kind, $origin, $source, $agent, $seq, $captured, $received, $boot, $offset, $data) ON CONFLICT DO NOTHING RETURNING id");
        _metric = Create(connection,
                         transaction,
                         "INSERT INTO metrics(record_id, source, name, captured_at, value, unit, labels) VALUES ($id, $source, $name, $captured, $value, $unit, $labels)");
        _logLine = Create(connection,
                          transaction,
                          "INSERT INTO log_lines(record_id, log, program, pid, priority, message, truncated) VALUES ($id, $log, $program, $pid, $priority, $message, $truncated)");
        _logFts = Create(connection, transaction, "INSERT INTO log_fts(rowid, message) VALUES ($id, $message)");
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Inserts a record and its payload rows. Nothing more is written when the record is a duplicate of a stored one.
    /// </summary>
    /// <param name="batch">The batch the record belongs to</param>
    /// <param name="record">The record</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that returns <c>true</c> when the record was stored, <c>false</c> when it was a duplicate</returns>
    internal async Task<bool> WriteAsync(RecordBatch batch, DataRecord record, CancellationToken cancellationToken)
    {
        var payload = record.Data!;
        var typed = payload is MetricPoint or LogLine;

        Set(_record, "$kind", record.Kind);
        Set(_record, "$origin", record.Origin);
        Set(_record, "$source", record.Source);
        Set(_record, "$agent", batch.AgentId.Length == 0 ? null : batch.AgentId);
        Set(_record, "$seq", record.Origin == RecordOrigin.Agent ? (long)record.Seq : null);
        Set(_record, "$captured", StorageTime.ToNanoseconds(record.CapturedAt));
        Set(_record, "$received", StorageTime.ToNanoseconds(batch.ReceivedAt));
        Set(_record, "$boot", batch.BootId.Length == 0 ? null : batch.BootId);
        Set(_record, "$offset", batch.ClockOffsetNs);
        Set(_record, "$data", typed ? null : PayloadRegistry.Serialize(payload));

        var id = await _record.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (id is not long recordId)
        {
            return false;
        }

        if (payload is MetricPoint metric)
        {
            await WriteMetricAsync(recordId, record, metric, cancellationToken).ConfigureAwait(false);
        }
        else if (payload is LogLine line)
        {
            await WriteLogLineAsync(recordId, line, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Creates a command with its transaction.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="text">The SQL text</param>
    /// <returns>The command</returns>
    private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = text;

        return command;
    }

    /// <summary>
    /// Sets a parameter, creating it on first use; a <c>null</c> value is stored as NULL.
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="name">The name of the parameter</param>
    /// <param name="value">The value</param>
    private static void Set(SqliteCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name))
        {
            command.Parameters[name].Value = value ?? DBNull.Value;
        }
        else
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    /// <summary>
    /// Inserts the typed row of a metric.
    /// </summary>
    /// <param name="id">The ID of the record</param>
    /// <param name="record">The record</param>
    /// <param name="metric">The metric</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the row is written</returns>
    private async Task WriteMetricAsync(long id, DataRecord record, MetricPoint metric, CancellationToken cancellationToken)
    {
        string? labels = null;

        if (metric.Labels is { Count: > 0 })
        {
            labels = JsonSerializer.Serialize(new SortedDictionary<string, string>(metric.Labels, StringComparer.Ordinal));
        }

        Set(_metric, "$id", id);
        Set(_metric, "$source", record.Source);
        Set(_metric, "$name", metric.Name);
        Set(_metric, "$captured", StorageTime.ToNanoseconds(record.CapturedAt));
        Set(_metric, "$value", metric.Value);
        Set(_metric, "$unit", metric.Unit);
        Set(_metric, "$labels", labels);
        await _metric.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the typed row of a log line and its full-text index entry.
    /// </summary>
    /// <param name="id">The ID of the record</param>
    /// <param name="line">The log line</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the rows are written</returns>
    private async Task WriteLogLineAsync(long id, LogLine line, CancellationToken cancellationToken)
    {
        Set(_logLine, "$id", id);
        Set(_logLine, "$log", line.Log);
        Set(_logLine, "$program", line.Program);
        Set(_logLine, "$pid", (long)line.Pid);
        Set(_logLine, "$priority", line.Priority is null ? null : (long)line.Priority.Value);
        Set(_logLine, "$message", line.Message);
        Set(_logLine, "$truncated", line.Truncated ? 1L : 0L);
        await _logLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        Set(_logFts, "$id", id);
        Set(_logFts, "$message", line.Message);
        await _logFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _record.Dispose();
        _metric.Dispose();
        _logLine.Dispose();
        _logFts.Dispose();
    }

    #endregion // IDisposable
}