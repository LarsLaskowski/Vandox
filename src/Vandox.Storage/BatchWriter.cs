using System.Buffers;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Holds the commands of one write transaction and inserts the records of a batch with their typed rows and their
/// full-text index entries. It is the only code that writes log lines, which keeps <c>log_fts</c> in step with
/// <c>log_lines</c>. The parameters are created once and set by reference, and the calls are synchronous because
/// Microsoft.Data.Sqlite runs them synchronously anyway: per record this saves the lookups by name and the asynchronous
/// state machines, which matters on a slow host (record 0082).
/// </summary>
internal sealed class BatchWriter : IDisposable
{
    #region Fields

    private readonly SqliteCommand _record;
    private readonly SqliteCommand _metric;
    private readonly SqliteCommand _logLine;
    private readonly SqliteCommand _logFts;
    private readonly SqliteParameter[] _recordValues;
    private readonly SqliteParameter[] _metricValues;
    private readonly SqliteParameter[] _logLineValues;
    private readonly SqliteParameter[] _logFtsValues;
    private readonly ArrayBufferWriter<byte> _labelBuffer = new();
    private readonly Utf8JsonWriter _labelWriter;

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
        _recordValues = Bind(_record, "$kind", "$origin", "$source", "$agent", "$seq", "$captured", "$received", "$boot", "$offset", "$data");
        _metricValues = Bind(_metric, "$id", "$source", "$name", "$captured", "$value", "$unit", "$labels");
        _logLineValues = Bind(_logLine, "$id", "$log", "$program", "$pid", "$priority", "$message", "$truncated");
        _logFtsValues = Bind(_logFts, "$id", "$message");
        _labelWriter = new Utf8JsonWriter(_labelBuffer);
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Serializes metric labels as compact JSON with the keys in ordinal order, the text that is stored in <c>metrics.labels</c>.
    /// </summary>
    /// <param name="labels">The labels</param>
    /// <returns>The JSON object</returns>
    internal static string SerializeLabels(IReadOnlyDictionary<string, string> labels)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using var writer = new Utf8JsonWriter(buffer);

        return SerializeLabels(labels, buffer, writer);
    }

    /// <summary>
    /// Inserts a record and its payload rows. Nothing more is written when the record is a duplicate of a stored one.
    /// </summary>
    /// <param name="batch">The batch the record belongs to</param>
    /// <param name="record">The record</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns><c>true</c> when the record was stored, <c>false</c> when it was a duplicate</returns>
    internal bool Write(RecordBatch batch, DataRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = record.Data!;
        var typed = payload is MetricPoint or LogLine;

        _recordValues[0].Value = record.Kind;
        _recordValues[1].Value = record.Origin;
        _recordValues[2].Value = record.Source;
        _recordValues[3].Value = batch.AgentId.Length == 0 ? DBNull.Value : (object)batch.AgentId;
        _recordValues[4].Value = record.Origin == RecordOrigin.Agent ? (object)(long)record.Seq : DBNull.Value;
        _recordValues[5].Value = StorageTime.ToNanoseconds(record.CapturedAt);
        _recordValues[6].Value = StorageTime.ToNanoseconds(batch.ReceivedAt);
        _recordValues[7].Value = batch.BootId.Length == 0 ? DBNull.Value : (object)batch.BootId;
        _recordValues[8].Value = batch.ClockOffsetNs ?? (object)DBNull.Value;
        _recordValues[9].Value = typed ? DBNull.Value : (object)PayloadRegistry.Serialize(payload);

        if (_record.ExecuteScalar() is not long recordId)
        {
            return false;
        }

        if (payload is MetricPoint metric)
        {
            WriteMetric(recordId, record, metric);
        }
        else if (payload is LogLine line)
        {
            WriteLogLine(recordId, line);
        }

        return true;
    }

    /// <summary>
    /// Serializes labels into a reused buffer and writer.
    /// </summary>
    /// <param name="labels">The labels</param>
    /// <param name="buffer">The buffer</param>
    /// <param name="writer">The writer, which is reset to the buffer</param>
    /// <returns>The JSON object</returns>
    private static string SerializeLabels(IReadOnlyDictionary<string, string> labels, ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer)
    {
        var keys = labels.Keys.ToArray();

        Array.Sort(keys, StringComparer.Ordinal);
        buffer.ResetWrittenCount();
        writer.Reset(buffer);
        writer.WriteStartObject();

        foreach (var key in keys)
        {
            writer.WriteString(key, labels[key]);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
    /// Creates the parameters of a command in the given order.
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="names">The names of the parameters</param>
    /// <returns>The parameters, in the order of <paramref name="names"/></returns>
    private static SqliteParameter[] Bind(SqliteCommand command, params string[] names)
    {
        var parameters = new SqliteParameter[names.Length];

        for (var index = 0; index < names.Length; index++)
        {
            parameters[index] = command.Parameters.Add(new SqliteParameter(names[index], DBNull.Value));
        }

        return parameters;
    }

    /// <summary>
    /// Inserts the typed row of a metric.
    /// </summary>
    /// <param name="id">The ID of the record</param>
    /// <param name="record">The record</param>
    /// <param name="metric">The metric</param>
    private void WriteMetric(long id, DataRecord record, MetricPoint metric)
    {
        _metricValues[0].Value = id;
        _metricValues[1].Value = record.Source;
        _metricValues[2].Value = metric.Name;
        _metricValues[3].Value = StorageTime.ToNanoseconds(record.CapturedAt);
        _metricValues[4].Value = metric.Value;
        _metricValues[5].Value = metric.Unit;
        _metricValues[6].Value = metric.Labels is { Count: > 0 } ? (object)SerializeLabels(metric.Labels, _labelBuffer, _labelWriter) : DBNull.Value;
        _metric.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts the typed row of a log line and its full-text index entry.
    /// </summary>
    /// <param name="id">The ID of the record</param>
    /// <param name="line">The log line</param>
    private void WriteLogLine(long id, LogLine line)
    {
        _logLineValues[0].Value = id;
        _logLineValues[1].Value = line.Log;
        _logLineValues[2].Value = line.Program;
        _logLineValues[3].Value = (long)line.Pid;
        _logLineValues[4].Value = line.Priority is null ? DBNull.Value : (object)(long)line.Priority.Value;
        _logLineValues[5].Value = line.Message;
        _logLineValues[6].Value = line.Truncated ? 1L : 0L;
        _logLine.ExecuteNonQuery();

        _logFtsValues[0].Value = id;
        _logFtsValues[1].Value = line.Message;
        _logFts.ExecuteNonQuery();
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
        _labelWriter.Dispose();
    }

    #endregion // IDisposable
}