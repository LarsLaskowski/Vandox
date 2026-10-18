#pragma warning disable RH2003, S2325, S4487

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Writes the connection mark of a stored agent snapshot and, once per batch, the comparison of each affected agent start.
/// </summary>
internal sealed class ListenerMarkWriter : IDisposable
{
    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

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
    internal long PayloadDecodes => throw new NotImplementedException();

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
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Computes the comparison of each affected start once and stores it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write</param>
    internal void WriteComparisons(CancellationToken cancellationToken)
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