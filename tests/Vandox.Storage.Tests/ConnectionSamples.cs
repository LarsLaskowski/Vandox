using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds connection snapshots, stores undecodable ones and reads the connection rollup tables for the tests of the connection fold and the removal of its inputs.
/// </summary>
internal static class ConnectionSamples
{
    #region Constants

    /// <summary>
    /// The source of the connection snapshots of most tests.
    /// </summary>
    internal const string Source = "connections";

    /// <summary>
    /// The table suffix of the 5-minute tier.
    /// </summary>
    internal const string FiveMinutesTier = "5m";

    /// <summary>
    /// The table suffix of the hourly tier.
    /// </summary>
    internal const string HourlyTier = "1h";

    /// <summary>
    /// The query of the rows of the 5-minute tier: source, bucket, kind, name, seen, minimum, maximum and sum.
    /// </summary>
    internal const string Rows5m = "SELECT source, bucket, kind, name, seen, min_value, max_value, sum_value FROM connection_rollups_5m ORDER BY source, bucket, kind, name";

    /// <summary>
    /// The query of the rows of the hourly tier: source, bucket, kind, name, seen, minimum, maximum and sum.
    /// </summary>
    internal const string Rows1h = "SELECT source, bucket, kind, name, seen, min_value, max_value, sum_value FROM connection_rollups_1h ORDER BY source, bucket, kind, name";

    /// <summary>
    /// The query of the totals of the 5-minute tier: source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed and process sum.
    /// </summary>
    internal const string Totals5m = "SELECT source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed, process_sum FROM connection_rollup_totals_5m ORDER BY source, bucket";

    /// <summary>
    /// The query of the totals of the hourly tier: source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed and process sum.
    /// </summary>
    internal const string Totals1h = "SELECT source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed, process_sum FROM connection_rollup_totals_1h ORDER BY source, bucket";

    /// <summary>
    /// The query of the connection watermark.
    /// </summary>
    internal const string Watermark = "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'connection_rollup_after_id'";

    /// <summary>
    /// The query of the number of every row of the connection rollup tables.
    /// </summary>
    internal const string AllRowCount = "SELECT (SELECT count(*) FROM connection_rollups_5m) + (SELECT count(*) FROM connection_rollups_1h) + (SELECT count(*) FROM connection_rollup_totals_5m) + (SELECT count(*) FROM connection_rollup_totals_1h)";

    /// <summary>
    /// The kind of a state row.
    /// </summary>
    internal const int StateKind = 0;

    /// <summary>
    /// The kind of a program row.
    /// </summary>
    internal const int ProgramKind = 1;

    /// <summary>
    /// The kind of an address row.
    /// </summary>
    internal const int AddressKind = 2;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a TCP state entry.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="state">The state, empty for a UDP entry without one</param>
    /// <param name="count">The number of sockets</param>
    /// <returns>The entry</returns>
    internal static StateCount State(string proto, string state, uint count)
    {
        return new StateCount
               {
                   Proto = proto,
                   State = state,
                   Count = count
               };
    }

    /// <summary>
    /// Creates an entry of the state ESTABLISHED of the protocol <c>tcp</c>.
    /// </summary>
    /// <param name="count">The number of sockets</param>
    /// <returns>The entry</returns>
    internal static StateCount Established(uint count)
    {
        return State(Proto.Tcp, "ESTABLISHED", count);
    }

    /// <summary>
    /// Creates a process entry.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <param name="command">The command</param>
    /// <param name="count">The number of sockets the process holds</param>
    /// <returns>The entry</returns>
    internal static ProcessConnections Process(int pid, string command, uint count)
    {
        return new ProcessConnections
               {
                   Pid = pid,
                   Command = command,
                   Count = count
               };
    }

    /// <summary>
    /// Creates a remote address entry.
    /// </summary>
    /// <param name="address">The address in text form</param>
    /// <param name="count">The number of sockets to the address</param>
    /// <returns>The entry</returns>
    internal static RemoteCount Remote(string address, uint count)
    {
        return new RemoteCount
               {
                   Addr = IPAddress.Parse(address),
                   Count = count
               };
    }

    /// <summary>
    /// Creates remote address entries <c>10.0.{third}.{k}</c> for k from <paramref name="first"/> to <paramref name="last"/>, each with the same count.
    /// </summary>
    /// <param name="third">The third octet</param>
    /// <param name="first">The first last octet</param>
    /// <param name="last">The last last octet</param>
    /// <param name="count">The number of sockets to each address</param>
    /// <returns>The entries</returns>
    internal static RemoteCount[] Remotes(int third, int first, int last, uint count)
    {
        return [.. Enumerable.Range(first, last - first + 1).Select(octet => Remote(Address(third, octet), count))];
    }

    /// <summary>
    /// Creates remote address entries <c>10.0.{third}.{k}</c> for k from 1 to <paramref name="last"/>, each with the count k.
    /// </summary>
    /// <param name="third">The third octet</param>
    /// <param name="last">The last last octet</param>
    /// <returns>The entries</returns>
    internal static RemoteCount[] Ranked(int third, int last)
    {
        return [.. Enumerable.Range(1, last).Select(octet => Remote(Address(third, octet), (uint)octet))];
    }

    /// <summary>
    /// Creates the three snapshots of the trim tests: each lists 25 addresses of its own, address k with the count k, so that every sum 1 to 25 is held by three addresses (75 addresses).
    /// </summary>
    /// <param name="start">The capture time of the first snapshot; the others follow one second apart</param>
    /// <returns>The records</returns>
    internal static DataRecord[] RankedSnapshots(DateTimeOffset start)
    {
        return [.. Enumerable.Range(0, 3).Select(third => Snapshot(start.AddSeconds(third), Source, remotes: Ranked(third, 25)))];
    }

    /// <summary>
    /// Gets the text of the address <c>10.0.{third}.{octet}</c>.
    /// </summary>
    /// <param name="third">The third octet</param>
    /// <param name="octet">The last octet</param>
    /// <returns>The text</returns>
    internal static string Address(int third, int octet)
    {
        return string.Create(CultureInfo.InvariantCulture, $"10.0.{third}.{octet}");
    }

    /// <summary>
    /// Creates a connection snapshot record that lists the given aggregates.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="source">The source</param>
    /// <param name="states">The states, <c>null</c> when the snapshot lists none</param>
    /// <param name="processes">The processes, <c>null</c> when the snapshot lists none</param>
    /// <param name="remotes">The remotes, <c>null</c> when the snapshot lists none</param>
    /// <param name="incomplete">Whether the snapshot is marked <c>aggregates_incomplete</c></param>
    /// <param name="origin">The origin</param>
    /// <returns>The record</returns>
    internal static DataRecord Snapshot(DateTimeOffset at, string source, StateCount[]? states = null, ProcessConnections[]? processes = null, RemoteCount[]? remotes = null, bool incomplete = false, string origin = RecordOrigin.Backend)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = source,
                   CapturedAt = at,
                   Data = new ConnectionSnapshot
                          {
                              Complete = incomplete == false,
                              AggregatesIncomplete = incomplete,
                              States = states?.ToList(),
                              Processes = processes?.ToList(),
                              Remotes = remotes?.ToList()
                          }
               };
    }

    /// <summary>
    /// Creates an imported connection snapshot as an import writes it when an <c>lsof</c> line was left out: not complete, yet without <c>aggregates_incomplete</c>.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="source">The source</param>
    /// <param name="states">The states</param>
    /// <returns>The record</returns>
    internal static DataRecord Imported(DateTimeOffset at, string source, params StateCount[] states)
    {
        var record = Snapshot(at, source, states, origin: RecordOrigin.Import);

        ((ConnectionSnapshot)record.Data!).Complete = false;

        return record;
    }

    /// <summary>
    /// Inserts a connection snapshot row whose data is the given text, without validation.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="data">The data, <c>null</c> for a NULL column</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that returns the ID of the row</returns>
    internal static Task<long> InsertRawAsync(string directory, string source, DateTimeOffset at, string? data, CancellationToken cancellationToken)
    {
        return InsertBytesAsync(directory, source, at, data is null ? null : Encoding.UTF8.GetBytes(data), cancellationToken);
    }

    /// <summary>
    /// Inserts a connection snapshot row whose data is the given bytes stored as text, without validation.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="data">The bytes, <c>null</c> for a NULL column</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that returns the ID of the row</returns>
    internal static async Task<long> InsertBytesAsync(string directory, string source, DateTimeOffset at, byte[]? data, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO records(kind, origin, source, captured_at, received_at, data) VALUES ('connection_snapshot', 'backend', $source, $at, $at, CAST($data AS TEXT)) RETURNING id";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$at", StorageTime.ToNanoseconds(at));
        command.Parameters.AddWithValue("$data", data is null ? DBNull.Value : data);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the JSON of a connection snapshot whose one process has a command that carries the given bytes between the quotes.
    /// </summary>
    /// <param name="command">The bytes of the command</param>
    /// <returns>The JSON bytes</returns>
    internal static byte[] JsonWithCommandBytes(params byte[] command)
    {
        return [.. Encoding.UTF8.GetBytes("{\"complete\":true,\"processes\":[{\"pid\":1,\"command\":\"a"), .. command, .. Encoding.UTF8.GetBytes("\",\"count\":1}]}")];
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar, a NULL as <c>null</c>.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that returns the rows as text</returns>
    internal static Task<List<string>> RowsAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        return ProgramSamples.RowsAsync(directory, sql, cancellationToken);
    }

    /// <summary>
    /// Gets the query of the rows of a tier that match a condition, as kind, name, seen, minimum, maximum and sum, in the order of the key.
    /// </summary>
    /// <param name="tier">The table suffix, <see cref="FiveMinutesTier"/> or <see cref="HourlyTier"/></param>
    /// <param name="filter">The condition</param>
    /// <returns>The query</returns>
    internal static string Items(string tier, string filter)
    {
        return $"SELECT kind, name, seen, min_value, max_value, sum_value FROM connection_rollups_{tier} WHERE {filter} ORDER BY bucket, kind, name";
    }

    /// <summary>
    /// Gets the query of the totals of a tier as snapshots, incomplete, capped, programs, addresses, trimmed and process sum.
    /// </summary>
    /// <param name="tier">The table suffix, <see cref="FiveMinutesTier"/> or <see cref="HourlyTier"/></param>
    /// <returns>The query</returns>
    internal static string TotalsOf(string tier)
    {
        return $"SELECT snapshots, incomplete, capped, programs, addresses, trimmed, process_sum FROM connection_rollup_totals_{tier} ORDER BY source, bucket";
    }

    /// <summary>
    /// Describes a row for comparison with <see cref="Rows5m"/> or <see cref="Rows1h"/>.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="bucket">The bucket start</param>
    /// <param name="kind">The kind</param>
    /// <param name="name">The name</param>
    /// <param name="values">The values after the name: seen, minimum, maximum and sum</param>
    /// <returns>The text</returns>
    internal static string Row(string source, DateTimeOffset bucket, int kind, string name, string values)
    {
        return $"{source}|{RetentionSamples.Ns(bucket)}|{kind}|{name}|{values}";
    }

    /// <summary>
    /// Describes a totals row for comparison with <see cref="Totals5m"/> or <see cref="Totals1h"/>.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="bucket">The bucket start</param>
    /// <param name="values">The values after the bucket: snapshots, incomplete, capped, programs, addresses, trimmed and process sum</param>
    /// <returns>The text</returns>
    internal static string TotalsRow(string source, DateTimeOffset bucket, string values)
    {
        return $"{source}|{RetentionSamples.Ns(bucket)}|{values}";
    }

    #endregion // Methods
}