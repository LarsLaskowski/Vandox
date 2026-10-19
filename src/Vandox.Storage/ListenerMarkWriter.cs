using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// Writes the connection mark of a stored agent snapshot and, once per batch, the comparison of each affected agent start.
/// The only rows it ever updates are rows of <c>listener_comparisons</c>; a <c>records</c> row is never changed.
/// </summary>
internal sealed class ListenerMarkWriter : IDisposable
{
    #region Constants

    private const string ConnectionsSource = "connections";

    private const string IdParameter = "$id";

    private const string AgentParameter = "$agent";

    private const string SeqParameter = "$seq";

    private const string CapturedParameter = "$captured";

    private const string CountParameter = "$count";

    private const string ListenersParameter = "$listeners";

    private const string InsertMark = "INSERT INTO connection_marks(record_id, agent_id, seq, captured_at, listener_check, new_count, new_listeners) VALUES ($id, $agent, $seq, $captured, $check, $count, $listeners)";

    private const string NextMark = "SELECT record_id, seq, captured_at, listener_check FROM connection_marks WHERE agent_id = $agent AND seq > $seq ORDER BY seq LIMIT 1";

    private const string Predecessor = "SELECT m.record_id, m.seq, m.captured_at, r.data FROM connection_marks m JOIN records r ON r.id = m.record_id WHERE m.agent_id = $agent AND m.seq < $seq ORDER BY m.seq DESC LIMIT 1";

    private const string StartData = "SELECT data FROM records WHERE id = $id";

    private const string ReplaceComparison = "INSERT OR REPLACE INTO listener_comparisons(record_id, agent_id, seq, captured_at, base_record_id, base_seq, base_captured_at, outcome, new_count, new_listeners, finding) "
                                             + "VALUES ($id, $agent, $seq, $captured, $baseId, $baseSeq, $baseCaptured, $outcome, $count, $listeners, $finding)";

    #endregion // Constants

    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly Dictionary<long, (string AgentId, long Seq, long CapturedAt)> _starts = [];
    private SqliteCommand? _insertMark;
    private SqliteCommand? _nextMark;
    private SqliteCommand? _predecessor;
    private SqliteCommand? _startData;
    private SqliteCommand? _replaceComparison;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ListenerMarkWriter"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    internal ListenerMarkWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of stored payloads decoded so far.
    /// </summary>
    internal long PayloadDecodes { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Determines whether a record is a usable agent connection snapshot.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="agentId">The agent ID of the batch</param>
    /// <returns><c>true</c> when the record is usable</returns>
    internal static bool IsUsable(DataRecord record, string agentId)
    {
        return record.Origin == RecordOrigin.Agent
               && record.Source == ConnectionsSource
               && agentId.Length > 0
               && record.Data is ConnectionSnapshot { ListenerCheck: not null, Inventory: not null, Tables.Count: > 0 };
    }

    /// <summary>
    /// Inserts the mark of a stored snapshot and notes the starts whose comparison it affects.
    /// </summary>
    /// <param name="recordId">The ID of the stored record</param>
    /// <param name="agentId">The agent ID of the batch</param>
    /// <param name="record">The record</param>
    /// <param name="snapshot">The snapshot of the record</param>
    internal void Write(long recordId, string agentId, DataRecord record, ConnectionSnapshot snapshot)
    {
        var check = snapshot.ListenerCheck!;
        var capturedAt = StorageTime.ToNanoseconds(record.CapturedAt);
        var seq = (long)record.Seq;
        var reported = check is ListenerQueries.CheckAll or ListenerQueries.CheckPrivileged;
        var listed = reported ? snapshot.NewListeners ?? [] : [];
        var count = reported ? listed.Count + (long)snapshot.NewListenersOmitted : 0L;
        var command = _insertMark ??= Create(InsertMark, IdParameter, AgentParameter, SeqParameter, CapturedParameter, "$check", CountParameter, ListenersParameter);

        command.Parameters[IdParameter].Value = recordId;
        command.Parameters[AgentParameter].Value = agentId;
        command.Parameters[SeqParameter].Value = seq;
        command.Parameters[CapturedParameter].Value = capturedAt;
        command.Parameters["$check"].Value = check;
        command.Parameters[CountParameter].Value = count;
        command.Parameters[ListenersParameter].Value = count > 0 ? ListenerQueries.SerializeListeners(listed.Take(ListenerComparer.MaxNewListeners)) : DBNull.Value;
        command.ExecuteNonQuery();

        if (check == ListenerQueries.CheckStart)
        {
            _starts[recordId] = (agentId, seq, capturedAt);
        }

        NoteNextStart(agentId, seq);
    }

    /// <summary>
    /// Computes the comparison of each affected start once and stores it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write</param>
    internal void WriteComparisons(CancellationToken cancellationToken)
    {
        foreach (var (recordId, start) in _starts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteComparison(recordId, start.AgentId, start.Seq, start.CapturedAt);
        }

        _starts.Clear();
    }

    /// <summary>
    /// Creates a command of the writer transaction.
    /// </summary>
    /// <param name="text">The SQL text</param>
    /// <param name="names">The names of the parameters the text uses</param>
    /// <returns>The command</returns>
    private SqliteCommand Create(string text, params string[] names)
    {
        var command = _connection.CreateCommand();

        command.Transaction = _transaction;
        command.CommandText = text;

        foreach (var name in names)
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        return command;
    }

    /// <summary>
    /// Notes the next mark of the agent by sequence number when it is a start, because the mark just written is now its base.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="seq">The sequence number of the mark just written</param>
    private void NoteNextStart(string agentId, long seq)
    {
        var command = _nextMark ??= Create(NextMark, AgentParameter, SeqParameter);

        command.Parameters[AgentParameter].Value = agentId;
        command.Parameters[SeqParameter].Value = seq;

        using var reader = command.ExecuteReader();

        if (reader.Read() && reader.GetString(3) == ListenerQueries.CheckStart)
        {
            _starts[reader.GetInt64(0)] = (agentId, reader.GetInt64(1), reader.GetInt64(2));
        }
    }

    /// <summary>
    /// Compares a start with the mark below it and stores the result, replacing an earlier one.
    /// </summary>
    /// <param name="recordId">The ID of the start's record</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="seq">The sequence number of the start</param>
    /// <param name="capturedAt">The capture time of the start in Unix nanoseconds</param>
    private void WriteComparison(long recordId, string agentId, long seq, long capturedAt)
    {
        var command = _predecessor ??= Create(Predecessor, AgentParameter, SeqParameter);

        command.Parameters[AgentParameter].Value = agentId;
        command.Parameters[SeqParameter].Value = seq;

        (long Id, long Seq, long CapturedAt)? baseMark = null;
        ConnectionSnapshot? earlier = null;

        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                baseMark = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
                earlier = Decode(reader, 3);
            }
        }

        Store(recordId, agentId, seq, capturedAt, baseMark, Compare(recordId, baseMark is not null, earlier));
    }

    /// <summary>
    /// Compares the decoded snapshot below a start with the start.
    /// </summary>
    /// <param name="recordId">The ID of the start's record</param>
    /// <param name="hasBase">Whether a mark lies below the start</param>
    /// <param name="earlier">The decoded snapshot below the start, <c>null</c> when it does not decode</param>
    /// <returns>The comparison</returns>
    private ListenerComparison Compare(long recordId, bool hasBase, ConnectionSnapshot? earlier)
    {
        if (hasBase)
        {
            var later = earlier is null ? null : Decode(recordId);

            return earlier is null || later is null
                       ? new ListenerComparison
                         {
                             Outcome = ListenerOutcome.NotComparable
                         }
                       : ListenerComparer.Compare(earlier, later);
        }

        return new ListenerComparison
               {
                   Outcome = ListenerOutcome.NoEarlierInventory
               };
    }

    /// <summary>
    /// Stores the comparison of a start.
    /// </summary>
    /// <param name="recordId">The ID of the start's record</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="seq">The sequence number of the start</param>
    /// <param name="capturedAt">The capture time of the start in Unix nanoseconds</param>
    /// <param name="baseMark">The record ID, sequence number and capture time of the mark compared with, if any</param>
    /// <param name="comparison">The comparison</param>
    private void Store(long recordId, string agentId, long seq, long capturedAt, (long Id, long Seq, long CapturedAt)? baseMark, ListenerComparison comparison)
    {
        var count = comparison.NewListeners.Count + comparison.Omitted;
        var command = _replaceComparison ??= Create(ReplaceComparison, IdParameter, AgentParameter, SeqParameter, CapturedParameter, "$baseId", "$baseSeq", "$baseCaptured", "$outcome", CountParameter, ListenersParameter, "$finding");

        command.Parameters[IdParameter].Value = recordId;
        command.Parameters[AgentParameter].Value = agentId;
        command.Parameters[SeqParameter].Value = seq;
        command.Parameters[CapturedParameter].Value = capturedAt;
        command.Parameters["$baseId"].Value = baseMark?.Id ?? (object)DBNull.Value;
        command.Parameters["$baseSeq"].Value = baseMark?.Seq ?? (object)DBNull.Value;
        command.Parameters["$baseCaptured"].Value = baseMark?.CapturedAt ?? (object)DBNull.Value;
        command.Parameters["$outcome"].Value = ListenerQueries.OutcomeText(comparison.Outcome);
        command.Parameters[CountParameter].Value = count;
        command.Parameters[ListenersParameter].Value = count > 0 ? ListenerQueries.SerializeListeners(comparison.NewListeners) : DBNull.Value;
        command.Parameters["$finding"].Value = count > 0 || ListenerQueries.IsNotCompared(comparison.Outcome) ? 1L : 0L;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Decodes the stored payload of a start.
    /// </summary>
    /// <param name="recordId">The ID of the start's record</param>
    /// <returns>The snapshot, or <c>null</c> when the payload does not decode</returns>
    private ConnectionSnapshot? Decode(long recordId)
    {
        var command = _startData ??= Create(StartData, IdParameter);

        command.Parameters[IdParameter].Value = recordId;

        using var reader = command.ExecuteReader();

        return reader.Read() ? Decode(reader, 0) : null;
    }

    /// <summary>
    /// Decodes the stored payload in a column of a row.
    /// </summary>
    /// <param name="reader">The reader positioned on the row</param>
    /// <param name="column">The column of the payload text</param>
    /// <returns>The snapshot, or <c>null</c> when the payload is missing or does not decode</returns>
    private ConnectionSnapshot? Decode(SqliteDataReader reader, int column)
    {
        PayloadDecodes++;

        if (reader.IsDBNull(column))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reader.GetString(column));

            return PayloadRegistry.Deserialize(RecordKind.ConnectionSnapshot, document.RootElement) as ConnectionSnapshot;
        }
        catch (JsonException)
        {
            // An undecodable stored payload makes the start not comparable; it never fails the batch.
            return null;
        }
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _insertMark?.Dispose();
        _nextMark?.Dispose();
        _predecessor?.Dispose();
        _startData?.Dispose();
        _replaceComparison?.Dispose();
    }

    #endregion // IDisposable
}