using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the rollups of connection snapshots (issue #486, <c>docs/BENCHMARKS.md</c>): the fold steps beside stored metrics and process snapshots, a worst-case
/// step, the disk growth of a day, the reads over one year and 1,095 days, the trim of an untrimmed hour and the duration of schema step 17.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ConnectionBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--connections";

    /// <summary>
    /// The source of the benchmark records.
    /// </summary>
    internal const string Source = "connections";

    /// <summary>
    /// The statement that writes hourly buckets of the trimmed size directly into the item table.
    /// </summary>
    internal const string RowsInsert = "INSERT INTO connection_rollups_1h (source, bucket, kind, name, seen, min_value, max_value, sum_value) "
                                       + "WITH RECURSIVE h(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM h WHERE n < $hours - 1), i(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM i WHERE n < 99) "
                                       + "SELECT 'connections', $first + h.n * 3600000000000, 2, printf('a%05d.%03d', h.n, i.n), 60, 0, 1 + (i.n % 5), 60 + i.n FROM h, i "
                                       + "UNION ALL SELECT 'connections', $first + h.n * 3600000000000, 0, printf('state-%02d', i.n), 60, 0, 10, 300 + i.n FROM h, i WHERE i.n < 13 "
                                       + "UNION ALL SELECT 'connections', $first + h.n * 3600000000000, 1, printf('program-%02d', i.n), 60, 0, 10, 300 + i.n FROM h, i WHERE i.n < 15";

    /// <summary>
    /// The statement that writes the totals of hourly buckets directly into the totals table.
    /// </summary>
    internal const string TotalsInsert = "INSERT INTO connection_rollup_totals_1h (source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed, process_sum) "
                                         + "WITH RECURSIVE h(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM h WHERE n < $hours - 1) "
                                         + "SELECT 'connections', $first + h.n * 3600000000000, 60, 0, 0, 15, 100, 0, 2400 FROM h";

    private const int Batches = 5;
    private const int MetricsPerBatch = 10000;
    private const int ProcessSnapshotsPerBatch = 500;
    private const int ConnectionSnapshotsPerBatch = 500;
    private const int ProgramsPerSnapshot = 80;
    private const int DaySnapshots = 1440;
    private const int YearSnapshots = 8760;
    private const int Reads = 5;
    private const int WriteChunk = 1000;
    private const int MaxSteps = 100000;
    private const int RemotesPerSnapshot = 25;
    private const int RegularStates = 13;
    private const int RegularProcesses = 40;
    private const int RegularPrograms = 15;
    private const int RegularListeners = 30;
    private const int FullEntries = 4096;
    private const int FullSnapshots = 8;
    private const int Days = 1095;
    private const int TrimmedAddresses = 100;
    private const string AgentId = "benchmark";
    private const string OpenStatement = "PRAGMA journal_mode = WAL";

    #endregion // Constants

    #region Fields

    private static readonly string[] _tcpStates = [
                                                      "ESTABLISHED",
                                                      "SYN_SENT",
                                                      "SYN_RECV",
                                                      "FIN_WAIT1",
                                                      "FIN_WAIT2",
                                                      "TIME_WAIT",
                                                      "CLOSE",
                                                      "CLOSE_WAIT",
                                                      "LAST_ACK",
                                                      "LISTEN",
                                                      "CLOSING",
                                                      "NEW_SYN_RECV"
                                                  ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Runs the measurement in databases in a new folder below a directory and prints the figures.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    internal static async Task RunAsync(string directory, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-storage-benchmark-{Guid.NewGuid():N}");

        Directory.CreateDirectory(work);

        try
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Console.WriteLine($"databases below {directory}; one fixed clock, default periods");

            await TimeFoldBesideIngestAsync(ProgramBenchmark.Folder(work, "fold"), cancellationToken).ConfigureAwait(false);
            await MeasureWorstCaseAsync(ProgramBenchmark.Folder(work, "worst"), cancellationToken).ConfigureAwait(false);
            await MeasureDayAsync(ProgramBenchmark.Folder(work, "day"), cancellationToken).ConfigureAwait(false);
            await MeasureReadsAsync(ProgramBenchmark.Folder(work, "reads"), cancellationToken).ConfigureAwait(false);
            await MeasureTrimAsync(ProgramBenchmark.Folder(work, "trim"), cancellationToken).ConfigureAwait(false);
            await MeasureMigrationAsync(ProgramBenchmark.Folder(work, "year"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Stores regular connection snapshots in chunks.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="count">The number of snapshots</param>
    /// <param name="first">The capture time of the first snapshot</param>
    /// <param name="interval">The time between two snapshots</param>
    /// <param name="firstIndex">The index of the first snapshot, which decides its addresses</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <param name="inventory">The inventory state of the snapshots, <see langword="null"/> for none</param>
    /// <returns>A task that completes when everything is stored</returns>
    internal static async Task WriteRegularAsync(SqliteStore store, int count, DateTimeOffset first, TimeSpan interval, int firstIndex, CancellationToken cancellationToken, string? inventory = null)
    {
        for (var start = 0; start < count; start += WriteChunk)
        {
            var size = Math.Min(WriteChunk, count - start);

            await store.WriteBatchAsync(RegularBatch(size, first + (start * interval), interval, firstIndex + start, inventory), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs statements in one transaction on the database of a directory.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="statements">The constant statements</param>
    /// <param name="hours">The value of <c>$hours</c> for statements that use it</param>
    /// <param name="first">The value of <c>$first</c> for statements that use it</param>
    /// <param name="cancellationToken">Interrupts the statements</param>
    /// <returns>A task that completes when the statements are done</returns>
    internal static async Task ExecuteAsync(string directory, string[] statements, int hours, long first, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var journal = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
            journal.CommandText = OpenStatement;
#pragma warning restore CA2100
            await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (transaction.ConfigureAwait(false))
            {
                foreach (var statement in statements)
                {
                    using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
                    command.CommandText = statement;
#pragma warning restore CA2100
                    command.Transaction = (SqliteTransaction)transaction;
                    command.Parameters.AddWithValue("$hours", hours);
                    command.Parameters.AddWithValue("$first", first);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads one integer of the database of a directory.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="sql">The constant query</param>
    /// <param name="cancellationToken">Interrupts the query</param>
    /// <returns>A task that returns the integer</returns>
    internal static async Task<long> CountAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
            command.CommandText = sql;
#pragma warning restore CA2100

            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Times every fold step until nothing is left and prints those that fold something and a summary of the steps that only trim.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The time of the fold</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the time of each step that folded something in milliseconds</returns>
    private static async Task<List<double>> TimeStepsAsync(SqliteStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var policy = ProgramBenchmark.CreatePolicy();
        var trimSteps = 0;
        var trimTime = 0D;
        var trimmed = 0;

        for (var step = 0; step < MaxSteps; step++)
        {
            var watch = Stopwatch.StartNew();
            var result = await store.FoldRollupsAsync(policy, now, cancellationToken).ConfigureAwait(false);

            watch.Stop();

            if (result.Values > 0 || result.Snapshots > 0 || result.ConnectionSnapshots > 0 || result.SkippedConnectionSnapshots > 0)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                                $"fold step {times.Count}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Values} values, {result.Snapshots} process snapshots, {result.ConnectionSnapshots} connection snapshots ({result.SkippedConnectionSnapshots} skipped), {result.ConnectionBuckets} connection buckets, {result.TrimmedAddresses} addresses trimmed"));
            }
            else if (result.TrimmedAddresses > 0)
            {
                trimSteps++;
                trimTime += watch.Elapsed.TotalMilliseconds;
            }

            trimmed += result.TrimmedAddresses;

            if (result.More == false)
            {
                break;
            }
        }

        if (trimSteps > 0)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{trimSteps} further steps only trimmed ({trimTime:F0} ms in all, {trimTime / trimSteps:F1} ms each); {trimmed} addresses trimmed in the pass"));
        }

        return times;
    }

    /// <summary>
    /// Times five rounds of metric points, process snapshots and regular connection snapshots, and their fold steps.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task TimeFoldBesideIngestAsync(string directory, CancellationToken cancellationToken)
    {
        Console.WriteLine($"N1: {Batches} batches of {MetricsPerBatch} metric points, {ProcessSnapshotsPerBatch} process snapshots of {ProgramsPerSnapshot} programs and {ConnectionSnapshotsPerBatch} regular connection snapshots ({RemotesPerSnapshot} new remotes, {RegularListeners} listeners)");

        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            var seq = 1UL;
            var batchTimes = new List<double>();
            var start = ProgramBenchmark.Now.AddDays(-3);

            for (var batch = 0; batch < Batches; batch++)
            {
                var watch = Stopwatch.StartNew();
                var offset = start.AddMinutes(batch * ConnectionSnapshotsPerBatch);

                await store.WriteBatchAsync(ProgramBenchmark.MetricBatch(MetricsPerBatch, ref seq, offset), cancellationToken).ConfigureAwait(false);
                await store.WriteBatchAsync(ProgramBenchmark.SnapshotBatch(ProcessSnapshotsPerBatch, offset, TimeSpan.FromMinutes(1), ProgramsPerSnapshot), cancellationToken).ConfigureAwait(false);
                await store.WriteBatchAsync(RegularBatch(ConnectionSnapshotsPerBatch, offset, TimeSpan.FromMinutes(1), batch * ConnectionSnapshotsPerBatch), cancellationToken).ConfigureAwait(false);
                watch.Stop();
                batchTimes.Add(watch.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"batch {batch + 1}: {watch.Elapsed.TotalMilliseconds:F0} ms (metric points, process and connection snapshots)"));
            }

            var foldTimes = await TimeStepsAsync(store, ProgramBenchmark.Now, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N1: median batch {ProgramBenchmark.Median(batchTimes):F0} ms, median fold step {ProgramBenchmark.Median(foldTimes):F0} ms ({foldTimes.Count} steps)"));
        }
    }

    /// <summary>
    /// Stores 10,000 metric points and full connection snapshots up to the byte budget of a step, and times the fold steps.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureWorstCaseAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            var seq = 1UL;
            var records = Enumerable.Range(0, FullSnapshots).Select(index => Record(ProgramBenchmark.Now.AddHours(-3).AddMinutes(index), FullSnapshot(index))).ToArray();

            await store.WriteBatchAsync(ProgramBenchmark.MetricBatch(MetricsPerBatch, ref seq, ProgramBenchmark.Now.AddHours(-3)), cancellationToken).ConfigureAwait(false);
            await store.WriteBatchAsync(Batch(records), cancellationToken).ConfigureAwait(false);

            var steps = await TimeStepsAsync(store, ProgramBenchmark.Now, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"worst case: {MetricsPerBatch} metric points and {FullSnapshots} full snapshots ({FullEntries} remotes and {FullEntries} connections each) folded in {steps.Sum():F0} ms ({steps.Count} steps that folded, slowest {steps.Max():F0} ms)"));
        }
    }

    /// <summary>
    /// Stores a day of one regular snapshot per minute and prints the size of the database before and after its fold and trim.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureDayAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            await WriteRegularAsync(store, DaySnapshots, ProgramBenchmark.Now.AddDays(-1), TimeSpan.FromMinutes(1), 0, cancellationToken).ConfigureAwait(false);

            var before = await ProgramBenchmark.PagesAsync(directory, cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var steps = await TimeStepsAsync(store, ProgramBenchmark.Now, cancellationToken).ConfigureAwait(false);

            watch.Stop();

            var after = await ProgramBenchmark.PagesAsync(directory, cancellationToken).ConfigureAwait(false);
            var rows = await CountAsync(directory, "SELECT (SELECT count(*) FROM connection_rollups_5m) + (SELECT count(*) FROM connection_rollups_1h)", cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                            $"N3: a day of {DaySnapshots} regular snapshots: page_count {before.Pages} before the fold, {after.Pages} after (page size {after.PageSize} bytes, +{(after.Pages - before.Pages) * after.PageSize / 1024.0:F0} KiB, {rows} rollup rows); the fold and trim took {watch.Elapsed.TotalMilliseconds:F0} ms in {steps.Count} steps"));
        }
    }

    /// <summary>
    /// Writes 1,095 days of hourly buckets at the trimmed size directly into the tables and times both reads over a year and over all days.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureReadsAsync(string directory, CancellationToken cancellationToken)
    {
        await (await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);

        var hours = Days * 24;
        var first = StorageTime.ToNanoseconds(ProgramBenchmark.Now.AddDays(-Days));
        var watch = Stopwatch.StartNew();

        await ExecuteAsync(directory, [RowsInsert, TotalsInsert], hours, first, cancellationToken).ConfigureAwait(false);
        watch.Stop();

        var rows = await CountAsync(directory, "SELECT count(*) FROM connection_rollups_1h", cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N2: {hours} hourly buckets of {TrimmedAddresses} addresses, {RegularStates} states and {RegularPrograms} programs written directly into the tables ({rows} rows, not folded) in {watch.Elapsed.TotalMilliseconds:F0} ms"));

        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            await TimeReadsAsync(store, "one-year", 365, TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
            await TimeReadsAsync(store, "1,095-day", Days, TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Times the read of states and programs and the read of addresses over a range.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="name">The name of the range</param>
    /// <param name="days">The days of the range before the fixed clock</param>
    /// <param name="step">The step</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task TimeReadsAsync(SqliteStore store, string name, int days, TimeSpan step, CancellationToken cancellationToken)
    {
        var query = new ConnectionRollupQuery
                    {
                        Source = Source,
                        From = ProgramBenchmark.Now.AddDays(-days),
                        To = ProgramBenchmark.Now,
                        Step = step
                    };
        var states = new List<double>();
        var addresses = new List<double>();
        ConnectionRollupResult? statesResult = null;
        ConnectionAddressResult? addressesResult = null;

        for (var read = 0; read < Reads; read++)
        {
            var watch = Stopwatch.StartNew();

            statesResult = await store.ConnectionRollupsAsync(query, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            states.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            addressesResult = await store.ConnectionAddressesAsync(query, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            addresses.Add(watch.Elapsed.TotalMilliseconds);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name} read {read + 1}: states and programs {states[^1]:F0} ms, addresses {addresses[^1]:F0} ms"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                        $"N2: median of {Reads} {name} reads at a {step} step: states and programs {ProgramBenchmark.Median(states):F0} ms ({statesResult?.Times.Count} steps, {statesResult?.Programs.Count} programs), addresses {ProgramBenchmark.Median(addresses):F0} ms ({addressesResult?.Addresses.Count} addresses, {addressesResult?.Further} further)"));
    }

    /// <summary>
    /// Folds one hour of regular snapshots of new addresses before it ends, so that its bucket holds 1,500 addresses, and times the step that trims it.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureTrimAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            var hour = ProgramBenchmark.Now.AddDays(-1);

            await WriteRegularAsync(store, 60, hour, TimeSpan.FromMinutes(1), 0, cancellationToken).ConfigureAwait(false);
            await TimeStepsAsync(store, hour.AddMinutes(59), cancellationToken).ConfigureAwait(false);

            var addresses = await CountAsync(directory, "SELECT addresses FROM connection_rollup_totals_1h", cancellationToken).ConfigureAwait(false);
            var policy = ProgramBenchmark.CreatePolicy();
            var watch = Stopwatch.StartNew();
            var steps = 0;
            RollupStepResult result;

            do
            {
                result = await store.FoldRollupsAsync(policy, ProgramBenchmark.Now, cancellationToken).ConfigureAwait(false);
                steps++;
            }
            while (result.More);

            watch.Stop();

            var kept = await CountAsync(directory, "SELECT addresses FROM connection_rollup_totals_1h", cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N7: an untrimmed hour of {addresses} addresses; the pass that trims it took {watch.Elapsed.TotalMilliseconds:F0} ms ({steps} steps), {kept} addresses kept"));
        }
    }

    /// <summary>
    /// Stores a year of hourly regular snapshots, takes the database back to schema version 16 and times the open that applies step 17.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureMigrationAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            await WriteRegularAsync(store, YearSnapshots, ProgramBenchmark.Now.AddDays(-365), TimeSpan.FromHours(1), 0, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(directory,
                           [
                               "DROP TABLE connection_rollups_5m",
                               "DROP TABLE connection_rollup_totals_5m",
                               "DROP TABLE connection_rollups_1h",
                               "DROP TABLE connection_rollup_totals_1h",
                               "DROP INDEX records_connection_fold",
                               "DELETE FROM meta WHERE key = 'connection_rollup_after_id'",
                               "UPDATE meta SET value = '16' WHERE key = 'schema_version'"
                           ],
                           0,
                           0,
                           cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        var watch = Stopwatch.StartNew();
        var reopened = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        watch.Stop();
        await reopened.DisposeAsync().ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"schema step 17 (tables, indexes, records_connection_fold over {YearSnapshots} snapshots) on the year database: open took {watch.Elapsed.TotalMilliseconds:F0} ms"));
    }

    /// <summary>
    /// Creates a batch of records that comes from the backend.
    /// </summary>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    private static RecordBatch Batch(DataRecord[] records)
    {
        return new RecordBatch
               {
                   AgentId = AgentId,
                   ReceivedAt = ProgramBenchmark.Now,
                   Records = records
               };
    }

    /// <summary>
    /// Creates a connection snapshot record of the benchmark source.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The record</returns>
    private static DataRecord Record(DateTimeOffset capturedAt, ConnectionSnapshot snapshot)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = Source,
                   CapturedAt = capturedAt,
                   Data = snapshot
               };
    }

    /// <summary>
    /// Builds a batch of regular snapshots.
    /// </summary>
    /// <param name="size">The number of snapshots</param>
    /// <param name="first">The capture time of the first snapshot</param>
    /// <param name="interval">The time between two snapshots</param>
    /// <param name="firstIndex">The index of the first snapshot</param>
    /// <param name="inventory">The inventory state of the snapshots, <see langword="null"/> for none</param>
    /// <returns>The batch</returns>
    private static RecordBatch RegularBatch(int size, DateTimeOffset first, TimeSpan interval, int firstIndex, string? inventory = null)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = Record(first + (index * interval), RegularSnapshot(firstIndex + index, inventory));
        }

        return Batch(records);
    }

    /// <summary>
    /// Builds a regular snapshot of the agent: aggregates and the listener inventory, 25 remote addresses no other snapshot names.
    /// </summary>
    /// <param name="index">The index of the snapshot</param>
    /// <param name="inventory">The inventory state of the snapshot, <see langword="null"/> for none</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot RegularSnapshot(int index, string? inventory = null)
    {
        var states = new List<StateCount>();

        for (var state = 0; state < RegularStates - 1; state++)
        {
            states.Add(new StateCount
                       {
                           Proto = state % 2 == 0 ? Proto.Tcp : Proto.Tcp6,
                           State = _tcpStates[state % _tcpStates.Length],
                           Count = (uint)(1 + ((index + state) % 20))
                       });
        }

        states.Add(new StateCount
                   {
                       Proto = Proto.Udp,
                       Count = 4
                   });

        return new ConnectionSnapshot
               {
                   Complete = false,
                   AggregatesOnly = true,
                   States = states,
                   Processes = [
                                   .. Enumerable.Range(1, RegularProcesses).Select(pid => new ProcessConnections
                                                                                          {
                                                                                              Pid = pid,
                                                                                              Command = string.Create(CultureInfo.InvariantCulture, $"program-{pid % RegularPrograms:D2}"),
                                                                                              Count = (uint)(1 + ((index + pid) % 9))
                                                                                          })
                               ],
                   Remotes = [
                                 .. Enumerable.Range(0, RemotesPerSnapshot).Select(remote => new RemoteCount
                                                                                             {
                                                                                                 Addr = NewAddress((index * RemotesPerSnapshot) + remote),
                                                                                                 Count = (uint)(1 + ((index + remote) % 5))
                                                                                             })
                             ],
                   Listeners = Listeners(),
                   Inventory = inventory
               };
    }

    /// <summary>
    /// Builds a full snapshot: 4,096 remote addresses and 4,096 connections beside the aggregates and listeners.
    /// </summary>
    /// <param name="index">The index of the snapshot</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot FullSnapshot(int index)
    {
        var snapshot = RegularSnapshot(index);
        var connections = new List<Connection>(FullEntries);
        var remotes = new List<RemoteCount>(FullEntries);

        for (var entry = 0; entry < FullEntries; entry++)
        {
            var address = NewAddress(1_000_000 + (index * FullEntries) + entry);

            remotes.Add(new RemoteCount
                        {
                            Addr = address,
                            Count = (uint)(1 + (entry % 7))
                        });
            connections.Add(new Connection
                            {
                                Proto = Proto.Tcp,
                                Local = new IPEndPoint(IPAddress.Loopback, 443),
                                Remote = new IPEndPoint(address, 1024 + (entry % 60000)),
                                State = "ESTABLISHED",
                                Pid = 1 + (entry % 40),
                                Command = "nginx",
                                Uid = 33,
                                User = "www-data"
                            });
        }

        snapshot.AggregatesOnly = false;
        snapshot.Complete = true;
        snapshot.Remotes = remotes;
        snapshot.Connections = connections;

        return snapshot;
    }

    /// <summary>
    /// Builds the listener inventory of a Plesk host: 30 listeners shaped as those of the wire sample.
    /// </summary>
    /// <returns>The listeners</returns>
    private static List<Listener> Listeners()
    {
        return [
                   .. Enumerable.Range(0, RegularListeners).Select(number => new Listener
                                                                             {
                                                                                 Proto = number % 3 == 0 ? Proto.Tcp6 : Proto.Tcp,
                                                                                 Local = new IPEndPoint(number % 3 == 0 ? IPAddress.IPv6Any : IPAddress.Any, 20 + (number * 37)),
                                                                                 Pid = 100 + number,
                                                                                 Command = number % 2 == 0 ? "nginx" : "mariadbd",
                                                                                 Uid = number % 2 == 0 ? 0u : 110u,
                                                                                 User = number % 2 == 0 ? "root" : "mysql"
                                                                             })
               ];
    }

    /// <summary>
    /// Returns the address of a number: every number names an address no other number names.
    /// </summary>
    /// <param name="number">The number</param>
    /// <returns>The address</returns>
    private static IPAddress NewAddress(int number)
    {
        return new IPAddress([10, (byte)((number >> 16) & 255), (byte)((number >> 8) & 255), (byte)(number & 255)]);
    }

    #endregion // Methods
}