using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Backend.History;
using Vandox.Backend.Processes;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the reads of the processes view (feature 50, <c>docs/BENCHMARKS.md</c>): over one source with a year of hourly and 30 days of one-minute snapshots of 80 programs,
/// folded, the three chart reads of a 30-day and of a 1-year range at the step the page chooses for a 1,000-point width, and the moment read and the comparison of two points.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ProcessesViewBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--processes-view";

    private const int Reads = 5;
    private const int HourlySnapshots = 8760;
    private const int MinuteSnapshots = 43200;
    private const int ChartWidth = 1000;
    private const int MomentDaysAgo = 10;
    private const int ComparisonDaysAgo = 200;
    private const double CriterionMilliseconds = 10000;
    private const string Source = "processes";

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

            var store = await SqliteStore.OpenAsync(ProgramBenchmark.Folder(work, "view"), cancellationToken).ConfigureAwait(false);

            await using (store.ConfigureAwait(false))
            {
                await FillAsync(store, cancellationToken).ConfigureAwait(false);

                var medians = new List<(string Name, double Median)>();

                await MeasureChartsAsync(store, 30, medians, cancellationToken).ConfigureAwait(false);
                await MeasureChartsAsync(store, 365, medians, cancellationToken).ConfigureAwait(false);
                await MeasureMomentAsync(store, medians, cancellationToken).ConfigureAwait(false);

                var over = medians.Where(entry => entry.Median >= CriterionMilliseconds).Select(entry => entry.Name).ToList();

                Console.WriteLine(over.Count == 0
                                      ? string.Create(CultureInfo.InvariantCulture, $"AC10: all {medians.Count} medians are under {CriterionMilliseconds:F0} ms: criterion met")
                                      : string.Create(CultureInfo.InvariantCulture, $"AC10: {over.Count} of {medians.Count} medians reach {CriterionMilliseconds:F0} ms ({string.Join(", ", over)}): criterion NOT met"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Stores the year of hourly and the 30 days of one-minute snapshots and folds them.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the rollups exist</returns>
    private static async Task FillAsync(SqliteStore store, CancellationToken cancellationToken)
    {
        var now = ProgramBenchmark.Now;
        var watch = Stopwatch.StartNew();

        await ProgramBenchmark.WriteSnapshotsAsync(store, HourlySnapshots, now.AddDays(-365), TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
        await ProgramBenchmark.WriteSnapshotsAsync(store, MinuteSnapshots, now.AddDays(-30), TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"N4: {HourlySnapshots} hourly and {MinuteSnapshots} one-minute snapshots of 80 programs of one source stored in {watch.Elapsed.TotalMilliseconds:F0} ms"));

        watch.Restart();

        var steps = await ProgramBenchmark.TimeStepsAsync(store, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"folded in {watch.Elapsed.TotalMilliseconds:F0} ms ({steps.Count} steps)"));
    }

    /// <summary>
    /// Times the three reads of the charts of a range, each as the median of five, and the whole page read.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="days">The length of the range ending now in days</param>
    /// <param name="medians">Receives the medians</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureChartsAsync(SqliteStore store, int days, List<(string Name, double Median)> medians, CancellationToken cancellationToken)
    {
        var now = ProgramBenchmark.Now;
        var range = new HistoryRange(now.AddDays(-days), now);
        var policy = ProgramBenchmark.CreatePolicy();
        var step = SeriesLadder.Choose(range.Start, range.End, now, HistoryView.Target(ChartWidth), policy);
        var reader = new ProcessesChartReader(store, policy, new FixedClock(now));
        var label = $"{days}-day range";
        var sources = new List<double>();
        var memory = new List<double>();
        var cpu = new List<double>();
        var page = new List<double>();
        ProgramRollupResult? result = null;
        ProcessesCharts? charts = null;

        for (var read = 0; read < Reads; read++)
        {
            sources.Add(await TimeAsync(() => store.ProgramSourcesAsync(new ProgramSourceQuery
                                                                        {
                                                                            From = range.Start,
                                                                            To = range.End
                                                                        },
                                                                        cancellationToken)).ConfigureAwait(false));
            memory.Add(await TimeAsync(async () => result = await store.ProgramRollupsAsync(Query(range, step, ProgramMeasure.Memory), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            cpu.Add(await TimeAsync(() => store.ProgramRollupsAsync(Query(range, step, ProgramMeasure.Cpu), cancellationToken)).ConfigureAwait(false));
            page.Add(await TimeAsync(async () => charts = await reader.ReadAsync(null, range, ChartWidth, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}, step {step}, width {ChartWidth}: {result?.Times.Count} steps, {result?.Programs.Count} programs, charts {(charts?.Memory is null ? "missing" : "built")}"));
        Report($"{label}: source list", sources, medians);
        Report($"{label}: memory read", memory, medians);
        Report($"{label}: CPU read", cpu, medians);
        Report($"{label}: page read (ProcessesChartReader)", page, medians);
    }

    /// <summary>
    /// Times the moment read and the comparison of two points, each as the median of five.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="medians">Receives the medians</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task MeasureMomentAsync(SqliteStore store, List<(string Name, double Median)> medians, CancellationToken cancellationToken)
    {
        var now = ProgramBenchmark.Now;
        var reader = new ProcessesMomentReader(store);
        var moment = new List<double>();
        var comparison = new List<double>();
        HistoryMoment? read = null;
        ProgramComparisonView? compared = null;

        for (var index = 0; index < Reads; index++)
        {
            moment.Add(await TimeAsync(async () => read = await reader.MomentAsync(Source, now.AddDays(-MomentDaysAgo).AddMinutes(index), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
            comparison.Add(await TimeAsync(async () => compared = await reader.CompareAsync(Source, now.AddDays(-MomentDaysAgo).AddMinutes(index), now.AddDays(-ComparisonDaysAgo).AddMinutes(index), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }

        Console.WriteLine($"moment found: {read?.Process is not null}; comparison: {(compared is null ? "none" : "read")}");
        Report("moment read (10 days back)", moment, medians);
        Report("comparison of two points (10 and 200 days back)", comparison, medians);
    }

    /// <summary>
    /// Builds the query of a measure of the benchmark source.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="step">The step</param>
    /// <param name="measure">The measure</param>
    /// <returns>The query</returns>
    private static ProgramRollupQuery Query(HistoryRange range, TimeSpan step, ProgramMeasure measure)
    {
        return new ProgramRollupQuery
               {
                   Source = Source,
                   From = range.Start,
                   To = range.End,
                   Step = step,
                   Measure = measure,
                   Count = ProcessesView.Programs
               };
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
    /// Prints the five times and the median of a read and adds the median to the list.
    /// </summary>
    /// <param name="name">The name of the read</param>
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