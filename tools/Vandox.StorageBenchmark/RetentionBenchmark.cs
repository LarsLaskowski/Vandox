using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the steps of the retention job (issue #46, <c>docs/BENCHMARKS.md</c>, <i>Retention steps</i>): five batches of metric points are stored and
/// aged past the raw period by a fixed clock, behind imported and held rows below the horizon, and the fold steps and the first removal steps are timed
/// against the median batch of the same run.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class RetentionBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--retention";

    private const int Batches = 5;
    private const int Steps = 5;
    private const int KeptRows = 5000;
    private const int MaxSteps = 100000;
    private const string BootId = "0123abcd-0123-0123-0123-0123456789ab";
    private const string AgentId = "benchmark";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs the measurement in a database in a new folder below a directory and prints the times.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="size">The number of records of each batch</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    internal static async Task RunAsync(string directory, int size, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-storage-benchmark-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);
        var policy = CreatePolicy();

        Directory.CreateDirectory(work);

        try
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Console.WriteLine($"database below {directory}; {Batches} batches of {size} metric points aged 60 days (raw period 30 days), behind {KeptRows} imported and {KeptRows} held values");

            var store = await SqliteStore.OpenAsync(work, cancellationToken).ConfigureAwait(false);

            await using (store.ConfigureAwait(false))
            {
                var batchTimes = await WriteAsync(store, size, now, cancellationToken).ConfigureAwait(false);

                await FoldAsync(store, policy, now, cancellationToken).ConfigureAwait(false);

                var foldTimes = await TimeFoldAsync(store, policy, now, cancellationToken).ConfigureAwait(false);
                var removalTimes = await TimeRemovalAsync(store, policy, now, cancellationToken).ConfigureAwait(false);
                var batch = Median(batchTimes);
                var fold = Median(foldTimes);
                var removal = Median(removalTimes);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median batch of {size} records: {batch:F0} ms ({batchTimes.Count} batches)"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median fold step: {fold:F0} ms ({foldTimes.Count} steps)"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median removal step: {removal:F0} ms ({removalTimes.Count} steps)"));
                Console.WriteLine(fold <= batch && removal <= batch ? "AC6 (median fold and removal step at or below the median batch): met" : "AC6 (median fold and removal step at or below the median batch): NOT met");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Creates the default retention policy.
    /// </summary>
    /// <returns>The policy</returns>
    private static RetentionPolicy CreatePolicy()
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
    private static double Median(List<double> times)
    {
        return times.Count == 0 ? 0 : times.Order().ElementAt(times.Count / 2);
    }

    /// <summary>
    /// Writes the imported rows, the held rows and the timed batches, and stores the hold of the held rows.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="size">The number of records of each batch</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the time of each batch in milliseconds</returns>
    private static async Task<List<double>> WriteAsync(SqliteStore store, int size, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var seq = 1UL;
        var heldAt = now.AddDays(-75);

        await store.WriteBatchAsync(BuildImported(KeptRows, now.AddDays(-80), now), cancellationToken).ConfigureAwait(false);
        await store.WriteBatchAsync(BuildAgent(KeptRows, ref seq, heldAt, now), cancellationToken).ConfigureAwait(false);

        for (var batch = 1; batch <= Batches; batch++)
        {
            var content = BuildAgent(size, ref seq, now.AddDays(-60).AddSeconds((batch - 1) * (long)size), now);
            var watch = Stopwatch.StartNew();
            var result = await store.WriteBatchAsync(content, cancellationToken).ConfigureAwait(false);

            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"batch {batch}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Stored} records stored"));
        }

        var progress = await store.KillScanProgressAsync(cancellationToken).ConfigureAwait(false);

        await store.SaveKillScanAsync([
                                          new RetentionHold
                                          {
                                              Start = heldAt.AddHours(-1),
                                              End = heldAt.AddSeconds(KeptRows).AddHours(1)
                                          }
                                      ],
                                      new KillScanProgress
                                      {
                                          ScannedId = progress.HighestId,
                                          HighestId = progress.HighestId
                                      },
                                      cancellationToken)
                   .ConfigureAwait(false);

        return times;
    }

    /// <summary>
    /// Folds the first step, which holds the imported and the held values and is not counted.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the step is done</returns>
    private static async Task FoldAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await store.FoldRollupsAsync(policy, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Times the fold steps that fold the batches.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the time of each step in milliseconds</returns>
    private static async Task<List<double>> TimeFoldAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var times = new List<double>();

        for (var step = 0; step < MaxSteps; step++)
        {
            var watch = Stopwatch.StartNew();
            var result = await store.FoldRollupsAsync(policy, now, cancellationToken).ConfigureAwait(false);

            watch.Stop();

            if (result.Values > 0)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"fold step {times.Count}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Values} values into {result.Buckets} buckets"));
            }

            if (result.More == false)
            {
                break;
            }
        }

        return times;
    }

    /// <summary>
    /// Times the first removal steps, which walk past the imported and the held values before they remove expired values.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The fixed clock</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that returns the time of each step in milliseconds</returns>
    private static async Task<List<double>> TimeRemovalAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        RemovalCursor? cursor = null;

        for (var step = 1; step <= Steps; step++)
        {
            var watch = Stopwatch.StartNew();
            var result = await store.RemoveExpiredAsync(policy, now, cursor, cancellationToken).ConfigureAwait(false);

            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"removal step {step}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.MetricValues} metric values removed"));

            if (result.More == false)
            {
                break;
            }

            cursor = result.Next;
        }

        return times;
    }

    /// <summary>
    /// Builds a batch of agent metric points one second apart.
    /// </summary>
    /// <param name="size">The number of records</param>
    /// <param name="seq">The next sequence number, advanced by <paramref name="size"/></param>
    /// <param name="firstCapturedAt">The capture time of the first record</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    private static RecordBatch BuildAgent(int size, ref ulong seq, DateTimeOffset firstCapturedAt, DateTimeOffset receivedAt)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = Build(RecordOrigin.Agent, seq++, firstCapturedAt.AddSeconds(index));
        }

        return new RecordBatch
               {
                   AgentId = AgentId,
                   BootId = BootId,
                   ClockOffsetNs = 0,
                   ReceivedAt = receivedAt,
                   Records = records
               };
    }

    /// <summary>
    /// Builds a batch of imported metric points one second apart.
    /// </summary>
    /// <param name="size">The number of records</param>
    /// <param name="firstCapturedAt">The capture time of the first record</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    private static RecordBatch BuildImported(int size, DateTimeOffset firstCapturedAt, DateTimeOffset receivedAt)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = Build(RecordOrigin.Import, 0, firstCapturedAt.AddSeconds(index));
        }

        return new RecordBatch
               {
                   ReceivedAt = receivedAt,
                   Records = records
               };
    }

    /// <summary>
    /// Builds one metric point.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="seq">The sequence number, 0 for an import</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    private static DataRecord Build(string origin, ulong seq, DateTimeOffset capturedAt)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = "host",
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new MetricPoint
                          {
                              Name = "cpu",
                              Value = 1.5,
                              Unit = "percent",
                              Labels = new Dictionary<string, string>
                                       {
                                           ["cpu"] = "0",
                                           ["a"] = "b"
                                       }
                          }
               };
    }

    #endregion // Methods
}