using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// The SQL of the incident table.
/// </summary>
internal static class IncidentRepository
{
    #region Constants

    private const string Prefix = "store: invalid incident: ";
    private const string QueryPrefix = "store: invalid query: ";
    private const string DecodingMessage = "store: reading incidents: decoding incident data";
    private const string Columns = "SELECT id, started_at, ended_at, first_kill_at, kill_count, created_at, updated_at, data FROM incidents";

    #endregion // Constants

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
        ArgumentNullException.ThrowIfNull(incident);

        Require(StorageTime.InStorableRange(incident.Start) && StorageTime.InStorableRange(incident.End), "start and end must be within the storable range");
        Require(incident.Start < incident.End, "start must be before end");
        Require(incident.FirstKillAt >= incident.Start && incident.FirstKillAt < incident.End, "the first kill must lie in the window");
        Require(incident.KillCount >= 1, "kill count must be at least 1");
        Require(StorageTime.InStorableRange(savedAt), "saved at must be within the storable range");

        var data = JsonSerializer.Serialize(incident, IncidentJson.Options);

        Require(Encoding.UTF8.GetByteCount(data) <= StorageLimits.MaxIncidentDataBytes, $"data is larger than {StorageLimits.MaxIncidentDataBytes} bytes");

        return data;
    }

    /// <summary>
    /// Saves a validated incident within a transaction.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="incident">The incident</param>
    /// <param name="data">The serialized JSON of the incident</param>
    /// <param name="savedAt">The time of the save</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The outcome</returns>
    internal static async Task<IncidentSaveResult> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, Incident incident, string data, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        var start = StorageTime.ToNanoseconds(incident.Start);
        var end = StorageTime.ToNanoseconds(incident.End);
        var firstKill = StorageTime.ToNanoseconds(incident.FirstKillAt);
        var saved = StorageTime.ToNanoseconds(savedAt);
        var (ids, same) = await FindOverlappingAsync(connection, transaction, (start, end, firstKill), incident.KillCount, data, cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            using var insert = Command(connection, transaction, "INSERT INTO incidents (started_at, ended_at, first_kill_at, kill_count, created_at, updated_at, data) VALUES ($start, $end, $first, $count, $saved, $saved, $data) RETURNING id");

            Bind(insert, start, end, firstKill, incident.KillCount, data);
            insert.Parameters.AddWithValue("$saved", saved);

            var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

            return new IncidentSaveResult
                   {
                       Id = id,
                       Outcome = IncidentSaveOutcome.Created
                   };
        }

        foreach (var other in ids.Skip(1))
        {
            await IncidentNoteRepository.MoveAsync(connection, transaction, other, ids[0], cancellationToken).ConfigureAwait(false);

            using var delete = Command(connection, transaction, "DELETE FROM incidents WHERE id = $id");

            delete.Parameters.AddWithValue("$id", other);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var merged = ids.Count - 1L;

        if (merged == 0 && same)
        {
            return new IncidentSaveResult
                   {
                       Id = ids[0],
                       Outcome = IncidentSaveOutcome.Unchanged
                   };
        }

        using var update = Command(connection, transaction, "UPDATE incidents SET started_at = $start, ended_at = $end, first_kill_at = $first, kill_count = $count, updated_at = $saved, data = $data WHERE id = $id");

        Bind(update, start, end, firstKill, incident.KillCount, data);
        update.Parameters.AddWithValue("$saved", saved);
        update.Parameters.AddWithValue("$id", ids[0]);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new IncidentSaveResult
               {
                   Id = ids[0],
                   Outcome = IncidentSaveOutcome.Updated,
                   Merged = merged
               };
    }

    /// <summary>
    /// Builds the statement that reads the incidents overlapping a range.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(IncidentQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$from"] = StorageTime.ToNanoseconds(query.From),
                             ["$to"] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)query.Limit
                         };

        return (Columns + " WHERE started_at < $to AND ended_at > $from ORDER BY started_at, id LIMIT $limit", parameters);
    }

    /// <summary>
    /// Reads an incident row.
    /// </summary>
    /// <param name="reader">The reader, positioned on a row of <see cref="Build"/></param>
    /// <returns>The stored incident</returns>
    /// <exception cref="StoreException">The data is not an incident</exception>
    internal static StoredIncident Read(SqliteDataReader reader)
    {
        Incident? incident;

        try
        {
            incident = JsonSerializer.Deserialize<Incident>(reader.GetFieldValue<byte[]>(7), IncidentJson.Options);
        }
        catch (JsonException exception)
        {
            throw new StoreException(DecodingMessage, exception);
        }

        return new StoredIncident
               {
                   Id = reader.GetInt64(0),
                   Incident = incident ?? throw new StoreException(DecodingMessage),
                   CreatedAt = StorageTime.FromNanoseconds(reader.GetInt64(5)),
                   UpdatedAt = StorageTime.FromNanoseconds(reader.GetInt64(6))
               };
    }

    /// <summary>
    /// Validates a list query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateList(IncidentListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        RequireQuery(query.Limit >= 1 && query.Limit <= StorageLimits.MaxIncidentListLimit, $"limit must be between 1 and {StorageLimits.MaxIncidentListLimit}");

        if (query.Before is null)
        {
            return;
        }

        RequireQuery(StorageTime.InStorableRange(query.Before.StartedAt), "before must be within the storable range");
        RequireQuery(query.Before.Id > 0, "before id must be positive");
    }

    /// <summary>
    /// Validates the number of an incident or of the incident whose notes are read.
    /// </summary>
    /// <param name="id">The number</param>
    /// <exception cref="InvalidQueryException">The number is not positive</exception>
    internal static void ValidateId(long id)
    {
        RequireQuery(id > 0, "incident ID must be positive");
    }

    /// <summary>
    /// Builds the statement that reads a page of the incident list.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildList(IncidentListQuery query)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$limit"] = (long)query.Limit
                         };
        var filter = string.Empty;

        if (query.Before is not null)
        {
            filter = " WHERE i.started_at < $before OR (i.started_at = $before AND i.id < $beforeId)";
            parameters["$before"] = StorageTime.ToNanoseconds(query.Before.StartedAt);
            parameters["$beforeId"] = query.Before.Id;
        }

        var sql = $"SELECT i.id, i.started_at, i.ended_at, i.first_kill_at, i.kill_count, i.created_at, i.updated_at, i.data, EXISTS (SELECT 1 FROM incident_notes n WHERE n.incident_id = i.id) FROM incidents i{filter} ORDER BY i.started_at DESC, i.id DESC LIMIT $limit";

        return (sql, parameters);
    }

    /// <summary>
    /// Reads a row of the incident list.
    /// </summary>
    /// <param name="reader">The reader, positioned on a row of <see cref="BuildList"/></param>
    /// <returns>The summary</returns>
    /// <exception cref="StoreException">The data is not an incident</exception>
    internal static IncidentSummary ReadSummary(SqliteDataReader reader)
    {
        IncidentSummaryData? data;

        try
        {
            data = JsonSerializer.Deserialize<IncidentSummaryData>(reader.GetFieldValue<byte[]>(7), IncidentJson.Options);
        }
        catch (JsonException exception)
        {
            throw new StoreException(DecodingMessage, exception);
        }

        if (data is null)
        {
            throw new StoreException(DecodingMessage);
        }

        return new IncidentSummary
               {
                   Id = reader.GetInt64(0),
                   Start = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                   End = StorageTime.FromNanoseconds(reader.GetInt64(2)),
                   FirstKillAt = StorageTime.FromNanoseconds(reader.GetInt64(3)),
                   LastKillAt = data.LastKillAt,
                   KillCount = reader.GetInt64(4),
                   FirstVictim = data.Kills.Count > 0 ? data.Kills[0].VictimCommand : string.Empty,
                   Hypothesis = data.Hypothesis,
                   HasNotes = reader.GetInt64(8) != 0,
                   CreatedAt = StorageTime.FromNanoseconds(reader.GetInt64(5)),
                   UpdatedAt = StorageTime.FromNanoseconds(reader.GetInt64(6))
               };
    }

    /// <summary>
    /// Builds the statement that reads one incident.
    /// </summary>
    /// <param name="id">The number</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildOne(long id)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$id"] = id
                         };

        return (Columns + " WHERE id = $id", parameters);
    }

    /// <summary>
    /// Throws an error unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">What must hold</param>
    private static void Require(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidIncidentException(Prefix + rule);
    }

    /// <summary>
    /// Throws a query error unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">What must hold</param>
    private static void RequireQuery(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidQueryException(QueryPrefix + rule);
    }

    /// <summary>
    /// Creates a command within the transaction.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="sql">The statement</param>
    /// <returns>The command</returns>
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }

    /// <summary>
    /// Binds the columns shared by insert and update.
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="start">The start in nanoseconds</param>
    /// <param name="end">The end in nanoseconds</param>
    /// <param name="firstKill">The first kill in nanoseconds</param>
    /// <param name="killCount">The kill count</param>
    /// <param name="data">The serialized incident</param>
    private static void Bind(SqliteCommand command, long start, long end, long firstKill, long killCount, string data)
    {
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        command.Parameters.AddWithValue("$first", firstKill);
        command.Parameters.AddWithValue("$count", killCount);
        command.Parameters.AddWithValue("$data", data);
    }

    /// <summary>
    /// Finds the stored incidents whose window overlaps the new one, oldest number first, and tells whether the oldest equals
    /// the new one in every saved column. The database compares the data, so a stored document is not read.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="window">Start, end and first kill in nanoseconds</param>
    /// <param name="killCount">The kill count</param>
    /// <param name="data">The serialized incident</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The IDs and whether the oldest equals the new incident</returns>
    private static async Task<(List<long> Ids, bool Same)> FindOverlappingAsync(SqliteConnection connection, SqliteTransaction transaction, (long Start, long End, long FirstKill) window, long killCount, string data, CancellationToken cancellationToken)
    {
        using var select = Command(connection, transaction, "SELECT id, (started_at = $start AND ended_at = $end AND first_kill_at = $first AND kill_count = $count AND data = $data) FROM incidents WHERE started_at < $end AND ended_at > $start ORDER BY id");

        Bind(select, window.Start, window.End, window.FirstKill, killCount, data);

        using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<long>();
        var same = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ids.Count == 0)
            {
                same = reader.GetInt64(1) != 0;
            }

            ids.Add(reader.GetInt64(0));
        }

        return (ids, same);
    }

    #endregion // Methods
}