using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// The SQL of the incident table.
/// </summary>
internal static class IncidentRepository
{
    #region Methods

    /// <summary>
    /// Validates an incident and serializes it.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="savedAt">The time of the save</param>
    /// <returns>The serialized JSON</returns>
    /// <exception cref="InvalidIncidentException">The incident breaks a rule</exception>
    internal static string Validate(Incident incident, DateTimeOffset savedAt)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Saves a validated incident within a transaction.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="incident">The incident</param>
    /// <param name="data">The serialized incident</param>
    /// <param name="savedAt">The time of the save</param>
    /// <param name="cancellationToken">Cancels the save</param>
    /// <returns>A task that returns the result</returns>
    internal static Task<IncidentSaveResult> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, Incident incident, string data, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the SQL of an incident query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(IncidentQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the current row as a stored incident.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The incident</returns>
    /// <exception cref="StoreException">The data does not decode</exception>
    internal static StoredIncident Read(SqliteDataReader reader)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}