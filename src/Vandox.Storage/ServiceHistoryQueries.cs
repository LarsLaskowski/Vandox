using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The service availability read of the services view. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class ServiceHistoryQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of the read.
    /// </summary>
    internal const string Context = "store: reading services";

    /// <summary>
    /// Streams the service records whose state holds into the range or lies in it, in order of capture time, from the covering index: ID, capture time, unit and active state. The window excludes its start,
    /// so each record streamed lies in the range or carries its state into it.
    /// </summary>
    internal const string StateStream = "SELECT id, captured_at, CAST(json_extract(data, '$.unit') AS TEXT), CAST(json_extract(data, '$.active_state') AS TEXT) FROM records INDEXED BY records_service_state "
                                        + "WHERE kind = 'service_state' AND captured_at > $from AND captured_at < $to ORDER BY captured_at, id";

    /// <summary>
    /// Counts the distinct units of the window of <see cref="StateStream"/>.
    /// </summary>
    internal const string DistinctUnits = "SELECT count(DISTINCT CAST(json_extract(data, '$.unit') AS TEXT)) FROM records INDEXED BY records_service_state WHERE kind = 'service_state' AND captured_at > $from AND captured_at < $to";

    /// <summary>
    /// Reads the sub state of the service records of a list of IDs.
    /// </summary>
    internal const string SubStates = "SELECT id, COALESCE(CAST(json_extract(data, '$.sub_state') AS TEXT), '') FROM records WHERE kind = 'service_state' AND id IN (SELECT value FROM json_each($ids))";

    private const string SystemdCollector = "systemd";

    private const string FromParameter = "$from";

    private const string ToParameter = "$to";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(ServiceAvailabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.From < query.To, "from must be before to");
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
    }

    /// <summary>
    /// Reads the availability of the units of a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The checked query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static async Task<ServiceAvailabilityResult> ReadAsync(SqliteConnection connection, ServiceAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var (gaps, named) = await CollectorGapReader.ReadAsync(connection, SystemdCollector, query.From, query.To, cancellationToken).ConfigureAwait(false);

        var folder = new ServiceAvailabilityFolder(query.From, query.To, new UnknownSpans(gaps.Spans, gaps.CompleteUntil, query.To));
        var lead = StorageTime.ToNanoseconds(SeriesTime.Add(query.From, -ServiceAvailabilityFolder.Hold));
        var to = StorageTime.ToNanoseconds(query.To);

        await StreamAsync(connection, folder, lead, to, cancellationToken).ConfigureAwait(false);

        var rows = folder.Finish();
        var count = folder.Dropped ? await CountUnitsAsync(connection, lead, to, cancellationToken).ConfigureAwait(false) : rows.Count;
        var subStates = await ReadSubStatesAsync(connection, rows.Select(row => row.LastId).ToList(), cancellationToken).ConfigureAwait(false);

        return new ServiceAvailabilityResult
               {
                   Units = rows.Select(row => new ServiceAvailability
                                              {
                                                  Unit = row.Row.Unit,
                                                  ActiveState = row.Row.ActiveState,
                                                  SubState = subStates.GetValueOrDefault(row.LastId, string.Empty),
                                                  Available = row.Row.Available,
                                                  NotAvailable = row.Row.NotAvailable,
                                                  Unknown = row.Row.Unknown,
                                                  StateAt = row.Row.StateAt
                                              }).ToList(),
                   UnitCount = count,
                   GapsCompleteUntil = gaps.CompleteUntil,
                   SystemdGaps = named
               };
    }

    /// <summary>
    /// Streams the service records of the window into the folder.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="folder">The folder</param>
    /// <param name="from">The start of the window, excluded, in nanoseconds</param>
    /// <param name="to">The end of the window, excluded, in nanoseconds</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the records are read</returns>
    private static async Task StreamAsync(SqliteConnection connection, ServiceAvailabilityFolder folder, long from, long to, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = StateStream;
        command.Parameters.AddWithValue(FromParameter, from);
        command.Parameters.AddWithValue(ToParameter, to);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2))
            {
                continue;
            }

            folder.Add(reader.GetInt64(0), StorageTime.FromNanoseconds(reader.GetInt64(1)), reader.GetString(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        }
    }

    /// <summary>
    /// Counts the distinct units of the window.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="from">The start of the window, excluded, in nanoseconds</param>
    /// <param name="to">The end of the window, excluded, in nanoseconds</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the number of units</returns>
    private static async Task<int> CountUnitsAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var counts = await ReaderQuery.ReadAsync(connection,
                                                 DistinctUnits,
                                                 new Dictionary<string, object>
                                                 {
                                                     [FromParameter] = from,
                                                     [ToParameter] = to
                                                 },
                                                 reader => reader.GetInt32(0),
                                                 Context,
                                                 cancellationToken).ConfigureAwait(false);

        return counts[0];
    }

    /// <summary>
    /// Reads the sub states of the latest records of the units.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="ids">The record IDs, at most <see cref="ServiceAvailabilityFolder.MaxUnits"/></param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sub state of each record by its ID</returns>
    private static async Task<Dictionary<long, string>> ReadSubStatesAsync(SqliteConnection connection, List<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await ReaderQuery.ReadAsync(connection,
                                               SubStates,
                                               new Dictionary<string, object>
                                               {
                                                   ["$ids"] = JsonSerializer.Serialize(ids)
                                               },
                                               reader => (Id: reader.GetInt64(0), State: reader.GetString(1)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.Id, row => row.State);
    }

    #endregion // Methods
}