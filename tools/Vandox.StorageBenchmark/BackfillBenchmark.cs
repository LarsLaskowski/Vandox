using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the write of one batch of metric points that fills an open sequence gap and its time gap (issue #41, <c>docs/BENCHMARKS.md</c>):
/// a small batch is stored, a small batch far above it opens the gap, and the measured batch brings every missing number and a sample
/// at every second of the missing time, oldest first, as an agent's backfill does.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class BackfillBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--backfill";

    private const int Rounds = 5;
    private const int Edge = 10;
    private const string BootId = "0123abcd-0123-0123-0123-0123456789ab";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs the measurement in a database in a new folder below a directory and prints the times.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="size">The number of records of the measured batch</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    internal static async Task RunAsync(string directory, int size, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-storage-benchmark-{Guid.NewGuid():N}");
        var times = new List<double>();
        var start = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        Directory.CreateDirectory(work);

        try
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Console.WriteLine($"database below {directory}; {Rounds} rounds, each of an agent with a gap of {size} numbers and {size + (2 * Edge)} seconds, filled by one batch of {size} metric points");

            await using (var store = await SqliteStore.OpenAsync(work, cancellationToken).ConfigureAwait(false))
            {
                for (var round = 1; round <= Rounds; round++)
                {
                    var agent = string.Create(CultureInfo.InvariantCulture, $"backfill-{round}");
                    var above = (ulong)(size + Edge);

                    await store.WriteBatchAsync(Build(agent, 1, Edge, start, start.AddSeconds(Edge)), cancellationToken).ConfigureAwait(false);

                    var opening = await store.WriteBatchAsync(Build(agent, above + 1, Edge, start.AddSeconds(size + Edge), start.AddHours(3)), cancellationToken).ConfigureAwait(false);
                    var content = Build(agent, Edge + 1, size, start.AddSeconds(Edge), start.AddHours(3).AddMinutes(1));
                    var watch = Stopwatch.StartNew();
                    var result = await store.WriteBatchAsync(content, cancellationToken).ConfigureAwait(false);

                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"round {round}: {watch.Elapsed.TotalMilliseconds:F0} ms, {result.Stored} records stored ({size / watch.Elapsed.TotalSeconds:F0} records/s); gaps opened by the jump {opening.Timeline?.Opened}, closed by the fill {result.Timeline?.Closed}, open after it {result.Timeline?.Open}"));
                }
            }

            var sorted = times.Order().ToList();

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"first round: {times[0]:F0} ms; fastest: {sorted[0]:F0} ms; median: {sorted[sorted.Count / 2]:F0} ms; slowest: {sorted[^1]:F0} ms"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Builds a batch of agent metric points one second apart.
    /// </summary>
    /// <param name="agent">The agent ID</param>
    /// <param name="firstSeq">The number of the first record</param>
    /// <param name="size">The number of records</param>
    /// <param name="firstCapturedAt">The capture time of the first record</param>
    /// <param name="receivedAt">The receive time</param>
    /// <returns>The batch</returns>
    private static RecordBatch Build(string agent, ulong firstSeq, int size, DateTimeOffset firstCapturedAt, DateTimeOffset receivedAt)
    {
        var records = new DataRecord[size];

        for (var index = 0; index < size; index++)
        {
            records[index] = new DataRecord
                             {
                                 Origin = RecordOrigin.Agent,
                                 Source = "host",
                                 Seq = firstSeq + (ulong)index,
                                 CapturedAt = firstCapturedAt.AddSeconds(index),
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
                   AgentId = agent,
                   BootId = BootId,
                   ClockOffsetNs = 0,
                   ReceivedAt = receivedAt,
                   Records = records
               };
    }

    #endregion // Methods
}