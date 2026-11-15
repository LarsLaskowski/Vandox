using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the one-year time-series query (issue #47, <c>docs/BENCHMARKS.md</c>, <i>Time-series query</i>): a database with a year of metric values folded into
/// rollups, tracked units recorded hourly, a backup run a day, OOM kills and recorded gaps is queried for one year of eight series at 1,000 points, five times.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class QueryBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--query";

    private const string SystemSource = "system";
    private const int Runs = 5;
    private const int Target = 1000;
    private const int Days = 365;
    private const int RawDays = 30;
    private const int Units = 10;
    private const int OomKills = 50;
    private const int Gaps = 1000;
    private const int MaxSteps = 100000;
    private const int LeaseRuns = 20;
    private const string CpuName = "cpu_percent";

    #endregion // Constants

    #region Fields

    private static readonly string[] _cpuModes = ["user", "nice", SystemSource, "idle", "iowait", "irq", "softirq", "steal"];

    private static readonly (string Source, string Name)[] _singleSeries = [
                                                                               (SystemSource, "mem_available_bytes"),
                                                                               (SystemSource, "swap_used_bytes"),
                                                                               (SystemSource, "load1"),
                                                                               (SystemSource, "load5"),
                                                                               (SystemSource, "load15"),
                                                                               (SystemSource, "filesystem_used_percent"),
                                                                               ("self", "agent_rss_bytes")
                                                                           ];

    private static readonly RetentionPolicy _policy = new()
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

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Runs the measurement in a database in a new folder below a directory and prints the times.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    internal static async Task RunAsync(string directory, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-storage-benchmark-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

        Directory.CreateDirectory(work);

        try
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Console.WriteLine($"database below {directory}; {Days} days of {_singleSeries.Length + _cpuModes.Length} metric lines, {Units} units hourly, a backup run a day, {OomKills} OOM kills, {Gaps} gaps");

            var store = await SqliteStore.OpenAsync(work, cancellationToken).ConfigureAwait(false);

            await using (store.ConfigureAwait(false))
            {
                var watch = Stopwatch.StartNew();
                var written = await WriteAsync(store, now, cancellationToken).ConfigureAwait(false);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {written} records in {watch.Elapsed.TotalSeconds:F0} s"));
                watch.Restart();

                var folded = await FoldAsync(store, now, cancellationToken).ConfigureAwait(false);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"folded {folded} values in {watch.Elapsed.TotalSeconds:F0} s"));

                var service = new TimeSeriesService(store, _policy, new FixedClock(now));
                var query = new TimeSeriesQuery
                            {
                                Series = [.. _singleSeries.Select(item => new SeriesKey(item.Source, item.Name)), new SeriesKey(SystemSource, CpuName)],
                                From = now.AddDays(-Days),
                                To = now,
                                Target = Target
                            };

                var times = new List<double>();

                // The first query warms the connection and the cache and is not counted.
                var result = await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);

                for (var run = 1; run <= Runs; run++)
                {
                    watch.Restart();
                    result = await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);
                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"query {run}: {watch.Elapsed.TotalMilliseconds:F0} ms"));
                }

                var median = times.Order().ElementAt(times.Count / 2);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"result: {result.Resolution} step {result.Step}, {result.Times.Count} times, {result.Lines.Count} lines, {result.Markers.Count} markers, {result.Gaps.Count} gaps"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median query: {median:F0} ms ({times.Count} runs)"));
                Console.WriteLine(median < 1000 ? "AC10 (median query under one second): met" : "AC10 (median query under one second): NOT met");

                await MeasureBreakdownAsync(store, query, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Runs the query five more times through a wrapper that times every store read, and prints the median time and the calls of each part, then the time of the
    /// statements of the three largest reads alone and of one reader lease.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="query">The query</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the breakdown is printed</returns>
    private static async Task MeasureBreakdownAsync(SqliteStore store, TimeSeriesQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var timing = new TimingTimeSeriesReader(store);
        var service = new TimeSeriesService(timing, _policy, new FixedClock(now));
        var totals = new List<double>();
        var reads = TimingTimeSeriesReader.Parts.ToDictionary(part => part, _ => new List<double>());
        var calls = new Dictionary<string, int>();

        await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        for (var run = 1; run <= Runs; run++)
        {
            timing.Reset();

            var watch = Stopwatch.StartNew();

            await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            totals.Add(watch.Elapsed.TotalMilliseconds);

            foreach (var part in TimingTimeSeriesReader.Parts)
            {
                var (count, milliseconds) = timing.Get(part);

                reads[part].Add(milliseconds);
                calls[part] = count;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"breakdown ({Runs} runs through the timing reader), median query: {Median(totals):F0} ms"));

        var readsTotal = 0.0;

        foreach (var part in TimingTimeSeriesReader.Parts)
        {
            var partMs = Median(reads[part]);

            readsTotal += partMs;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{part}: {partMs:F1} ms, {calls[part]} calls"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"engine (total less the reads): {Median(totals) - readsTotal:F1} ms"));

        var from = StorageTime.ToNanoseconds(query.From);
        var to = StorageTime.ToNanoseconds(query.To);
        var range = new Dictionary<string, object>
                    {
                        ["$from"] = from,
                        ["$to"] = to,
                        ["$after"] = 0L,
                        ["$limit"] = (long)TimeSeriesQueries.MaxServiceLimit
                    };
        var gapRecords = new Dictionary<string, object>(range)
                         {
                             ["$from"] = StorageTime.ToNanoseconds(query.From.AddHours(-1)),
                             ["$to"] = StorageTime.ToNanoseconds(query.To.AddDays(7)),
                             ["$limit"] = (long)EventPages.PageSize
                         };

        await using (var lease = await store.LeaseReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var labels = (await ReaderQuery.ReadAsync(lease.Connection,
                                                      TimeSeriesQueries.FirstLabelsHourly,
                                                      new Dictionary<string, object>
                                                      {
                                                          ["$source"] = SystemSource,
                                                          ["$name"] = CpuName
                                                      },
                                                      reader => reader.GetString(0),
                                                      TimeSeriesQueries.Context,
                                                      cancellationToken).ConfigureAwait(false))[0];
            var buckets = new Dictionary<string, object>
                          {
                              ["$source"] = SystemSource,
                              ["$name"] = CpuName,
                              ["$labels"] = labels,
                              ["$from"] = from,
                              ["$to"] = to
                          };

            await CountAsync(lease.Connection, "cpu_percent buckets (one label set, hourly)", TimeSeriesQueries.BucketsHourly, buckets, cancellationToken).ConfigureAwait(false);
            await CountAsync(lease.Connection, "service changes", TimeSeriesQueries.ServiceChanges, range, cancellationToken).ConfigureAwait(false);
            await CountAsync(lease.Connection, "gap records", TimeSeriesQueries.GapRecords, gapRecords, cancellationToken).ConfigureAwait(false);
        }

        var leases = new List<double>();

        for (var run = 0; run < LeaseRuns; run++)
        {
            var watch = Stopwatch.StartNew();

            await using (await store.LeaseReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                watch.Stop();
            }

            leases.Add(watch.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"one lease: {Median(leases):F2} ms (median of {LeaseRuns})"));
    }

    /// <summary>
    /// Times the statement alone as <c>SELECT count(*) FROM (statement)</c> five times and prints the median.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="name">The name printed</param>
    /// <param name="statement">The statement</param>
    /// <param name="parameters">The statement parameters</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the line is printed</returns>
    private static async Task CountAsync(SqliteConnection connection, string name, string statement, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        long rows = 0;

        for (var run = 0; run < Runs; run++)
        {
            var watch = Stopwatch.StartNew();
            var counted = await ReaderQuery.ReadAsync(connection, $"SELECT count(*) FROM ({statement})", parameters, reader => reader.GetInt64(0), TimeSeriesQueries.Context, cancellationToken).ConfigureAwait(false);

            watch.Stop();
            rows = counted[0];
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"statement alone, {name}: {Median(times):F1} ms, {rows} rows"));
    }

    /// <summary>
    /// Gets the median of the times.
    /// </summary>
    /// <param name="times">The times</param>
    /// <returns>The middle time</returns>
    private static double Median(List<double> times)
    {
        return times.Order().ElementAt(times.Count / 2);
    }

    /// <summary>
    /// Writes the metric values, unit states, backup runs, kernel events and gaps in batches.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the number of records written</returns>
    private static async Task<long> WriteAsync(SqliteStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var start = now.AddDays(-Days);
        var writer = new BatchWriter(store, now);

        // Raw values of the last 30 days every minute, older values every 15 minutes: the rollups of every hour hold values either way.
        for (var at = start; at < now; at = at.AddMinutes(at < now.AddDays(-RawDays) ? 15 : 1))
        {
            foreach (var (source, name) in _singleSeries)
            {
                await writer.AddAsync(source, at, Metric(name, at, null), cancellationToken).ConfigureAwait(false);
            }

            foreach (var mode in _cpuModes)
            {
                await writer.AddAsync(SystemSource, at, Metric(CpuName, at, mode), cancellationToken).ConfigureAwait(false);
            }
        }

        for (var hour = 0; hour < Days * 24; hour++)
        {
            var at = start.AddHours(hour);

            for (var unit = 0; unit < Units; unit++)
            {
                var restarted = hour % (97 + unit) == 0;

                await writer.AddAsync("systemd",
                                      at,
                                      new ServiceState
                                      {
                                          Unit = $"unit{unit}.service",
                                          LoadState = "loaded",
                                          ActiveState = "active",
                                          SubState = "running",
                                          ActiveEnterAt = restarted ? at.AddSeconds(-30) : start.AddDays(-1).AddHours(unit),
                                          Restarts = (uint)(hour / (97 + unit))
                                      },
                                      cancellationToken).ConfigureAwait(false);
            }
        }

        for (var day = 0; day < Days; day++)
        {
            var begun = start.AddDays(day).AddHours(2);

            await writer.AddAsync("backups",
                                  begun,
                                  new BackupRun
                                  {
                                      Phase = BackupRun.PhaseStart,
                                      BootId = BatchWriter.BootId,
                                      StartedAt = begun,
                                      Pid = 1000 + day,
                                      Command = "/usr/bin/backup --nightly",
                                      Processes = 1
                                  },
                                  cancellationToken).ConfigureAwait(false);
            await writer.AddAsync("backups",
                                  begun.AddMinutes(20),
                                  new BackupRun
                                  {
                                      Phase = BackupRun.PhaseEnd,
                                      BootId = BatchWriter.BootId,
                                      StartedAt = begun,
                                      LastSeenAt = begun.AddMinutes(19),
                                      GoneBy = begun.AddMinutes(20),
                                      MaxProcesses = 2
                                  },
                                  cancellationToken).ConfigureAwait(false);
        }

        for (var kill = 0; kill < OomKills; kill++)
        {
            var at = start.AddDays(kill * 7).AddHours(5);

            await writer.AddAsync("kernel",
                                  at,
                                  new KernelEvent
                                  {
                                      Type = KernelEvent.TypeOomKill,
                                      OomKill = new OomKill
                                                {
                                                    VictimPid = 2000 + kill,
                                                    VictimCommand = "mariadbd",
                                                    BootId = BatchWriter.BootId,
                                                    KernelTimeNs = 1_000_000_000L * (kill + 1)
                                                }
                                  },
                                  cancellationToken).ConfigureAwait(false);
        }

        for (var gap = 0; gap < Gaps; gap++)
        {
            var from = start.AddHours(gap * 8).AddMinutes(7);

            await writer.AddAsync("agent",
                                  from.AddMinutes(10),
                                  new Gap
                                  {
                                      From = from,
                                      To = from.AddMinutes(10),
                                      Cause = GapCause.AgentNotRunning
                                  },
                                  cancellationToken).ConfigureAwait(false);
        }

        return await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds every value into the rollups.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the number of values folded</returns>
    private static async Task<long> FoldAsync(SqliteStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var values = 0L;

        for (var step = 0; step < MaxSteps; step++)
        {
            var result = await store.FoldRollupsAsync(_policy, now, cancellationToken).ConfigureAwait(false);

            values += result.Values;

            if (result.More == false)
            {
                break;
            }
        }

        return values;
    }

    /// <summary>
    /// Builds a metric point.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="at">The capture time</param>
    /// <param name="mode">The CPU mode label, <c>null</c> for none</param>
    /// <returns>The payload</returns>
    private static MetricPoint Metric(string name, DateTimeOffset at, string? mode)
    {
        return new MetricPoint
               {
                   Name = name,
                   Value = 50 + (25 * Math.Sin(at.ToUnixTimeSeconds() / 3600.0)),
                   Unit = "count",
                   Labels = mode is null
                                ? null
                                : new Dictionary<string, string>
                                  {
                                      ["mode"] = mode
                                  }
               };
    }

    #endregion // Methods
}