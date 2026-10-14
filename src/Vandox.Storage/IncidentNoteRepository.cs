#pragma warning disable RH2003, S2325

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The SQL of the incident notes.
/// </summary>
internal static class IncidentNoteRepository
{
    #region Methods

    /// <summary>
    /// Validates an edit.
    /// </summary>
    /// <param name="edit">The edit</param>
    /// <exception cref="InvalidNoteException">The edit breaks a rule</exception>
    internal static void Validate(IncidentNoteEdit edit)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Saves a validated edit within a transaction.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="edit">The edit</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The outcome</returns>
    internal static Task<IncidentNoteOutcome> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Moves the notes of an incident to another one.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="fromIncident">The incident that goes away</param>
    /// <param name="toIncident">The incident that keeps the notes</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the notes are moved</returns>
    internal static Task MoveAsync(SqliteConnection connection, SqliteTransaction transaction, long fromIncident, long toIncident, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the statement that reads the notes of an incident.
    /// </summary>
    /// <param name="incidentId">The number of the incident</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildRead(long incidentId)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a note row.
    /// </summary>
    /// <param name="reader">The reader, positioned on a row of <see cref="BuildRead"/></param>
    /// <returns>The note</returns>
    internal static IncidentNote Read(SqliteDataReader reader)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}