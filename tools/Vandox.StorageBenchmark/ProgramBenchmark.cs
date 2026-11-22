using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the per-program rollups of process snapshots (issue #458, <c>docs/BENCHMARKS.md</c>): the fold steps beside stored metrics, the disk growth of a
/// day of snapshots, the one-year read at a 6-hour step, one worst-case fold step and the duration of schema step 16 on a database that holds the year.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ProgramBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--programs";

    private const int Batches = 5;
    private const int MetricsPerBatch = 10000;
    private const int SnapshotsPerBatch = 500;
    private const int ProgramsPerSnapshot = 80;
    private const int DaySnapshots = 1440;
    private const int YearSnapshots = 8760;
    private const int Reads = 5;
    private const int ReadCount = 10;
    private const int WriteChunk = 1000;
    private const int MaxSteps = 100000;
    private const int WorstPrograms = 4096;
    private const int WorstNameBytes = 1024;
    private const int HugeProcesses = 1000;
    private const int HugeCommandBytes = 15900;
    private const string AgentId = "benchmark";
    private const string BootId = "0123abcd-0123-0123-0123-0123456789ab";
    private const string Source = "processes";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time of the fixed clock of the measurements.
    /// </summary>
    internal static readonly DateTimeOffset Now = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

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

            await TimeFoldBesideMetricsAsync(Folder(work, "fold"), cancellationToken).ConfigureAwait(false);
            await MeasureDayAsync(Folder(work, "day"), cancellationToken).ConfigureAwait(false);
            await MeasureYearAsync(Folder(work, "year"), cancellationToken).ConfigureAwait(false);
            await MeasureWorstCaseAsync(Folder(work, "worst"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Creates a folder for one database.
    /// </summary>
    /// <param name="work">The working folder</param>
    /// <param name="name">The name of the folder</param>
    /// <returns>The path</returns>
    internal static string Folder(string work, string name)
    {
        var path = Path.Combine(work, name);

        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// Creates the default retention policy.
    /// </summary>
    /// <returns>The policy</returns>
    internal static RetentionPolicy CreatePolicy()
    {
        return new RetentionPolicy
               {
                   Raw = TimeSpan.FromDays(30),
                   FiveMinuteRollups = TimeSpan.FromDays(365),
                   HourlyRollups = TimeSpan.FromDays(1095),
                   Logs = TimeSpan.FromDays(90),
                   Snapshots = TimeSpan.FromDays(30),
                   Events = TimeSpan.FromDays(1095),
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Returns the median of the times.
    /// </summary>
    /// <param name="times">The times in milliseconds</param>
    /// <returns>The median, 0 for none</returns>
    internal static double Median(List<double> times)
    {
        return times.Count == 0 ? 0 : times.Order().ElementAt(times.Count / 2);
    }

    /// <summary>
    /// Times every fold step until nothing is left.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the time of each step in milliseconds</returns>
    internal static async Task<List<double>> TimeStepsAsync(SqliteStore store, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var policy = CreatePolicy();

        for (var step = 0; step < MaxSteps; step++)
        {
            var watch = Stopwatch.StartNew();
            var result = await store.FoldRollupsAsync(policy, Now, cancellationToken).ConfigureAwait(false);

            watch.Stop();

            if (result.Values > 0 || result.Snapshots > 0 || result.SkippedSnapshots > 0)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"fold step {times.Count}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Values} values, {result.Snapshots} snapshots ({result.SkippedSnapshots} skipped), {result.ProgramBuckets} program buckets"));
            }

            if (result.More == false)
            {
                break;
            }
        }

        return times;
    }

    /// <summary>
    /// Stores snapshots in chunks.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="count">The number of snapshots</param>
    /// <param name="first">The capture time of the first snapshot</param>
    /// <param name="interval">The time between two snapshots</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when everything is stored</returns>
    internal static async Task WriteSnapshotsAsync(SqliteStore store, int count, DateTimeOffset first, TimeSpan interval, CancellationToken cancellationToken)
    {
        for (var start = 0; start < count; start += WriteChunk)
        {
            var size = Math.Min(WriteChunk, count - start);

            await store.WriteBatchAsync(SnapshotBatch(size, first + (start * interval), interval, ProgramsPerSnapshot), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Times the fold steps of five batches of metric points and snapshots, and prints them beside the time of the batches.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task TimeFoldBesideMetricsAsync(string directory, CancellationToken cancellationToken)
    {
        Console.WriteLine($"N2: {Batches} batches of {MetricsPerBatch} metric points and {SnapshotsPerBatch} snapshots of {ProgramsPerSnapshot} programs each");

        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            var seq = 1UL;
            var batchTimes = new List<double>();

            for (var batch = 0; batch < Batches; batch++)
            {
                var watch = Stopwatch.StartNew();

                await store.WriteBatchAsync(MetricBatch(MetricsPerBatch, ref seq, Now.AddHours(-3).AddSeconds(batch * MetricsPerBatch)), cancellationToken).ConfigureAwait(false);
                await store.WriteBatchAsync(SnapshotBatch(SnapshotsPerBatch, Now.AddHours(-3).AddMinutes(batch * SnapshotsPerBatch), TimeSpan.FromMinutes(1), ProgramsPerSnapshot), cancellationToken).ConfigureAwait(false);
                watch.Stop();
                batchTimes.Add(watch.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"batch {batch + 1}: {watch.Elapsed.TotalMilliseconds:F0} ms (metric points and snapshots)"));
            }

            var foldTimes = await TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N2: median batch {Median(batchTimes):F0} ms, median fold step {Median(foldTimes):F0} ms ({foldTimes.Count} steps)"));
        }
    }

    /// <summary>
    /// Stores a day of one snapshot per minute and prints the size of the database before and after its fold.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureDayAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            await WriteSnapshotsAsync(store, DaySnapshots, Now.AddDays(-1), TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

            var before = await PagesAsync(directory, cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var steps = await TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

            watch.Stop();

            var after = await PagesAsync(directory, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N4: a day of {DaySnapshots} snapshots of {ProgramsPerSnapshot} programs: page_count {before.Pages} before the fold, {after.Pages} after (page size {after.PageSize} bytes, +{(after.Pages - before.Pages) * after.PageSize / 1024.0:F0} KiB); the fold took {watch.Elapsed.TotalMilliseconds:F0} ms in {steps.Count} steps"));
        }
    }

    /// <summary>
    /// Reads the number of pages of the database after a checkpoint that moves the journal into it.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the page count and the page size</returns>
    private static async Task<(long Pages, long PageSize)> PagesAsync(string directory, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var pages = await ScalarAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", "PRAGMA page_count", cancellationToken).ConfigureAwait(false);
            var size = await ScalarAsync(connection, "PRAGMA page_size", null, cancellationToken).ConfigureAwait(false);

            return (pages, size);
        }
    }

    /// <summary>
    /// Runs a statement and, when a second one is given, reads its integer result.
    /// </summary>
    /// <param name="connection">The open connection</param>
    /// <param name="first">The statement to run, or to read when there is no second one</param>
    /// <param name="second">The statement to read, or <c>null</c></param>
    /// <param name="cancellationToken">Interrupts the statement</param>
    /// <returns>A task that returns the integer</returns>
    private static async Task<long> ScalarAsync(SqliteConnection connection, string first, string? second, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
        command.CommandText = first;

        if (second is null)
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = second;
#pragma warning restore CA2100

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stores a year of hourly snapshots, folds them, times the one-year reads and then the migration step that builds the program rollups of the stored snapshots.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureYearAsync(string directory, CancellationToken cancellationToken)
    {
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        await using (store.ConfigureAwait(false))
        {
            await WriteSnapshotsAsync(store, YearSnapshots, Now.AddDays(-365), TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            var steps = await TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

            watch.Stop();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"a year of {YearSnapshots} hourly snapshots folded in {watch.Elapsed.TotalMilliseconds:F0} ms ({steps.Count} steps)"));

            var times = new List<double>();
            var query = new ProgramRollupQuery
                        {
                            Source = Source,
                            From = Now.AddDays(-365),
                            To = Now,
                            Step = TimeSpan.FromHours(6),
                            Measure = ProgramMeasure.Memory,
                            Count = ReadCount
                        };
            ProgramRollupResult? result = null;

            for (var read = 0; read < Reads; read++)
            {
                var timer = Stopwatch.StartNew();

                result = await store.ProgramRollupsAsync(query, cancellationToken).ConfigureAwait(false);
                timer.Stop();
                times.Add(timer.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"one-year read {read + 1}: {timer.Elapsed.TotalMilliseconds:F0} ms"));
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N3: median of {Reads} one-year reads at a 6-hour step, memory, n = {ReadCount}: {Median(times):F0} ms ({result?.Times.Count} steps, {result?.Programs.Count} programs)"));
        }

        await MeasureMigrationAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the database back to schema version 15 and times the open that applies step 16 and folds nothing.
    /// </summary>
    /// <param name="directory">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureMigrationAsync(string directory, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string[] statements = [
                                      "DROP TABLE program_rollups_5m",
                                      "DROP TABLE program_rollup_totals_5m",
                                      "DROP TABLE program_rollups_1h",
                                      "DROP TABLE program_rollup_totals_1h",
                                      "DROP INDEX records_process_fold",
                                      "DELETE FROM meta WHERE key = 'program_rollup_after_id'",
                                      "UPDATE meta SET value = '15' WHERE key = 'schema_version'"
                                  ];

            foreach (var statement in statements)
            {
                using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
                command.CommandText = statement;
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        SqliteConnection.ClearAllPools();

        var watch = Stopwatch.StartNew();
        var store = await SqliteStore.OpenAsync(directory, cancellationToken).ConfigureAwait(false);

        watch.Stop();
        await store.DisposeAsync().ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"schema step 16 (tables, indexes, records_process_fold over {YearSnapshots} snapshots) on the year database: open took {watch.Elapsed.TotalMilliseconds:F0} ms"));
    }

    /// <summary>
    /// Stores one worst-case fold step, which holds 10,000 metric points and snapshots of 4,096 programs with 1,024-byte names, and then one snapshot of about 16 MiB, and times their fold steps.
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

            await store.WriteBatchAsync(MetricBatch(MetricsPerBatch, ref seq, Now.AddHours(-3)), cancellationToken).ConfigureAwait(false);
            await store.WriteBatchAsync(Batch([Record(Now.AddHours(-2), WorstSnapshot(0))]), cancellationToken).ConfigureAwait(false);

            var typical = await TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"worst case: {MetricsPerBatch} metric points and one snapshot of {WorstPrograms} programs with {WorstNameBytes}-byte names folded in {typical.Sum():F0} ms ({typical.Count} steps)"));

            var huge = HugeSnapshot();

            await store.WriteBatchAsync(Batch([Record(Now.AddHours(-1), huge)]), cancellationToken).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            var steps = await TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

            watch.Stop();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"worst case: one snapshot of {HugeProcesses} processes with {HugeCommandBytes}-byte command lines (about 16 MiB) folded in {watch.Elapsed.TotalMilliseconds:F0} ms ({steps.Count} steps)"));
        }
    }

    /// <summary>
    /// Builds a batch of agent metric points one second apart.
    /// </summary>
    /// <param name="size">The number of records</param>
    /// <param name="seq">The next sequence number, advanced by <paramref name="size"/></param>
    /// <param name="first">The capture time of the first record</param>
    /// <returns>The batch</returns>
    private static RecordBatch MetricBatch(int size, ref ulong seq, DateTimeOffset first)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = new DataRecord
                             {
                                 Origin = RecordOrigin.Agent,
                                 Source = "host",
                                 Seq = seq++,
                                 CapturedAt = first.AddSeconds(index),
                                 Data = new MetricPoint
                                        {
                                            Name = "cpu",
                                            Value = 1.5,
                                            Unit = "percent",
                                            Labels = new Dictionary<string, string>
                                                     {
                                                         ["cpu"] = "0"
                                                     }
                                        }
                             };
        }

        return new RecordBatch
               {
                   AgentId = AgentId,
                   BootId = BootId,
                   ClockOffsetNs = 0,
                   ReceivedAt = Now,
                   Records = records
               };
    }

    /// <summary>
    /// Builds a batch of process snapshots of a source.
    /// </summary>
    /// <param name="size">The number of snapshots</param>
    /// <param name="first">The capture time of the first snapshot</param>
    /// <param name="interval">The time between two snapshots</param>
    /// <param name="programs">The number of programs of a snapshot</param>
    /// <returns>The batch</returns>
    private static RecordBatch SnapshotBatch(int size, DateTimeOffset first, TimeSpan interval, int programs)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = Record(first + (index * interval), TypicalSnapshot(index, programs));
        }

        return Batch(records);
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
                   ReceivedAt = Now,
                   Records = records
               };
    }

    /// <summary>
    /// Creates a process snapshot record of the benchmark source.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The record</returns>
    private static DataRecord Record(DateTimeOffset capturedAt, ProcessSnapshot snapshot)
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
    /// Builds a snapshot of programs whose values vary with its index.
    /// </summary>
    /// <param name="index">The index of the snapshot</param>
    /// <param name="programs">The number of programs</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot TypicalSnapshot(int index, int programs)
    {
        var list = new List<ProgramAggregate>(programs);

        for (var program = 0; program < programs; program++)
        {
            var spread = ((index * 7) + (program * 13)) % 5000 * 1000L;

            list.Add(new ProgramAggregate
                     {
                         Program = string.Create(CultureInfo.InvariantCulture, $"program-{program:D3}"),
                         Count = 1,
                         CpuPercent = (index + program) % 100 / 10.0,
                         RssBytes = (ulong)(1_000_000 + spread),
                         PssBytes = (ulong)(800_000 + spread)
                     });
        }

        return Snapshot(list);
    }

    /// <summary>
    /// Builds a snapshot of the most programs with the longest names.
    /// </summary>
    /// <param name="index">The index of the snapshot</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot WorstSnapshot(int index)
    {
        var list = new List<ProgramAggregate>(WorstPrograms);

        for (var program = 0; program < WorstPrograms; program++)
        {
            list.Add(new ProgramAggregate
                     {
                         Program = string.Create(CultureInfo.InvariantCulture, $"{index}-{program:D4}-").PadRight(WorstNameBytes, 'x'),
                         Count = 1,
                         CpuPercent = program % 100,
                         RssBytes = (ulong)(1000 + program),
                         PssBytes = (ulong)(500 + program)
                     });
        }

        return Snapshot(list);
    }

    /// <summary>
    /// Builds a snapshot of about 16 MiB: many processes with the longest command lines.
    /// </summary>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot HugeSnapshot()
    {
        var processes = new List<ProcessSample>(HugeProcesses);

        for (var pid = 1; pid <= HugeProcesses; pid++)
        {
            processes.Add(new ProcessSample
                          {
                              Pid = pid,
                              Command = "worker",
                              Cmdline = new string('a', HugeCommandBytes),
                              RssBytes = 1000
                          });
        }

        return new ProcessSnapshot
               {
                   Complete = true,
                   Total = HugeProcesses,
                   Processes = processes,
                   Programs = [
                                  new ProgramAggregate
                                  {
                                      Program = "worker",
                                      Count = HugeProcesses,
                                      CpuPercent = 1,
                                      RssBytes = 1000
                                  }
                              ]
               };
    }

    /// <summary>
    /// Wraps programs into a complete snapshot with one process.
    /// </summary>
    /// <param name="programs">The programs</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot Snapshot(List<ProgramAggregate> programs)
    {
        return new ProcessSnapshot
               {
                   Complete = true,
                   Total = (uint)programs.Sum(program => program.Count),
                   Processes = [
                                   new ProcessSample
                                   {
                                       Pid = 1,
                                       Command = "init",
                                       RssBytes = 1
                                   }
                               ],
                   Programs = programs
               };
    }

    #endregion // Methods
}