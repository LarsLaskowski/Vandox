using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325, S4487, IDE0052
/// <summary>
/// Keeps the gaps, the clock samples and the stream state of one agent in step with the batch of a write transaction.
/// </summary>
internal sealed class AgentTimeline : IDisposable
{
    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly string _agentId;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentTimeline"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="agentId">The agent ID</param>
    private AgentTimeline(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        _connection = connection;
        _transaction = transaction;
        _agentId = agentId;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Loads the stream state of an agent.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="agentId">The agent ID</param>
    /// <returns>The timeline</returns>
    internal static AgentTimeline Load(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        return new AgentTimeline(connection, transaction, agentId);
    }

    /// <summary>
    /// Updates the gaps and the offset for the records of a batch.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="restartWait">Whether this is the first batch of the agent since the store was opened</param>
    /// <param name="cancellationToken">Cancels the work</param>
    internal void Prepare(RecordBatch batch, bool restartWait, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Decides whether a record of the prepared batch is live.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns><see langword="true"/> when the record is live</returns>
    internal bool IsLive(DataRecord record)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Records the sample times of the stored metric records and updates the time gaps.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="stored">The records that were stored</param>
    /// <param name="cancellationToken">Cancels the work</param>
    internal void WriteSampleTimes(RecordBatch batch, IReadOnlyList<DataRecord> stored, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the changed rows and the stream state.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work</param>
    /// <returns>What the batch changed, or <see langword="null"/> when nothing of note</returns>
    internal TimelineChange? Finish(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
    }

    #endregion // IDisposable
}