using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds process snapshots, stores undecodable ones and reads the per-program rollup tables for the tests of the program fold and the removal of its inputs.
/// </summary>
internal static class ProgramSamples
{
    #region Constants

    /// <summary>
    /// The source of the process snapshots of most tests.
    /// </summary>
    internal const string Source = "processes";

    /// <summary>
    /// The query of the program rows of the 5-minute tier.
    /// </summary>
    internal const string ProgramRows5m = "SELECT source, bucket, program, seen, rss_min, rss_max, rss_sum, pss_min, pss_max, pss_sum, cpu_min, cpu_max, cpu_sum FROM program_rollups_5m ORDER BY source, bucket, program";

    /// <summary>
    /// The query of the program rows of the hourly tier.
    /// </summary>
    internal const string ProgramRows1h = "SELECT source, bucket, program, seen, rss_min, rss_max, rss_sum, pss_min, pss_max, pss_sum, cpu_min, cpu_max, cpu_sum FROM program_rollups_1h ORDER BY source, bucket, program";

    /// <summary>
    /// The query of the totals of the 5-minute tier.
    /// </summary>
    internal const string Totals5m = "SELECT source, bucket, snapshots, pss_snapshots, incomplete, named, rss_sum, pss_sum, cpu_sum FROM program_rollup_totals_5m ORDER BY source, bucket";

    /// <summary>
    /// The query of the totals of the hourly tier.
    /// </summary>
    internal const string Totals1h = "SELECT source, bucket, snapshots, pss_snapshots, incomplete, named, rss_sum, pss_sum, cpu_sum FROM program_rollup_totals_1h ORDER BY source, bucket";

    /// <summary>
    /// The query of the program watermark.
    /// </summary>
    internal const string Watermark = "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'program_rollup_after_id'";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a program with the same memory in both keys left to the caller.
    /// </summary>
    /// <param name="name">The program name</param>
    /// <param name="count">The number of processes</param>
    /// <param name="rss">The resident memory in bytes</param>
    /// <param name="cpu">The CPU percentage</param>
    /// <param name="pss">The proportional memory in bytes, <c>null</c> when the program has none</param>
    /// <returns>The program</returns>
    internal static ProgramAggregate Program(string name, uint count, ulong rss, double cpu, ulong? pss = null)
    {
        return new ProgramAggregate
               {
                   Program = name,
                   Count = count,
                   RssBytes = rss,
                   CpuPercent = cpu,
                   PssBytes = pss
               };
    }

    /// <summary>
    /// Creates a process snapshot of the backend that lists every program: its total is the sum of the counts.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="source">The source</param>
    /// <param name="programs">The programs</param>
    /// <returns>The record</returns>
    internal static DataRecord Snapshot(DateTimeOffset at, string source, params ProgramAggregate[] programs)
    {
        return Snapshot(RecordOrigin.Backend, at, source, true, (uint)programs.Sum(program => program.Count), programs);
    }

    /// <summary>
    /// Creates a process snapshot.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="at">The capture time</param>
    /// <param name="source">The source</param>
    /// <param name="complete">Whether the snapshot is marked complete</param>
    /// <param name="total">The total, <c>null</c> when absent</param>
    /// <param name="programs">The programs, <c>null</c> when the snapshot lists none</param>
    /// <returns>The record</returns>
    internal static DataRecord Snapshot(string origin, DateTimeOffset at, string source, bool complete, uint? total, IReadOnlyList<ProgramAggregate>? programs)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = source,
                   CapturedAt = at,
                   Data = new ProcessSnapshot
                          {
                              Complete = complete,
                              Total = total,
                              Processes = [
                                              new ProcessSample
                                              {
                                                  Pid = 1,
                                                  Command = "init",
                                                  RssBytes = 1
                                              }
                                          ],
                              Programs = programs?.ToList()
                          }
               };
    }

    /// <summary>
    /// Inserts a process snapshot row whose data is the given text, without validation.
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
    /// Inserts a process snapshot row whose data is the given bytes stored as text, without validation.
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
        command.CommandText = "INSERT INTO records(kind, origin, source, captured_at, received_at, data) VALUES ('process_snapshot', 'backend', $source, $at, $at, CAST($data AS TEXT)) RETURNING id";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$at", StorageTime.ToNanoseconds(at));
        command.Parameters.AddWithValue("$data", data is null ? DBNull.Value : data);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the JSON of a process snapshot whose program names carry the given bytes between the quotes of a name.
    /// </summary>
    /// <param name="name">The bytes of the name</param>
    /// <returns>The JSON bytes</returns>
    internal static byte[] JsonWithNameBytes(params byte[] name)
    {
        return [.. Encoding.UTF8.GetBytes("{\"complete\":true,\"programs\":[{\"program\":\"a"), .. name, .. Encoding.UTF8.GetBytes("\",\"count\":1,\"cpu_percent\":1,\"rss_bytes\":1}]}")];
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar, a NULL as <c>null</c>.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that returns the rows as text</returns>
    internal static async Task<List<string>> RowsAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal passed by the calling test
        command.CommandText = sql;
#pragma warning restore CA2100

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new string[reader.FieldCount];

            for (var index = 0; index < values.Length; index++)
            {
                values[index] = reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    /// <summary>
    /// Describes a program row for comparison with <see cref="RowsAsync"/>.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="bucket">The bucket start</param>
    /// <param name="program">The program</param>
    /// <param name="values">The values after the program: seen, RSS minimum, maximum and sum, PSS minimum, maximum and sum, CPU minimum, maximum and sum</param>
    /// <returns>The text</returns>
    internal static string ProgramRow(string source, DateTimeOffset bucket, string program, string values)
    {
        return $"{source}|{RetentionSamples.Ns(bucket)}|{program}|{values}";
    }

    /// <summary>
    /// Describes a totals row for comparison with <see cref="RowsAsync"/>.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="bucket">The bucket start</param>
    /// <param name="values">The values after the bucket: snapshots, PSS snapshots, incomplete, named, RSS sum, PSS sum and CPU sum</param>
    /// <returns>The text</returns>
    internal static string TotalsRow(string source, DateTimeOffset bucket, string values)
    {
        return $"{source}|{RetentionSamples.Ns(bucket)}|{values}";
    }

    /// <summary>
    /// Gets the start of the 5-minute bucket of an instant.
    /// </summary>
    /// <param name="at">The instant</param>
    /// <returns>The start</returns>
    internal static DateTimeOffset FiveMinutes(DateTimeOffset at)
    {
        return at.AddTicks(-(at.UtcTicks % TimeSpan.FromMinutes(5).Ticks));
    }

    /// <summary>
    /// Gets the start of the hourly bucket of an instant.
    /// </summary>
    /// <param name="at">The instant</param>
    /// <returns>The start</returns>
    internal static DateTimeOffset Hour(DateTimeOffset at)
    {
        return at.AddTicks(-(at.UtcTicks % TimeSpan.FromHours(1).Ticks));
    }

    #endregion // Methods
}