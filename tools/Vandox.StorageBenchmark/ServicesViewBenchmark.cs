using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the three actions of the services view (feature 52, <c>docs/BENCHMARKS.md</c>) on a database written directly into the tables: 30 days of <c>mariadb_status</c> records at 30 seconds
/// (86,400; every 100th carries 60 threads with 16 KiB statements, about 1 MiB), the five metrics of the MariaDB charts as 30 days of raw values at 30 seconds beside the 5-minute and hourly rollups,
/// 256 units (the agent's cap) with an hourly <c>service_state</c> record over a year and a restart per unit per day, and a refused unit's <c>systemd</c> gap per 30-second run over the last
/// 30 days. It times the build of the index of schema step 18 once, then each action of a 30-day and of a 1-year range as the median of five, and prints the verdict against 10 seconds over seven of the eight medians: the 1-year services action at 256 units is printed
/// and left out (E12, amended AC10).
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ServicesViewBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--services-view";

    private const int Reads = 5;
    private const int ChartWidth = 1000;
    private const int Units = 256;
    private const int UnitDays = 365;
    private const int StatusDays = 30;
    private const int RunsPerDay = 2880;
    private const int ThreadsPerSnapshot = 60;
    private const int SnapshotEvery = 100;
    private const double CriterionMilliseconds = 10000;
    private const string RecordsInsert = "INSERT INTO records (id, kind, origin, source, agent_id, seq, captured_at, received_at, boot_id, clock_offset_ns, data) ";

    private const string StatusIndexName = "records_mariadb_status";

    private const string IsoFormat = "'%Y-%m-%dT%H:%M:%SZ'";

    /// <summary>
    /// The five metrics: the index, source, name and unit.
    /// </summary>
    private const string SeriesValues = "(VALUES (0, 'mariadb', 'mariadb_memory_max_bytes', 'bytes'), (1, 'system', 'mem_total_bytes', 'bytes'), (2, 'mariadb', 'mariadb_questions_per_second', 'per_second'), "
                                        + "(3, 'mariadb', 'mariadb_innodb_buffer_pool_used_percent', 'percent'), (4, 'mariadb', 'mariadb_innodb_buffer_pool_read_hit_percent', 'percent'))";

    /// <summary>
    /// The raw values: one record and one metric row per series and 30-second sample of the last 30 days. Ids 1 to 432,000.
    /// </summary>
    private const string RawRecordsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $samples - 1), s(i, source, name, unit) AS " + SeriesValues + " "
                                            + RecordsInsert
                                            + "SELECT t.n * 5 + s.i + 1, 'metric', 'agent', s.source, 'benchmark', t.n * 5 + s.i + 1, $statusStart + t.n * 30000000000, $statusStart + t.n * 30000000000, $boot, 0, NULL FROM t, s";

    private const string RawMetricsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $samples - 1), s(i, source, name, unit) AS " + SeriesValues + " "
                                            + "INSERT INTO metrics (record_id, source, name, captured_at, value, unit, labels) "
                                            + "SELECT t.n * 5 + s.i + 1, s.source, s.name, $statusStart + t.n * 30000000000, "
                                            + "CASE s.i WHEN 0 THEN 6442450944.0 WHEN 1 THEN 4294967296.0 ELSE 20.0 + (t.n % 60) END, s.unit, NULL FROM t, s";

    /// <summary>
    /// The 5-minute rollups of the last 30 days and the hourly rollups of the year, as the fold writes them.
    /// </summary>
    private const string FiveMinuteRollupsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $fiveMinuteBuckets - 1), s(i, source, name, unit) AS " + SeriesValues + " "
                                                   + "INSERT INTO metric_rollups_5m (source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                                                   + "SELECT s.source, s.name, '', $statusStart + t.n * 300000000000, 10.0, 90.0, 500.0, 10, 50.0, $statusStart + t.n * 300000000000 + 270000000000, 1 FROM t, s";

    private const string HourlyRollupsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $hourlyBuckets - 1), s(i, source, name, unit) AS " + SeriesValues + " "
                                               + "INSERT INTO metric_rollups_1h (source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                                               + "SELECT s.source, s.name, '', $unitStart + t.n * 3600000000000, 10.0, 90.0, 6000.0, 120, 50.0, $unitStart + t.n * 3600000000000 + 3570000000000, 1 FROM t, s";

    private const string WatermarkInsert = "INSERT OR REPLACE INTO meta (key, value) VALUES ('rollup_after_id', CAST($samples * 5 AS TEXT))";

    /// <summary>
    /// The unit states: one record per unit and hour, a restart (a new enter time and a higher counter) at the first record of each day, unit 1 failing for two hours each day. Ids from 1,000,001.
    /// </summary>
    private const string UnitRecordsInsert = "WITH RECURSIVE h(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM h WHERE n < $hours - 1), u(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM u WHERE n < $units - 1) "
                                             + RecordsInsert
                                             + "SELECT 1000000 + h.n * $units + u.n + 1, 'service_state', 'agent', 'systemd', 'benchmark', 1000000 + h.n * $units + u.n + 1, "
                                             + "$unitStart + h.n * 3600000000000 + u.n * 1000000000, $unitStart + h.n * 3600000000000 + u.n * 1000000000, $boot, 0, "
                                             + "json_object('unit', printf('unit%03d.service', u.n), 'load_state', 'loaded', "
                                             + "'active_state', CASE WHEN u.n = 1 AND h.n % 24 IN (3, 4) THEN 'failed' ELSE 'active' END, "
                                             + "'sub_state', CASE WHEN u.n = 1 AND h.n % 24 IN (3, 4) THEN 'failed' ELSE 'running' END, "
                                             + "'active_enter_at', strftime(" + IsoFormat + ", ($unitStart / 1000000000) + (h.n / 24) * 86400 - 30, 'unixepoch'), "
                                             + "'restarts', h.n / 24) FROM h, u";

    /// <summary>
    /// The status records: one per 30 seconds of 30 days, down for 20 samples in every 24 hours, every 100th with 60 threads of 16 KiB statements, two thirds of them not sleeping. Ids from 5,000,001.
    /// </summary>
    private const string StatusRecordsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $samples - 1), th(k) AS (SELECT 1 UNION ALL SELECT k + 1 FROM th WHERE k < $threads) "
                                               + RecordsInsert
                                               + "SELECT 5000000 + b.n + 1, 'mariadb_status', 'agent', 'mariadb', 'benchmark', 5000000 + b.n + 1, $statusStart + b.n * 30000000000, $statusStart + b.n * 30000000000, $boot, 0, "
                                               + "CASE WHEN b.n % $every = 0 THEN json_insert(b.base, '$.threads', json((SELECT json_group_array(json_object('id', 1000 + th.k, 'user', 'app', 'host', '10.0.0.5:3306', 'db', 'shop', "
                                               + "'command', CASE WHEN th.k % 3 = 0 THEN 'Sleep' ELSE 'Query' END, 'time_seconds', th.k * 7, 'state', 'Sending data', 'info', replace(hex(zeroblob(8192)), '0', 'x'))) FROM th))) "
                                               + "ELSE b.base END "
                                               + "FROM (SELECT t.n AS n, json_object('availability', CASE WHEN t.n % 2880 BETWEEN 1000 AND 1019 THEN 'down' ELSE 'up' END, "
                                               + "'status', json_object('Memory_used', 1288490188 + (t.n % 1000) * 1000000, 'Threads_connected', 20 + t.n % 30, 'Threads_running', 3 + t.n % 4), 'complete', json('true')) AS base FROM t) AS b";

    /// <summary>
    /// The gap records of a refused unit: one per 30-second run of the 30 days, from the run's start over the interval, so they touch edge to edge. Ids from 6,000,001.
    /// </summary>
    private const string GapRecordsInsert = "WITH RECURSIVE t(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM t WHERE n < $samples - 1) "
                                            + RecordsInsert
                                            + "SELECT 6000000 + t.n + 1, 'gap', 'agent', 'systemd', 'benchmark', 6000000 + t.n + 1, $statusStart + (t.n + 1) * 30000000000, $statusStart + (t.n + 1) * 30000000000, $boot, 0, "
                                            + "json_object('from', strftime(" + IsoFormat + ", ($statusStart / 1000000000) + t.n * 30, 'unixepoch'), 'to', strftime(" + IsoFormat + ", ($statusStart / 1000000000) + (t.n + 1) * 30, 'unixepoch'), "
                                            + "'cause', 'collector_timeout', 'collector', 'systemd', 'first_seq', 0, 'last_seq', 0) FROM t";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs the measurement in a database in a new folder below a directory and prints the figures and the verdict.
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
            Console.WriteLine($"database below {directory}; one fixed clock, default periods");

            var folder = ProgramBenchmark.Folder(work, "view");

            // The store creates the schema of the current version; the index of step 18 is dropped before the fill and built once afterwards, as the migration of an existing database does.
            await (await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            await ExecuteAsync(folder, ["DROP INDEX " + StatusIndexName], cancellationToken).ConfigureAwait(false);
            await FillAsync(folder, cancellationToken).ConfigureAwait(false);
            await BuildIndexAsync(folder, cancellationToken).ConfigureAwait(false);

            var store = await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false);

            await using (store.ConfigureAwait(false))
            {
                var medians = new List<(string Name, double Median)>();

                foreach (var days in new[] { StatusDays, UnitDays })
                {
                    await MeasureAsync(store, days, medians, cancellationToken).ConfigureAwait(false);
                }

                ProgramBenchmark.Verdict("AC10", medians, CriterionMilliseconds);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Writes the metrics, the unit states, the status records and the gap records directly into the tables.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the records are written</returns>
    private static async Task FillAsync(string folder, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        await ExecuteAsync(folder, [RawRecordsInsert, RawMetricsInsert, FiveMinuteRollupsInsert, HourlyRollupsInsert, WatermarkInsert], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(folder, [UnitRecordsInsert], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(folder, [StatusRecordsInsert, GapRecordsInsert], cancellationToken).ConfigureAwait(false);
        watch.Stop();

        var units = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM records WHERE kind = 'service_state'", cancellationToken).ConfigureAwait(false);
        var statuses = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM records WHERE kind = 'mariadb_status'", cancellationToken).ConfigureAwait(false);
        var snapshots = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM records WHERE kind = 'mariadb_status' AND json_array_length(data, '$.threads') > 0", cancellationToken).ConfigureAwait(false);
        var largest = await ConnectionBenchmark.CountAsync(folder, "SELECT max(length(data)) FROM records WHERE kind = 'mariadb_status'", cancellationToken).ConfigureAwait(false);
        var gaps = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM records WHERE kind = 'gap'", cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                        $"N3: {units} service state records of {Units} units over {UnitDays} days, {statuses} status records of {StatusDays} days ({snapshots} with threads, the largest {largest} bytes), {gaps} per-run systemd gap records, "
                                        + $"five metrics raw for {StatusDays} days beside their rollups, written directly into the tables in {watch.Elapsed.TotalSeconds:F0} s; database {new FileInfo(Path.Combine(folder, StorageLimits.FileName)).Length / (1024 * 1024)} MiB"));
    }

    /// <summary>
    /// Times the build of the index of schema step 18 over the stored status records, once.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the index is built</returns>
    private static async Task BuildIndexAsync(string folder, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        await ExecuteAsync(folder, [SchemaMigrator.MariaDbStatusIndexStatement], cancellationToken).ConfigureAwait(false);
        watch.Stop();

        var count = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM records WHERE kind = 'mariadb_status'", cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"step 18: index {StatusIndexName} built over {count} status records in {watch.Elapsed.TotalMilliseconds:F0} ms"));
    }

    /// <summary>
    /// Runs statements with the parameters of the workload, each in its own transaction.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="statements">The statements</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the statements are run</returns>
    private static async Task ExecuteAsync(string folder, string[] statements, CancellationToken cancellationToken)
    {
        var now = ProgramBenchmark.Now;
        var parameters = new Dictionary<string, object>
                         {
                             ["$samples"] = StatusDays * RunsPerDay,
                             ["$statusStart"] = StorageTime.ToNanoseconds(now.AddDays(-StatusDays)),
                             ["$unitStart"] = StorageTime.ToNanoseconds(now.AddDays(-UnitDays)),
                             ["$fiveMinuteBuckets"] = StatusDays * 288,
                             ["$hourlyBuckets"] = UnitDays * 24,
                             ["$hours"] = UnitDays * 24,
                             ["$units"] = Units,
                             ["$threads"] = ThreadsPerSnapshot,
                             ["$every"] = SnapshotEvery,
                             ["$boot"] = BatchWriter.BootId
                         };
        var connection = new SqliteConnection($"Data Source={Path.Combine(folder, StorageLimits.FileName)};Pooling=False");

        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var statement in statements)
            {
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using (transaction.ConfigureAwait(false))
                {
                    using var command = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements of this tool
                    command.CommandText = statement;
#pragma warning restore CA2100
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandTimeout = 0;

                    foreach (var (name, value) in parameters)
                    {
                        if (statement.Contains(name, StringComparison.Ordinal))
                        {
                            command.Parameters.AddWithValue(name, value);
                        }
                    }

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Times the three actions of a range, each as the median of five, and the thread action at a moment.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="days">The length of the range ending now in days</param>
    /// <param name="medians">Receives the medians</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureAsync(SqliteStore store, int days, List<(string Name, double Median)> medians, CancellationToken cancellationToken)
    {
        var now = ProgramBenchmark.Now;
        var range = new HistoryRange(now.AddDays(-days), now);
        var policy = ProgramBenchmark.CreatePolicy();
        var clock = new FixedClock(now);
        var series = new TimeSeriesService(store, policy, clock);
        var servicesReader = new ServicesReader(series, store, clock);
        var mariaDbReader = new MariaDbReader(series, store, policy, clock);
        var threadReader = new ThreadSnapshotReader(store);
        var moment = now.AddDays(-1).AddSeconds(-1);
        var services = new List<double>();
        var mariaDb = new List<double>();
        var threads = new List<double>();
        var threadsAt = new List<double>();
        ServicesTable? table = null;
        MariaDbCharts? charts = null;
        ThreadPanel? panel = null;
        ThreadPanel? panelAt = null;

        for (var read = 0; read < Reads; read++)
        {
            services.Add(await ProgramBenchmark.TimeAsync(async () => table = await servicesReader.ReadAsync(range, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            mariaDb.Add(await ProgramBenchmark.TimeAsync(async () => charts = await mariaDbReader.ReadAsync(range, ChartWidth, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            threads.Add(await ProgramBenchmark.TimeAsync(async () => panel = await threadReader.ReadAsync(range, null, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            threadsAt.Add(await ProgramBenchmark.TimeAsync(async () => panelAt = await threadReader.ReadAsync(range, moment, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }

        var label = $"{days}-day range";

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: services table {table?.Units.Rows.Count} rows, {table?.Restarts.Rows.Count} restarts listed, {table?.Notes.Count} notes ({string.Join(" | ", table?.Notes ?? [])})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: MariaDB {charts?.Notes.Count} notes, summary '{charts?.Summary}', {(charts?.Memory is null ? "no" : "a")} memory chart"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: thread panel {panel?.Stamps.Count} snapshots listed, {panel?.Threads?.Rows.Count} thread rows; at a moment {panelAt?.Threads?.Rows.Count} thread rows"));

        if (days == UnitDays)
        {
            // E12 (amended AC10): the 1-year services action at the agent's 256 units may pass the deadline, so it is printed and left out of the verdict.
            var excluded = new List<(string Name, double Median)>();

            ProgramBenchmark.Report($"{label}: services action", services, excluded);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: services action at {Units} units {excluded[0].Median:F0} ms: may pass {CriterionMilliseconds:F0} ms (E12, amended AC10)"));
        }
        else
        {
            ProgramBenchmark.Report($"{label}: services action", services, medians);
        }

        ProgramBenchmark.Report($"{label}: MariaDB action", mariaDb, medians);
        ProgramBenchmark.Report($"{label}: thread snapshot action (latest)", threads, medians);
        ProgramBenchmark.Report($"{label}: thread snapshot action (at a moment)", threadsAt, medians);
    }

    #endregion // Methods
}