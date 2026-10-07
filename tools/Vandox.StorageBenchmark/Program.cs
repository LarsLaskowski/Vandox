using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures how long the storage layer needs to write batches of metric points in one transaction each (criterion of issue #14,
/// <c>docs/BENCHMARKS.md</c>). It needs only the .NET runtime, so it runs on the reference host (a NAS); it asserts nothing.
/// </summary>
[ExcludeFromCodeCoverage]
public static class Program
{
    #region Constants

    private const int DefaultBatches = 5;
    private const int DefaultSize = 10000;
    private const int UsageError = 2;
    private const int WarmupSize = 1000;
    private const int ProbeRounds = 15;
    private const double CriterionMilliseconds = 1000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs the measurement.
    /// </summary>
    /// <param name="args">The directory for the temporary database, then optionally <c>--batches N</c>, <c>--size N</c> and <c>--warmup</c></param>
    /// <returns>The exit code: 0 on success, 1 on a failure, 2 on a usage error</returns>
    public static async Task<int> Main(string[] args)
    {
        if (TryParse(args, out var directory, out var batches, out var size, out var warmup))
        {
            using var stop = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
                                      {
                                          eventArgs.Cancel = true;
                                          stop.Cancel();
                                      };

            try
            {
                await RunAsync(directory, batches, size, warmup, stop.Token).ConfigureAwait(false);

                return 0;
            }
            catch (OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("interrupted").ConfigureAwait(false);

                return 1;
            }
            catch (Exception exception) when (exception is IOException or StoreException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync($"failed: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);

                return 1;
            }
        }

        await Console.Error.WriteLineAsync("usage: Vandox.StorageBenchmark <existing directory on the volume to measure> [--batches N] [--size N] [--warmup]").ConfigureAwait(false);

        return UsageError;
    }

    /// <summary>
    /// Reads the arguments.
    /// </summary>
    /// <param name="args">The arguments</param>
    /// <param name="directory">The directory</param>
    /// <param name="batches">The number of batches</param>
    /// <param name="size">The number of records per batch</param>
    /// <param name="warmup">Whether a small batch that is not counted runs first</param>
    /// <returns><c>true</c> when the arguments are valid</returns>
    private static bool TryParse(string[] args, out string directory, out int batches, out int size, out bool warmup)
    {
        directory = args.Length > 0 ? args[0] : string.Empty;
        batches = DefaultBatches;
        size = DefaultSize;
        warmup = false;

        var index = 1;

        while (index < args.Length)
        {
            if (args[index] == "--warmup")
            {
                warmup = true;
                index++;

                continue;
            }

            var valid = index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

            if (valid && args[index] == "--batches")
            {
                batches = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            }
            else if (valid && args[index] == "--size")
            {
                size = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            }
            else
            {
                return false;
            }

            index += 2;
        }

        return directory.Length > 0 && Directory.Exists(directory) && size <= StorageLimits.MaxBatchRecords;
    }

    /// <summary>
    /// Writes the batches into a database in a new folder below <paramref name="directory"/> and prints the times.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="batches">The number of batches</param>
    /// <param name="size">The number of records per batch</param>
    /// <param name="warmup">Whether a small batch that is not counted runs first</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    private static async Task RunAsync(string directory, int batches, int size, bool warmup, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-storage-benchmark-{Guid.NewGuid():N}");
        var times = new List<double>();

        Directory.CreateDirectory(work);

        try
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Console.WriteLine($"database below {directory}; {batches} batches of {size} metric points, one transaction each");

            var flush = await ProbeFlushAsync(work, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"flush to disk of a 4 KiB write on this volume: median {flush:F1} ms (a transaction waits for at least one such flush)"));

            await using (var store = await SqliteStore.OpenAsync(work, cancellationToken).ConfigureAwait(false))
            {
                var seq = 1UL;

                if (warmup)
                {
                    var small = Build(Math.Min(WarmupSize, size), ref seq);
                    var first = Stopwatch.StartNew();

                    await store.WriteBatchAsync(small, cancellationToken).ConfigureAwait(false);
                    first.Stop();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"warm-up batch of {small.Records.Count} records: {first.Elapsed.TotalMilliseconds:F0} ms (not counted)"));
                }

                for (var batch = 1; batch <= batches; batch++)
                {
                    var content = Build(size, ref seq);
                    var watch = Stopwatch.StartNew();
                    var result = await store.WriteBatchAsync(content, cancellationToken).ConfigureAwait(false);

                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"batch {batch}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Stored} records stored ({size / watch.Elapsed.TotalSeconds:F0} records/s)"));
                }
            }

            var sorted = times.Order().ToList();

            var median = sorted[sorted.Count / 2];

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"first counted batch{(warmup ? " (after the warm-up)" : " (fresh database)")}: {times[0]:F0} ms; fastest: {sorted[0]:F0} ms; median: {median:F0} ms; slowest: {sorted[^1]:F0} ms"));

            if (batches == DefaultBatches && size == DefaultSize)
            {
                Console.WriteLine(median < CriterionMilliseconds ? "criterion of issue #14 (median of five batches under 1000 ms): met" : "criterion of issue #14 (median of five batches under 1000 ms): NOT met");
            }
            else
            {
                Console.WriteLine($"no verdict: the criterion is the median of {DefaultBatches} batches of {DefaultSize} records");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Measures how long a 4 KiB write takes to reach the disk on the volume, which shows how much of a transaction is waiting for it.
    /// </summary>
    /// <param name="work">The working folder on the volume</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>The median time in milliseconds</returns>
    private static async Task<double> ProbeFlushAsync(string work, CancellationToken cancellationToken)
    {
        var path = Path.Combine(work, "flush-probe");
        var block = new byte[4096];
        var times = new List<double>();

        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None))
        {
            for (var index = 0; index < ProbeRounds; index++)
            {
                var watch = Stopwatch.StartNew();

                await file.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                file.Flush(true);
                watch.Stop();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        File.Delete(path);

        return times.Order().ElementAt(times.Count / 2);
    }

    /// <summary>
    /// Builds a batch of agent metric points.
    /// </summary>
    /// <param name="size">The number of records</param>
    /// <param name="seq">The next sequence number, advanced by <paramref name="size"/></param>
    /// <returns>The batch</returns>
    private static RecordBatch Build(int size, ref ulong seq)
    {
        var captured = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = new DataRecord
                             {
                                 Origin = RecordOrigin.Agent,
                                 Source = "host",
                                 Seq = seq++,
                                 CapturedAt = captured.AddSeconds(index % 3600),
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

        return new RecordBatch
               {
                   AgentId = "benchmark",
                   BootId = "0123abcd-0123-0123-0123-0123456789ab",
                   ClockOffsetNs = 0,
                   ReceivedAt = captured.AddMinutes(1),
                   Records = records
               };
    }

    #endregion // Methods
}