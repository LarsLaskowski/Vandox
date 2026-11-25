using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the reads of the network view (feature 51, <c>docs/BENCHMARKS.md</c>): over one source with 1,095 days of hourly buckets at the trimmed size and 30 days of
/// 5-minute buckets, written directly into the tables, the chart action (sources, states and programs) and the address action (the table of the remote addresses, with the
/// reads of the listener panel running beside it) of a 30-day, a 1-year and a 1,095-day range at the step the page chooses for a 1,000-point width, each as the median of five.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class NetworkViewBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--network-view";

    private const int Reads = 5;
    private const int ChartWidth = 1000;
    private const int HourlyDays = 1095;
    private const int FiveMinuteDays = 30;
    private const int FiveMinuteBucketsPerDay = 288;
    private const int PanelSnapshotHours = 25;
    private const double CriterionMilliseconds = 10000;

    private const string FiveMinuteRowsInsert = "INSERT INTO connection_rollups_5m (source, bucket, kind, name, seen, min_value, max_value, sum_value) "
                                                + "WITH RECURSIVE h(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM h WHERE n < $hours - 1), i(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM i WHERE n < 49) "
                                                + "SELECT 'connections', $first + h.n * 300000000000, 2, printf('a%05d.%03d', h.n, i.n), 5, 0, 1 + (i.n % 5), 5 + i.n FROM h, i "
                                                + "UNION ALL SELECT 'connections', $first + h.n * 300000000000, 0, printf('state-%02d', i.n), 5, 0, 10, 25 + i.n FROM h, i WHERE i.n < 13 "
                                                + "UNION ALL SELECT 'connections', $first + h.n * 300000000000, 1, printf('program-%02d', i.n), 5, 0, 10, 25 + i.n FROM h, i WHERE i.n < 15";

    private const string FiveMinuteTotalsInsert = "INSERT INTO connection_rollup_totals_5m (source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed, process_sum) "
                                                  + "WITH RECURSIVE h(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM h WHERE n < $hours - 1) "
                                                  + "SELECT 'connections', $first + h.n * 300000000000, 5, 0, 0, 15, 50, 0, 200 FROM h";

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

            await FillAsync(folder, cancellationToken).ConfigureAwait(false);

            var store = await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false);

            await using (store.ConfigureAwait(false))
            {
                var now = ProgramBenchmark.Now;

                await ConnectionBenchmark.WriteRegularAsync(store, 1, now.AddHours(-1), TimeSpan.FromMinutes(1), 0, cancellationToken, "all").ConfigureAwait(false);
                await ConnectionBenchmark.WriteRegularAsync(store, 1, now.AddHours(-PanelSnapshotHours), TimeSpan.FromMinutes(1), 1, cancellationToken, "all").ConfigureAwait(false);

                var counter = new CountingHistoryReader(store);

                await new NetworkListenerReader(counter).ReadAsync(ConnectionBenchmark.Source, new HistoryRange(now.AddDays(-30), now), null, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"panel read: {counter.Calls} store calls ({counter.Snapshots} snapshots decoded), comparison snapshot {(counter.Snapshots == 2 ? "read" : "NOT read")}");

                if (counter.Snapshots != 2)
                {
                    throw new InvalidOperationException("The panel reads did not reach the comparison snapshot.");
                }

                var medians = new List<(string Name, double Median)>();

                foreach (var days in new[] { 30, 365, HourlyDays })
                {
                    await MeasureAsync(store, days, medians, cancellationToken).ConfigureAwait(false);
                }

                var over = medians.Where(entry => entry.Median >= CriterionMilliseconds).Select(entry => entry.Name).ToList();

                Console.WriteLine(over.Count == 0
                                      ? string.Create(CultureInfo.InvariantCulture, $"N4: all {medians.Count} medians are under {CriterionMilliseconds:F0} ms: criterion met")
                                      : string.Create(CultureInfo.InvariantCulture, $"N4: {over.Count} of {medians.Count} medians reach {CriterionMilliseconds:F0} ms ({string.Join(", ", over)}): criterion NOT met"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Creates the schema and writes the hourly and the 5-minute buckets directly into the tables.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the buckets are written</returns>
    private static async Task FillAsync(string folder, CancellationToken cancellationToken)
    {
        await (await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);

        var now = ProgramBenchmark.Now;
        var hours = HourlyDays * 24;
        var buckets = FiveMinuteDays * FiveMinuteBucketsPerDay;
        var watch = Stopwatch.StartNew();

        await ConnectionBenchmark.ExecuteAsync(folder, [ConnectionBenchmark.RowsInsert, ConnectionBenchmark.TotalsInsert], hours, StorageTime.ToNanoseconds(now.AddDays(-HourlyDays)), cancellationToken).ConfigureAwait(false);
        await ConnectionBenchmark.ExecuteAsync(folder, [FiveMinuteRowsInsert, FiveMinuteTotalsInsert], buckets, StorageTime.ToNanoseconds(now.AddDays(-FiveMinuteDays)), cancellationToken).ConfigureAwait(false);
        watch.Stop();

        var hourly = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM connection_rollups_1h", cancellationToken).ConfigureAwait(false);
        var fiveMinutes = await ConnectionBenchmark.CountAsync(folder, "SELECT count(*) FROM connection_rollups_5m", cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N4: {hours} hourly and {buckets} 5-minute buckets written directly into the tables ({hourly} and {fiveMinutes} rows, not folded) in {watch.Elapsed.TotalMilliseconds:F0} ms"));
    }

    /// <summary>
    /// Times the chart action, and the address action beside the reads of the panel, of a range, each as the median of five.
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
        var chartReader = new NetworkChartReader(store, policy, new FixedClock(now));
        var addressReader = new NetworkAddressReader(store);
        var panelReader = new NetworkListenerReader(store);
        var charts = new List<double>();
        var addresses = new List<double>();
        var panel = new List<double>();
        NetworkCharts? chartResult = null;
        NetworkAddresses? addressResult = null;

        for (var read = 0; read < Reads; read++)
        {
            charts.Add(await TimeAsync(async () => chartResult = await chartReader.ReadAsync(ConnectionBenchmark.Source, range, ChartWidth, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));

            var step = chartResult?.Step ?? TimeSpan.FromDays(1);
            var beside = TimeAsync(() => panelReader.ReadAsync(ConnectionBenchmark.Source, range, null, cancellationToken));

            addresses.Add(await TimeAsync(async () => addressResult = await addressReader.ReadAsync(ConnectionBenchmark.Source, range, step, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            panel.Add(await beside.ConfigureAwait(false));
        }

        var label = $"{days}-day range";

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}, step {chartResult?.Step}, width {ChartWidth}: {chartResult?.States?.Times.Count} steps, {chartResult?.States?.Lines.Count} states, {chartResult?.Programs?.Lines.Count} program lines, {addressResult?.Table.Rows.Count} addresses shown"));
        Report($"{label}: chart action (sources, states, programs)", charts, medians);
        Report($"{label}: address action (beside the panel)", addresses, medians);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: panel reads beside the address action: median {ProgramBenchmark.Median(panel):F0} ms of {string.Join(", ", panel.Select(time => time.ToString("F0", CultureInfo.InvariantCulture)))}"));
    }

    /// <summary>
    /// Runs an action and returns its duration.
    /// </summary>
    /// <param name="action">The action</param>
    /// <returns>A task that returns the duration in milliseconds</returns>
    private static async Task<double> TimeAsync(Func<Task> action)
    {
        var watch = Stopwatch.StartNew();

        await action().ConfigureAwait(false);
        watch.Stop();

        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Prints the five times and the median of an action and adds the median to the list.
    /// </summary>
    /// <param name="name">The name of the action</param>
    /// <param name="times">The times in milliseconds</param>
    /// <param name="medians">Receives the median</param>
    private static void Report(string name, List<double> times, List<(string Name, double Median)> medians)
    {
        var median = ProgramBenchmark.Median(times);

        medians.Add((name, median));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: median {median:F0} ms of {string.Join(", ", times.Select(time => time.ToString("F0", CultureInfo.InvariantCulture)))}"));
    }

    #endregion // Methods
}