using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The MariaDB status and thread snapshot reads of the services view. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class MariaDbHistoryQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of the reads.
    /// </summary>
    internal const string Context = "store: reading mariadb status";

    /// <summary>
    /// The most steps of a status read.
    /// </summary>
    internal const int MaxSteps = 2001;

    /// <summary>
    /// Streams the status records of the source <c>mariadb</c> in a range, in order of capture time, from the covering index: capture time, availability, memory in use and connections.
    /// </summary>
    internal const string StatusStream = "SELECT captured_at, json_extract(data, '$.availability'), json_extract(data, '$.status.Memory_used'), json_extract(data, '$.status.Threads_connected') FROM records INDEXED BY records_mariadb_status "
                                         + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at >= $from AND captured_at < $to ORDER BY captured_at, id";

    /// <summary>
    /// Reads the capture time of the oldest status record of the source <c>mariadb</c> from the covering index.
    /// </summary>
    internal const string StatusOldest = "SELECT min(captured_at) FROM records INDEXED BY records_mariadb_status WHERE kind = 'mariadb_status' AND source = 'mariadb'";

    /// <summary>
    /// Lists the status records with threads in a range, newest first, from the covering index: ID, capture time and number of threads.
    /// </summary>
    internal const string SnapshotStamps = "SELECT id, captured_at, json_array_length(data, '$.threads') FROM records INDEXED BY records_mariadb_status "
                                           + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at >= $from AND captured_at < $to AND json_array_length(data, '$.threads') > 0 ORDER BY captured_at DESC, id DESC LIMIT $limit";

    /// <summary>
    /// Reads the latest status record with threads at or before a moment and not before the start of the window: ID, capture time and number of threads. At one capture time the lowest ID comes first.
    /// </summary>
    internal const string SnapshotAtOrBefore = "SELECT id, captured_at, json_array_length(data, '$.threads') FROM records INDEXED BY records_mariadb_status "
                                               + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at <= $at AND captured_at >= $from AND json_array_length(data, '$.threads') > 0 ORDER BY captured_at DESC, id LIMIT 1";

    /// <summary>
    /// Reads the earliest status record with threads after a moment and not after the end of the window: ID, capture time and number of threads. At one capture time the lowest ID comes first.
    /// </summary>
    internal const string SnapshotAfter = "SELECT id, captured_at, json_array_length(data, '$.threads') FROM records INDEXED BY records_mariadb_status "
                                          + "WHERE kind = 'mariadb_status' AND source = 'mariadb' AND captured_at > $at AND captured_at <= $to AND json_array_length(data, '$.threads') > 0 ORDER BY captured_at, id LIMIT 1";

    /// <summary>
    /// Reads, for a list of record IDs, the threads not sleeping and the longest of their seconds, at most <c>$maxSeconds</c>. A thread without a command or without seconds counts as running, or as zero seconds.
    /// </summary>
    internal const string SnapshotCounts = "SELECT r.id, "
                                           + "(SELECT count(*) FROM json_each(r.data, '$.threads') WHERE COALESCE(json_extract(value, '$.command'), '') <> 'Sleep'), "
                                           + "(SELECT MIN(max(COALESCE(CAST(json_extract(value, '$.time_seconds') AS INTEGER), 0)), $maxSeconds) FROM json_each(r.data, '$.threads') WHERE COALESCE(json_extract(value, '$.command'), '') <> 'Sleep') "
                                           + "FROM records r WHERE r.kind = 'mariadb_status' AND r.source = 'mariadb' AND r.id IN (SELECT value FROM json_each($ids))";

    /// <summary>
    /// Reads the capture time, the number of threads and the number of threads not sleeping of one status record with threads.
    /// </summary>
    internal const string SnapshotHeader = "SELECT r.captured_at, json_array_length(r.data, '$.threads'), "
                                           + "(SELECT count(*) FROM json_each(r.data, '$.threads') WHERE COALESCE(json_extract(value, '$.command'), '') <> 'Sleep') "
                                           + "FROM records r WHERE r.id = $id AND r.kind = 'mariadb_status' AND r.source = 'mariadb' AND json_array_length(r.data, '$.threads') > 0";

    /// <summary>
    /// Reads the threads not sleeping of one status record in the order of the record, each as the JSON text of its object, at most <c>$limit</c>.
    /// </summary>
    internal const string ThreadRows = "SELECT je.value FROM records r, json_each(r.data, '$.threads') je WHERE r.id = $id AND r.kind = 'mariadb_status' AND r.source = 'mariadb' "
                                       + "AND COALESCE(json_extract(je.value, '$.command'), '') <> 'Sleep' LIMIT $limit";

    private const string MariaDbCollector = "mariadb";

    private const string FromParameter = "$from";

    private const string ToParameter = "$to";

    private const string AtParameter = "$at";

    private const string IdParameter = "$id";

    private const string LimitParameter = "$limit";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a status query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateStatus(MariaDbStatusQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.From < query.To, "from must be before to");
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
        RecordQueries.Require(query.Step > TimeSpan.Zero, "step must be positive");
        RecordQueries.Require(SeriesTime.Overlapping(query.From.UtcTicks - SeriesTime.EpochTicks, query.To.UtcTicks - SeriesTime.EpochTicks, query.Step.Ticks) <= MaxSteps, $"at most {MaxSteps} steps");
    }

    /// <summary>
    /// Checks a snapshot list query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateSnapshots(ThreadSnapshotQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.From < query.To, "from must be before to");
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
        RecordQueries.Require(query.Limit >= 1 && query.Limit <= ThreadSnapshotQuery.MaxLimit, $"limit must be 1 to {ThreadSnapshotQuery.MaxLimit}");
    }

    /// <summary>
    /// Checks a closest snapshot query.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <param name="window">The window on either side of the moment</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateClosest(DateTimeOffset at, TimeSpan window)
    {
        RecordQueries.Require(window > TimeSpan.Zero && window <= TimeSpan.FromDays(1), "window must be positive and at most one day");
        RecordQueries.Require(StorageTime.InStorableRange(at), "at must be within the storable range");
    }

    /// <summary>
    /// Folds the status records of a range onto the steps.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The checked query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static async Task<MariaDbStatusResult> ReadStatusAsync(SqliteConnection connection, MariaDbStatusQuery query, CancellationToken cancellationToken)
    {
        var (gaps, _) = await CollectorGapReader.ReadAsync(connection, MariaDbCollector, query.From, query.To, cancellationToken).ConfigureAwait(false);

        var folder = new MariaDbStatusFolder(query, new UnknownSpans(gaps.Spans, gaps.CompleteUntil, query.To));

        using (var command = connection.CreateCommand())
        {
            command.CommandText = StatusStream;
            command.Parameters.AddWithValue(FromParameter, StorageTime.ToNanoseconds(SeriesTime.Add(query.From, -MariaDbStatusFolder.Hold)));
            command.Parameters.AddWithValue(ToParameter, StorageTime.ToNanoseconds(query.To));

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var readerDisposal = reader.ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                folder.Add(StorageTime.FromNanoseconds(reader.GetInt64(0)),
                           reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                           reader.IsDBNull(2) ? null : reader.GetDouble(2),
                           reader.IsDBNull(3) ? null : reader.GetDouble(3));
            }
        }

        var oldest = await ReaderQuery.ReadAsync(connection,
                                                 StatusOldest,
                                                 new Dictionary<string, object>(),
                                                 reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                                                 Context,
                                                 cancellationToken).ConfigureAwait(false);

        return folder.Finish(oldest[0] is { } nanoseconds ? StorageTime.FromNanoseconds(nanoseconds) : null);
    }

    /// <summary>
    /// Lists the thread snapshots of a range, newest first.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The checked query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamps</returns>
    internal static async Task<IReadOnlyList<ThreadSnapshotStamp>> ReadStampsAsync(SqliteConnection connection, ThreadSnapshotQuery query, CancellationToken cancellationToken)
    {
        var found = await ReaderQuery.ReadAsync(connection,
                                                SnapshotStamps,
                                                new Dictionary<string, object>
                                                {
                                                    [FromParameter] = StorageTime.ToNanoseconds(query.From),
                                                    [ToParameter] = StorageTime.ToNanoseconds(query.To),
                                                    [LimitParameter] = query.Limit
                                                },
                                                ReadFound,
                                                Context,
                                                cancellationToken).ConfigureAwait(false);

        return await CompleteAsync(connection, found, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the snapshot closest to a moment: the latest at or before it and the earliest after it, each within the window, the nearer, the earlier on a tie.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="at">The moment</param>
    /// <param name="window">The window on either side of the moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamp, or <see langword="null"/> when none lies within the window</returns>
    internal static async Task<ThreadSnapshotStamp?> ReadClosestAsync(SqliteConnection connection, DateTimeOffset at, TimeSpan window, CancellationToken cancellationToken)
    {
        var moment = StorageTime.ToNanoseconds(at);
        var before = await ReaderQuery.ReadAsync(connection,
                                                 SnapshotAtOrBefore,
                                                 new Dictionary<string, object>
                                                 {
                                                     [AtParameter] = moment,
                                                     [FromParameter] = StorageTime.ToNanoseconds(SeriesTime.Add(at, -window))
                                                 },
                                                 ReadFound,
                                                 Context,
                                                 cancellationToken).ConfigureAwait(false);
        var after = await ReaderQuery.ReadAsync(connection,
                                                SnapshotAfter,
                                                new Dictionary<string, object>
                                                {
                                                    [AtParameter] = moment,
                                                    [ToParameter] = StorageTime.ToNanoseconds(SeriesTime.Add(at, window))
                                                },
                                                ReadFound,
                                                Context,
                                                cancellationToken).ConfigureAwait(false);
        var chosen = Closer(before, after, at);

        if (chosen.Count == 0)
        {
            return null;
        }

        return (await CompleteAsync(connection, chosen, cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Reads one thread snapshot: its counts and the threads not sleeping in the order of the record, at most <see cref="ThreadSnapshot.MaxRows"/>.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="recordId">The ID of the status record</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the snapshot, or <see langword="null"/> when the record does not exist or holds no threads</returns>
    /// <exception cref="StoreException">A thread object does not parse</exception>
    internal static async Task<ThreadSnapshot?> ReadSnapshotAsync(SqliteConnection connection, long recordId, CancellationToken cancellationToken)
    {
        var headers = await ReaderQuery.ReadAsync(connection,
                                                  SnapshotHeader,
                                                  new Dictionary<string, object>
                                                  {
                                                      [IdParameter] = recordId
                                                  },
                                                  reader => (CapturedAt: StorageTime.FromNanoseconds(reader.GetInt64(0)), Threads: reader.GetInt32(1), Running: reader.GetInt32(2)),
                                                  Context,
                                                  cancellationToken).ConfigureAwait(false);

        if (headers.Count == 0)
        {
            return null;
        }

        var texts = await ReaderQuery.ReadAsync(connection,
                                                ThreadRows,
                                                new Dictionary<string, object>
                                                {
                                                    [IdParameter] = recordId,
                                                    [LimitParameter] = ThreadSnapshot.MaxRows
                                                },
                                                reader => reader.GetString(0),
                                                Context,
                                                cancellationToken).ConfigureAwait(false);
        var header = headers[0];

        return new ThreadSnapshot
               {
                   CapturedAt = header.CapturedAt,
                   Threads = header.Threads,
                   Running = header.Running,
                   Sleeping = header.Threads - header.Running,
                   Rows = texts.Select(ParseThread).ToList()
               };
    }

    /// <summary>
    /// Maps the first three columns of a snapshot row: ID, capture time and number of threads.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The row</returns>
    private static (long Id, DateTimeOffset CapturedAt, int Threads) ReadFound(SqliteDataReader reader)
    {
        return (reader.GetInt64(0), StorageTime.FromNanoseconds(reader.GetInt64(1)), reader.GetInt32(2));
    }

    /// <summary>
    /// Picks the nearer of the two sides; the earlier one when they are as near.
    /// </summary>
    /// <param name="before">The snapshot at or before the moment, if any</param>
    /// <param name="after">The snapshot after the moment, if any</param>
    /// <param name="at">The moment</param>
    /// <returns>The chosen snapshot, none when both sides are empty</returns>
    private static IReadOnlyList<(long Id, DateTimeOffset CapturedAt, int Threads)> Closer(IReadOnlyList<(long Id, DateTimeOffset CapturedAt, int Threads)> before, IReadOnlyList<(long Id, DateTimeOffset CapturedAt, int Threads)> after, DateTimeOffset at)
    {
        if (before.Count == 0)
        {
            return after;
        }

        if (after.Count == 0 || at - before[0].CapturedAt <= after[0].CapturedAt - at)
        {
            return before;
        }

        return after;
    }

    /// <summary>
    /// Completes the found snapshots with the number of threads not sleeping and the longest seconds of those.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="found">The snapshots found, in the order to return them</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamps in the order of <paramref name="found"/></returns>
    private static async Task<IReadOnlyList<ThreadSnapshotStamp>> CompleteAsync(SqliteConnection connection, IReadOnlyList<(long Id, DateTimeOffset CapturedAt, int Threads)> found, CancellationToken cancellationToken)
    {
        if (found.Count == 0)
        {
            return [];
        }

        var counts = await ReaderQuery.ReadAsync(connection,
                                                 SnapshotCounts,
                                                 new Dictionary<string, object>
                                                 {
                                                     ["$ids"] = JsonSerializer.Serialize(found.Select(item => item.Id).ToList()),
                                                     ["$maxSeconds"] = ThreadSnapshot.MaxSeconds
                                                 },
                                                 reader => (Id: reader.GetInt64(0), Running: reader.GetInt32(1), Longest: reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2)),
                                                 Context,
                                                 cancellationToken).ConfigureAwait(false);
        var byId = counts.ToDictionary(item => item.Id);

        return found.Where(item => byId.ContainsKey(item.Id))
                    .Select(item => new ThreadSnapshotStamp
                                    {
                                        RecordId = item.Id,
                                        CapturedAt = item.CapturedAt,
                                        Threads = item.Threads,
                                        Running = byId[item.Id].Running,
                                        LongestSeconds = byId[item.Id].Longest
                                    })
                    .ToList();
    }

    /// <summary>
    /// Reads a thread object as the data model's thread.
    /// </summary>
    /// <param name="text">The JSON text of the object</param>
    /// <returns>The thread</returns>
    /// <exception cref="StoreException">The object does not parse as a thread</exception>
    private static MariaDbThread ParseThread(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<MariaDbThread>(text, PayloadRegistry.Options) ?? throw new StoreException($"{Context}: a stored thread is empty");
        }
        catch (JsonException exception)
        {
            throw new StoreException($"{Context}: a stored thread does not parse", exception);
        }
    }

    #endregion // Methods
}