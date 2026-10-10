using System.Diagnostics;
using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Measures the write throughput of the store (criterion of issue #14, <c>docs/BENCHMARKS.md</c>). The measurement
/// runs only when the environment variable <c>VANDOX_BENCHMARK</c> is set, and it never asserts a duration.
/// </summary>
[TestClass]
public class WriteThroughputTests
{
    #region Constants

    private const string EnableVariable = "VANDOX_BENCHMARK";
    private const int BatchSize = 10000;
    private const int Iterations = 5;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the test run.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Writes batches of 10,000 metric points into one database and prints the time of each one.
    /// </summary>
    /// <returns>A task that completes when the measurement is done</returns>
    [TestMethod]
    public async Task WriteThroughputOfTenThousandMetricsPerBatch()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            Assert.Inconclusive($"set {EnableVariable}=1 to run the measurement");
        }

        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var seq = 1UL;

        // Act
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var records = new DataRecord[BatchSize];

            for (var index = 0; index < BatchSize; index++)
            {
                records[index] = Samples.Metric(RecordOrigin.Agent, seq++, index % 3600, "cpu");
            }

            var batch = Samples.AgentBatch(records);
            var watch = Stopwatch.StartNew();
            var result = await store.WriteBatchAsync(batch, TestContext.CancellationToken);

            watch.Stop();

            // Assert
            Assert.AreEqual(BatchSize, result.Stored, "stored records");
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"batch {iteration + 1}: {watch.Elapsed.TotalMilliseconds:F0} ms for {BatchSize} records ({BatchSize / watch.Elapsed.TotalSeconds:F0} records/s)"));
        }
    }

    #endregion // Methods
}