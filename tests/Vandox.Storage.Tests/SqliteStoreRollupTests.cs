using System.Globalization;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the rollup fold of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreRollupTests
{
    #region Constants

    private const long FiveMinutesNs = 300_000_000_000;

    private const long HourNs = 3_600_000_000_000;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every 5-minute and hourly bucket of every series holds the minimum, mean, maximum, last value and count of all its values; the last value is the one of the latest capture time, of the higher record ID on a tie.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsMatchesComputationFromAllValues()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Samples.Base;

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(10), "cpu", 4, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(70), "cpu", 1.5, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(70), "cpu", 2.5, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(20), "cpu", 9, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddMinutes(7), "cpu", 6, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddMinutes(61), "cpu", 3, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(30), "cpu", 50, Cpu("1")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(40), "cpu", -2, Cpu("1")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddSeconds(15), "load1", 0.5),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddMinutes(12), "load1", 0.25));
        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Import, start.AddSeconds(70), "cpu", 7, Cpu("0")),
                                          RetentionSamples.Metric(RecordOrigin.Backend, start.AddMinutes(14), "load1", 1));

        var expected = await ExpectedBucketsAsync(directory.Path);

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), start.AddHours(3), TestContext.CancellationToken);
        var actual = await BucketsAsync(store, ["cpu", "load1"]);

        // Assert
        Assert.AreEqual(12, steps.Sum(step => step.Values), "every stored value is folded once");
        Assert.HasCount(10, expected, "the arranged values fall into 6 five-minute and 4 hourly buckets of 3 series");
        Assert.AreSequenceEqual(expected, actual, "each bucket equals the computation from all its values");
        Assert.Contains($"FiveMinutes|host|cpu|{Labels(directory.Path, "0")}|{start.UtcTicks}|1.5|4.8|9|7|{start.AddSeconds(70).UtcTicks}|5", actual, "the first bucket of cpu 0: mean of 4, 1.5, 2.5, 9 and 7; of the three values at 70 s the last is the one with the highest record ID");
    }

    /// <summary>
    /// A metric without labels and one with an empty label map are one series, whose buckets have empty labels.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsEmptyLabelsAndNullLabelsAreOneSeries()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(10), "load1", 1),
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(20), "load1", 3, []));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(2), TestContext.CancellationToken);

        var actual = await BucketsAsync(store, ["load1"]);

        // Assert
        Assert.AreSequenceEqual([$"FiveMinutes|host|load1||{Samples.Base.UtcTicks}|1|2|3|3|{Samples.Base.AddSeconds(20).UtcTicks}|2", $"Hour|host|load1||{Samples.Base.UtcTicks}|1|2|3|3|{Samples.Base.AddSeconds(20).UtcTicks}|2"], actual, "one series with empty labels per tier");
    }

    /// <summary>
    /// A batch and an import that add values to buckets already folded, whose raw values are still stored, correct those buckets.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsCorrectsBucketsWithRawValuesStored()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(2);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(30), "cpu", 5),
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddMinutes(20), "cpu", 7));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(10), "cpu", 1));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Import, Samples.Base.AddSeconds(50), "cpu", 12));

        var expected = await ExpectedBucketsAsync(directory.Path);

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var actual = await BucketsAsync(store, ["cpu"]);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.Values), "only the two late values are folded");
        Assert.AreSequenceEqual(expected, actual, "the corrected buckets equal the computation from all values");
        Assert.Contains($"FiveMinutes|host|cpu||{Samples.Base.UtcTicks}|1|6|12|12|{Samples.Base.AddSeconds(50).UtcTicks}|3", actual, "the first bucket after the late batch and import");
    }

    /// <summary>
    /// A late value for a bucket whose raw values were removed is merged: minimum, maximum, count and mean become exact, the last value changes only if the late value is later.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsMergesIntoBucketsWhoseRawValuesWereRemoved()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);
        var second = Samples.Base.AddMinutes(10);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(60), "cpu", 4),
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(120), "cpu", 8),
                                          RetentionSamples.Metric(RecordOrigin.Backend, second.AddSeconds(60), "cpu", 4),
                                          RetentionSamples.Metric(RecordOrigin.Backend, second.AddSeconds(120), "cpu", 8));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, now, "marker", 1));
        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var removed = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM metrics WHERE name = 'cpu'", TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Import, Samples.Base.AddSeconds(30), "cpu", 1),
                                          RetentionSamples.Metric(RecordOrigin.Import, second.AddSeconds(180), "cpu", 12));

        var firstMean = (13.0 / 3.0).ToString("R", CultureInfo.InvariantCulture);
        var hourMean = (37.0 / 6.0).ToString("R", CultureInfo.InvariantCulture);

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var actual = await BucketsAsync(store, ["cpu"]);

        // Assert
        Assert.AreEqual(0L, removed, "the raw values of both buckets were removed before the late values arrived");

        List<string> expected = [$"FiveMinutes|host|cpu||{Samples.Base.UtcTicks}|1|{firstMean}|8|8|{Samples.Base.AddSeconds(120).UtcTicks}|3", $"FiveMinutes|host|cpu||{second.UtcTicks}|4|8|12|12|{second.AddSeconds(180).UtcTicks}|3", $"Hour|host|cpu||{Samples.Base.UtcTicks}|1|{hourMean}|12|12|{second.AddSeconds(180).UtcTicks}|6"];

        Assert.AreSequenceEqual(expected, actual, "an earlier late value keeps the last value, a later one becomes it; the other aggregates cover all values seen");
    }

    /// <summary>
    /// A fold step writes only the buckets of the rows it reads: an earlier bucket is left as it is.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsTouchesOnlyBucketsOfNewRows()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var later = Samples.Base.AddHours(1);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(10), "cpu", 2),
                                          RetentionSamples.Metric(RecordOrigin.Backend, later.AddSeconds(10), "cpu", 2));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.ExecuteAsync(directory.Path, "UPDATE metric_rollups_5m SET count = 999; UPDATE metric_rollups_1h SET count = 999", TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, later.AddSeconds(20), "cpu", 4));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var untouched5m = await RetentionSamples.CountAsync(directory.Path, $"SELECT count FROM metric_rollups_5m WHERE bucket = {RetentionSamples.Ns(Samples.Base)}", TestContext.CancellationToken);
        var untouched1h = await RetentionSamples.CountAsync(directory.Path, $"SELECT count FROM metric_rollups_1h WHERE bucket = {RetentionSamples.Ns(Samples.Base)}", TestContext.CancellationToken);
        var touched5m = await RetentionSamples.CountAsync(directory.Path, $"SELECT count FROM metric_rollups_5m WHERE bucket = {RetentionSamples.Ns(later)}", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(999L, untouched5m, "the 5-minute bucket without a new row is not rewritten");
        Assert.AreEqual(999L, untouched1h, "the hourly bucket without a new row is not rewritten");
        Assert.AreEqual(1000L, touched5m, "the bucket of the new row is merged with the new value");
    }

    /// <summary>
    /// A bucket already past the period of its tier is not created, while the other tier and newer buckets are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsSkipsBucketsPastTheirTier()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = new RetentionPolicy
                     {
                         Raw = RetentionSamples.Days(1),
                         FiveMinuteRollups = RetentionSamples.Days(1),
                         HourlyRollups = RetentionSamples.Days(2),
                         Logs = RetentionSamples.Days(1),
                         Snapshots = RetentionSamples.Days(1),
                         Events = RetentionSamples.Days(3)
                     };
        var now = Samples.Base.AddDays(1.5);
        var recent = now.AddHours(-2);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(10), "cpu", 2),
                                          RetentionSamples.Metric(RecordOrigin.Backend, recent.AddSeconds(10), "cpu", 3),
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddDays(-3), "cpu", 4));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var actual = await BucketsAsync(store, ["cpu"]);

        // Assert
        Assert.AreEqual(3, steps.Sum(step => step.Values), "every value is read");

        List<string> expected = [$"FiveMinutes|host|cpu||{recent.UtcTicks}|3|3|3|3|{recent.AddSeconds(10).UtcTicks}|1", $"Hour|host|cpu||{Samples.Base.UtcTicks}|2|2|2|2|{Samples.Base.AddSeconds(10).UtcTicks}|1", $"Hour|host|cpu||{recent.UtcTicks}|3|3|3|3|{recent.AddSeconds(10).UtcTicks}|1"];

        Assert.AreSequenceEqual(expected, actual, "no 5-minute bucket older than 1 day and no hourly bucket older than 2 days");
    }

    /// <summary>
    /// A fold step stops after the row whose labels take the summed UTF-8 length of the labels read past the budget, commits its watermark, and the next step continues there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsStopsAtTheLabelByteBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var records = Enumerable.Range(0, 25).Select(index => RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(index), "wide", index, WideLabels(index))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        var lengths = await LabelLengthsAsync(directory.Path);
        var expectedFirst = RowsWithinBudget(lengths);

        // Act
        var first = await store.FoldRollupsAsync(policy, Samples.Base.AddHours(1), TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'rollup_after_id'", TestContext.CancellationToken);
        var expectedWatermark = await RetentionSamples.CountAsync(directory.Path, $"SELECT record_id FROM metrics ORDER BY record_id LIMIT 1 OFFSET {expectedFirst - 1}", TestContext.CancellationToken);
        var rest = await RetentionSamples.FoldAllAsync(store, policy, Samples.Base.AddHours(1), TestContext.CancellationToken);
        var folded = await RetentionSamples.CountAsync(directory.Path, "SELECT sum(count) FROM metric_rollups_5m WHERE name = 'wide'", TestContext.CancellationToken);

        // Assert
        Assert.IsGreaterThan(190_000, lengths[0], "a row's labels are near the wire limit, escaped to six bytes per character");
        Assert.IsLessThan(25, expectedFirst, "the budget ends the first step before all rows");
        Assert.AreEqual(expectedFirst, first.Values, "the first step reads up to and including the row that takes the labels past the budget");
        Assert.IsTrue(first.More, "the first step reports more rows");
        Assert.AreEqual(expectedWatermark, watermark, "the watermark of the first step is the last row it read");
        Assert.AreEqual(25 - expectedFirst, rest.Sum(step => step.Values), "the next steps continue after the watermark");
        Assert.AreEqual(25L, folded, "every row is folded exactly once");
    }

    /// <summary>
    /// Creates the labels of a cpu metric.
    /// </summary>
    /// <param name="cpu">The cpu label</param>
    /// <returns>The labels</returns>
    private static Dictionary<string, string> Cpu(string cpu)
    {
        return new Dictionary<string, string>
               {
                   ["cpu"] = cpu
               };
    }

    /// <summary>
    /// Creates 32 labels with keys of 128 bytes and values of 1,024 characters the JSON encoder escapes to six bytes each.
    /// </summary>
    /// <param name="index">Makes the labels of one row distinct</param>
    /// <returns>The labels</returns>
    private static Dictionary<string, string> WideLabels(int index)
    {
        var labels = new Dictionary<string, string>();

        for (var key = 0; key < 32; key++)
        {
            labels[$"k{key:D2}{new string('a', 125)}"] = new string('<', 1020) + index.ToString("D4", CultureInfo.InvariantCulture);
        }

        return labels;
    }

    /// <summary>
    /// Counts the rows the first fold step reads: up to and including the row that takes the summed lengths past the budget.
    /// </summary>
    /// <param name="lengths">The UTF-8 lengths of the stored labels in record order</param>
    /// <returns>The number of rows</returns>
    private static int RowsWithinBudget(List<long> lengths)
    {
        var sum = 0L;

        for (var index = 0; index < lengths.Count; index++)
        {
            sum += lengths[index];

            if (sum > StorageLimits.RollupStepLabelBytes)
            {
                return index + 1;
            }
        }

        return lengths.Count;
    }

    /// <summary>
    /// Reads the stored labels text of the metrics of one cpu label value, as the rollups name it.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="cpu">The cpu label</param>
    /// <returns>The labels text</returns>
    private static string Labels(string directory, string cpu)
    {
        using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT labels FROM metrics WHERE labels LIKE $pattern LIMIT 1";
        command.Parameters.AddWithValue("$pattern", $"%\"cpu\":\"{cpu}\"%");

        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Describes a bucket for comparison.
    /// </summary>
    /// <param name="bucket">The bucket</param>
    /// <returns>The description</returns>
    private static string Describe(RollupBucket bucket)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{bucket.Tier}|{bucket.Source}|{bucket.Name}|{bucket.Labels}|{bucket.Start.UtcTicks}|{bucket.Minimum:R}|{bucket.Average:R}|{bucket.Maximum:R}|{bucket.Last:R}|{bucket.LastAt.UtcTicks}|{bucket.Count}");
    }

    /// <summary>
    /// Computes the buckets of one tier.
    /// </summary>
    /// <param name="rows">The stored values</param>
    /// <param name="tier">The tier</param>
    /// <param name="width">The bucket width in nanoseconds</param>
    /// <returns>The descriptions</returns>
    private static IEnumerable<string> Expected(List<(long Id, string Source, string Name, string Labels, long At, double Value)> rows, RollupTier tier, long width)
    {
        return rows.GroupBy(row => (row.Source, row.Name, row.Labels, Bucket: row.At - (row.At % width)))
                   .Select(group =>
                           {
                               var last = group.OrderBy(row => row.At).ThenBy(row => row.Id).Last();

                               return Describe(new RollupBucket
                                               {
                                                   Tier = tier,
                                                   Source = group.Key.Source,
                                                   Name = group.Key.Name,
                                                   Labels = group.Key.Labels,
                                                   Start = StorageTime.FromNanoseconds(group.Key.Bucket),
                                                   Minimum = group.Min(row => row.Value),
                                                   Average = group.Sum(row => row.Value) / group.Count(),
                                                   Maximum = group.Max(row => row.Value),
                                                   Last = last.Value,
                                                   LastAt = StorageTime.FromNanoseconds(last.At),
                                                   Count = group.Count()
                                               });
                           });
    }

    /// <summary>
    /// Reads the UTF-8 lengths of the stored labels in record order.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the lengths</returns>
    private async Task<List<long>> LabelLengthsAsync(string directory)
    {
        var lengths = new List<long>();

        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT length(CAST(labels AS BLOB)) FROM metrics ORDER BY record_id";

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            lengths.Add(reader.GetInt64(0));
        }

        return lengths;
    }

    /// <summary>
    /// Reads the buckets of both tiers of the given metric names, sorted.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="names">The metric names</param>
    /// <returns>A task that returns the descriptions</returns>
    private async Task<List<string>> BucketsAsync(SqliteStore store, string[] names)
    {
        var lines = new List<string>();

        foreach (var tier in new[] { RollupTier.FiveMinutes, RollupTier.Hour })
        {
            foreach (var name in names)
            {
                var buckets = await store.RollupsAsync(new RollupQuery
                                                       {
                                                           Tier = tier,
                                                           Source = RetentionSamples.MetricSource,
                                                           Name = name,
                                                           From = Samples.Base.AddDays(-10),
                                                           To = Samples.Base.AddDays(10),
                                                           Limit = 1000
                                                       },
                                                       TestContext.CancellationToken);

                lines.AddRange(buckets.Select(Describe));
            }
        }

        lines.Sort(StringComparer.Ordinal);

        return lines;
    }

    /// <summary>
    /// Computes the buckets of both tiers from every stored metric value, as the rollups must hold them.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the descriptions, sorted</returns>
    private async Task<List<string>> ExpectedBucketsAsync(string directory)
    {
        var rows = new List<(long Id, string Source, string Name, string Labels, long At, double Value)>();

        await using (var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync(TestContext.CancellationToken);

            await using var command = connection.CreateCommand();

            command.CommandText = "SELECT record_id, source, name, labels, captured_at, value FROM metrics";

            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
                var labels = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), labels == "{}" ? string.Empty : labels, reader.GetInt64(4), reader.GetDouble(5)));
            }
        }

        var lines = Expected(rows, RollupTier.FiveMinutes, FiveMinutesNs).Concat(Expected(rows, RollupTier.Hour, HourNs)).ToList();

        lines.Sort(StringComparer.Ordinal);

        return lines;
    }

    #endregion // Methods
}