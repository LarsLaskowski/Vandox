using System.Globalization;
using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the fold of connection snapshots into the per-state, per-program and per-address rollups of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreConnectionRollupTests
{
    #region Constants

    private const string Nginx = "nginx";

    private const string Established = "ESTABLISHED";

    private const string FirstAddress = "10.0.0.1";

    private const string SecondAddress = "10.0.0.2";

    private const string LowAddress = "10.0.200.1";

    private const string Filler = "filler";

    private const string Forged = "forged";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every decodable connection snapshot is folded into the 5-minute and the hourly bucket of its source: per state, program and address the number of snapshots naming it and its minimum, maximum and sum, and per bucket
    /// the snapshots, the incomplete and capped snapshots, the stored programs and addresses, the trimmed addresses and the sum over all processes; a second fold changes nothing and a later snapshot is folded alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsFoldsEachConnectionSnapshotOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var late = Samples.Base.AddMinutes(20);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(2)], [ConnectionSamples.Process(1, Nginx, 2)], [ConnectionSamples.Remote(FirstAddress, 3)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(3),
                                                                     ConnectionSamples.Source,
                                                                     [ConnectionSamples.Established(4)],
                                                                     [ConnectionSamples.Process(1, Nginx, 4)],
                                                                     [ConnectionSamples.Remote(FirstAddress, 5), ConnectionSamples.Remote(SecondAddress, 1)]),
                                          ConnectionSamples.Snapshot(late, ConnectionSamples.Source, [ConnectionSamples.Established(1)]));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var rows5m = await RowsAsync(directory, ConnectionSamples.Rows5m);
        var rows1h = await RowsAsync(directory, ConnectionSamples.Rows1h);
        var totals5m = await RowsAsync(directory, ConnectionSamples.Totals5m);
        var totals1h = await RowsAsync(directory, ConnectionSamples.Totals1h);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ConnectionSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);
        var repeat = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var afterRepeat = await RowsAsync(directory, ConnectionSamples.Rows5m);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddMinutes(4), ConnectionSamples.Source, [ConnectionSamples.Established(1)]));

        var added = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var totalsAfter = await RowsAsync(directory, ConnectionSamples.Totals5m);

        // Assert
        Assert.AreEqual(3, steps.Sum(step => step.ConnectionSnapshots), "every snapshot is folded once");
        Assert.AreEqual(0, steps.Sum(step => step.SkippedConnectionSnapshots), "no snapshot is skipped");
        Assert.AreEqual(3, steps.Sum(step => step.ConnectionBuckets), "two 5-minute buckets and one hourly bucket are written");
        Assert.AreEqual(0, steps.Sum(step => step.TrimmedAddresses), "no bucket holds more addresses than its kept number");
        Assert.AreSequenceEqual([
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.StateKind, Established, "2|2|4|6"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.ProgramKind, Nginx, "2|2|4|6"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.AddressKind, FirstAddress, "2|3|5|8"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.AddressKind, SecondAddress, "1|1|1|1"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, late, ConnectionSamples.StateKind, Established, "1|1|1|1")
                                ],
                                rows5m,
                                "the 5-minute rows per state, program and address");
        Assert.AreSequenceEqual([
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.StateKind, Established, "3|1|4|7"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.ProgramKind, Nginx, "2|2|4|6"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.AddressKind, FirstAddress, "2|3|5|8"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.AddressKind, SecondAddress, "1|1|1|1")
                                ],
                                rows1h,
                                "the hourly rows hold all three snapshots");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "2|0|0|1|2|0|6"), ConnectionSamples.TotalsRow(ConnectionSamples.Source, late, "1|0|0|0|0|0|0")], totals5m, "the 5-minute totals");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "3|0|0|1|2|0|6")], totals1h, "the hourly total");
        Assert.AreEqual(highest, watermark, "the watermark is the last snapshot read");
        Assert.AreEqual(0, repeat.Sum(step => step.ConnectionSnapshots), "the repeated fold reads no snapshot");
        Assert.AreSequenceEqual(rows5m, afterRepeat, "the repeated fold leaves the rows as they are");
        Assert.AreEqual(1, added.Sum(step => step.ConnectionSnapshots), "the later snapshot is folded alone");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "3|0|0|1|2|0|6"), ConnectionSamples.TotalsRow(ConnectionSamples.Source, late, "1|0|0|0|0|0|0")], totalsAfter, "each snapshot counts once in the total");
    }

    /// <summary>
    /// Imported connection snapshots, which carry no <c>aggregates_incomplete</c> even when they are not complete, are folded into the buckets of their source beside those of the other origins, and count as complete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsFoldsImportedConnectionSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Imported(Samples.Base.AddSeconds(10), "legacy-monitor", ConnectionSamples.Established(2)),
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), "legacy-monitor", [ConnectionSamples.Established(4)]));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var rows = await RowsAsync(directory, ConnectionSamples.Rows5m);
        var totals = await RowsAsync(directory, ConnectionSamples.Totals1h);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.ConnectionSnapshots), "the imported and the backend snapshot are folded");
        Assert.AreSequenceEqual([ConnectionSamples.Row("legacy-monitor", Samples.Base, ConnectionSamples.StateKind, Established, "2|2|4|6")], rows, "one row holds both origins");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow("legacy-monitor", Samples.Base, "2|0|0|0|0|0|0")], totals, "the imported snapshot of an incomplete import does not count as incomplete");
    }

    /// <summary>
    /// The two connection snapshots of the golden wire batch that list aggregates, stored as the wire carries them, fold per source into states, the program and the addresses, with an IPv4-mapped address in its text form and
    /// both counted as incomplete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC2")]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsFoldsWireSampleConnectionSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var captured = new DateTimeOffset(2026, 3, 1, 12, 30, 15, TimeSpan.Zero);
        var bucket = new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);
        var hour = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var inserted = 0;

        foreach (var line in await File.ReadAllLinesAsync(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"), TestContext.CancellationToken))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("kind", out var kind) == false || kind.GetString() != RecordKind.ConnectionSnapshot || root.GetProperty("data").TryGetProperty("remotes", out _) == false)
            {
                continue;
            }

            await ConnectionSamples.InsertRawAsync(directory.Path, root.GetProperty("source").GetString()!, captured, root.GetProperty("data").GetRawText(), TestContext.CancellationToken);
            inserted++;
        }

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), captured.AddHours(3), TestContext.CancellationToken);
        var rows = await RowsAsync(directory, ConnectionSamples.Rows5m);
        var totals = await RowsAsync(directory, ConnectionSamples.Totals1h);

        // Assert
        Assert.AreEqual(2, inserted, "the golden batch holds two connection snapshots with remotes");
        Assert.AreEqual(2, steps.Sum(step => step.ConnectionSnapshots), "both snapshots are folded");
        Assert.AreEqual(0, steps.Sum(step => step.SkippedConnectionSnapshots), "the wire sample decodes");
        Assert.AreSequenceEqual([
                                    ConnectionSamples.Row(ConnectionSamples.Source, bucket, ConnectionSamples.StateKind, "UDP", "1|1|1|1"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, bucket, ConnectionSamples.AddressKind, "::ffff:192.0.2.7", "1|1|1|1"),
                                    ConnectionSamples.Row("lsof", bucket, ConnectionSamples.StateKind, Established, "1|3|3|3"),
                                    ConnectionSamples.Row("lsof", bucket, ConnectionSamples.ProgramKind, Nginx, "1|2|2|2"),
                                    ConnectionSamples.Row("lsof", bucket, ConnectionSamples.AddressKind, "2001:db8::1", "1|4|4|4"),
                                    ConnectionSamples.Row("lsof", bucket, ConnectionSamples.AddressKind, "::ffff:192.0.2.7", "1|1|1|1")
                                ],
                                rows,
                                "the rows of both sources, the mapped address kept in its text form");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, hour, "1|1|0|0|1|0|0"), ConnectionSamples.TotalsRow("lsof", hour, "1|1|0|1|2|0|2")], totals, "both snapshots carry aggregates_incomplete");
    }

    /// <summary>
    /// A stored connection snapshot whose data does not decode or validate is skipped and counted, the position moves past it, and the snapshots around it are folded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsSkipsUndecodableConnectionSnapshotAndPassesIt()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var at = Samples.Base.AddSeconds(30);
        string?[] texts = [
                              "not json",
                              null,
                              "{\"states\":\"x\"}",
                              "{\"remotes\":[{\"addr\":\"nope\",\"count\":1}]}",
                              "{\"states\":[{\"proto\":\"tcp\",\"state\":\"ESTABLISHED\",\"count\":0}]}",
                              "{\"processes\":[{\"pid\":0,\"command\":\"x\",\"count\":1}]}"
                          ];
        byte[][] invalid = [ConnectionSamples.JsonWithCommandBytes(0xFF), ConnectionSamples.JsonWithCommandBytes(0xC3)];

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(1)]));

        foreach (var text in texts)
        {
            await ConnectionSamples.InsertRawAsync(directory.Path, Forged, at, text, TestContext.CancellationToken);
        }

        foreach (var bytes in invalid)
        {
            await ConnectionSamples.InsertBytesAsync(directory.Path, Forged, at, bytes, TestContext.CancellationToken);
        }

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(50), ConnectionSamples.Source, [ConnectionSamples.Established(3)]));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var totals = await RowsAsync(directory, ConnectionSamples.Totals5m);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ConnectionSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.ConnectionSnapshots), "the two decodable snapshots are folded");
        Assert.AreEqual(8, steps.Sum(step => step.SkippedConnectionSnapshots), "the eight undecodable snapshots are counted");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "2|0|0|0|0|0|0")], totals, "only the decodable snapshots reach the buckets");
        Assert.AreEqual(highest, watermark, "the position moves past the skipped rows to the last row read");
        Assert.IsFalse(steps[^1].More, "nothing is left to fold");
    }

    /// <summary>
    /// A snapshot that arrives after its bucket's snapshots were removed is merged into the existing bucket: the extremes, the sums and the counts become exact, and the late snapshot is removed with its period like the others.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsMergesLateConnectionSnapshotIntoBucket()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(3)], remotes: [ConnectionSamples.Remote(FirstAddress, 1)]));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, now, "marker", 1));
        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var removed = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM records WHERE kind = 'connection_snapshot'", TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source, [ConnectionSamples.Established(5)], remotes: [ConnectionSamples.Remote(SecondAddress, 2)]));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, now, "marker", 2));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var totals = await RowsAsync(directory, ConnectionSamples.Totals1h);

        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var left = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM records WHERE kind = 'connection_snapshot'", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, removed, "the first snapshot was removed after its fold");
        Assert.AreSequenceEqual(["0|ESTABLISHED|2|3|5|8", "2|10.0.0.1|1|1|1|1", "2|10.0.0.2|1|2|2|2"], rows, "the late snapshot is merged into the bucket");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "2|0|0|0|2|0|0")], totals, "the total counts both snapshots");
        Assert.AreEqual(0L, left, "the late snapshot is removed after its fold");
    }

    /// <summary>
    /// A bucket already past the period of its tier is not created, while the other tier and newer buckets are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsCreatesNoConnectionBucketPastItsPeriod()
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
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(recent.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(3)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddDays(-3), ConnectionSamples.Source, [ConnectionSamples.Established(5)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var rows5m = await RowsAsync(directory, ConnectionSamples.Rows5m);
        var rows1h = await RowsAsync(directory, ConnectionSamples.Rows1h);
        var totals5m = await RowsAsync(directory, ConnectionSamples.Totals5m);
        var totals1h = await RowsAsync(directory, ConnectionSamples.Totals1h);

        // Assert
        Assert.AreSequenceEqual([ConnectionSamples.Row(ConnectionSamples.Source, ProgramSamples.FiveMinutes(recent), ConnectionSamples.StateKind, Established, "1|3|3|3")], rows5m, "no 5-minute row older than 1 day");
        Assert.AreSequenceEqual([
                                    ConnectionSamples.Row(ConnectionSamples.Source, Samples.Base, ConnectionSamples.StateKind, Established, "1|1|1|1"),
                                    ConnectionSamples.Row(ConnectionSamples.Source, ProgramSamples.Hour(recent), ConnectionSamples.StateKind, Established, "1|3|3|3")
                                ],
                                rows1h,
                                "no hourly row older than 2 days");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, ProgramSamples.FiveMinutes(recent), "1|0|0|0|0|0|0")], totals5m, "no 5-minute total older than 1 day");
        Assert.AreSequenceEqual([
                                    ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "1|0|0|0|0|0|0"),
                                    ConnectionSamples.TotalsRow(ConnectionSamples.Source, ProgramSamples.Hour(recent), "1|0|0|0|0|0|0")
                                ],
                                totals1h,
                                "no hourly total older than 2 days");
    }

    /// <summary>
    /// A fold step reads at most 500 connection snapshots, commits its position and reports more; the next step continues there and reports nothing more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsStopsConnectionPartAtStepBound()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var rows = StorageLimits.ConnectionRollupStepRows + 1;
        var records = Enumerable.Range(0, rows).Select(index => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(index), ConnectionSamples.Source, [ConnectionSamples.Established(1)])).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ConnectionSamples.Watermark, TestContext.CancellationToken);
        var expectedWatermark = await RetentionSamples.CountAsync(directory.Path, $"SELECT id FROM records WHERE kind = 'connection_snapshot' ORDER BY id LIMIT 1 OFFSET {StorageLimits.ConnectionRollupStepRows - 1}", TestContext.CancellationToken);
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var folded = await RetentionSamples.CountAsync(directory.Path, "SELECT coalesce(sum(snapshots), 0) FROM connection_rollup_totals_1h", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StorageLimits.ConnectionRollupStepRows, first.ConnectionSnapshots, "the first step reads 500 snapshots");
        Assert.IsTrue(first.More, "the first step reports more rows");
        Assert.AreEqual(expectedWatermark, watermark, "the position of the first step is the last row it read");
        Assert.AreEqual(1, second.ConnectionSnapshots, "the second step reads the rest");
        Assert.IsFalse(second.More, "nothing is left after the second step");
        Assert.AreEqual((long)rows, folded, "every snapshot is folded exactly once");
    }

    /// <summary>
    /// A fold step stops after the connection snapshot that takes the stored bytes read past 2 MiB, commits its position, and the next step continues there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsStopsConnectionPartAtByteBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var wide = Enumerable.Range(0, 50).Select(index => ConnectionSamples.Process(index + 1, string.Create(CultureInfo.InvariantCulture, $"{index:D2}{new string('w', 998)}"), 1)).ToArray();
        var records = Enumerable.Range(0, 45).Select(index => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(index), ConnectionSamples.Source, processes: wide)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        var lengths = await LengthsAsync(directory.Path);
        var expectedFirst = RowsWithinBudget(lengths);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ConnectionSamples.Watermark, TestContext.CancellationToken);
        var expectedWatermark = await RetentionSamples.CountAsync(directory.Path, $"SELECT id FROM records WHERE kind = 'connection_snapshot' ORDER BY id LIMIT 1 OFFSET {expectedFirst - 1}", TestContext.CancellationToken);
        var rest = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var folded = await RetentionSamples.CountAsync(directory.Path, "SELECT coalesce(sum(snapshots), 0) FROM connection_rollup_totals_1h", TestContext.CancellationToken);

        // Assert
        Assert.IsGreaterThan(40_000, lengths[0], "a snapshot is tens of kilobytes");
        Assert.IsLessThan(45, expectedFirst, "the byte budget ends the first step before all rows");
        Assert.AreEqual(expectedFirst, first.ConnectionSnapshots, "the first step reads up to and including the snapshot that takes the bytes past the budget");
        Assert.IsTrue(first.More, "the first step reports more rows");
        Assert.AreEqual(expectedWatermark, watermark, "the position of the first step is the last row it read");
        Assert.AreEqual(45 - expectedFirst, rest.Sum(step => step.ConnectionSnapshots), "the next steps continue after the position");
        Assert.AreEqual(45L, folded, "every snapshot is folded exactly once");
    }

    /// <summary>
    /// The TCP states of the protocols <c>tcp</c> and <c>tcp6</c> are added under the state, and every <c>udp</c> and <c>udp6</c> entry under <c>UDP</c>, whatever state it carries or lacks.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsMergesTcpAndTcp6StatesAndUdpAsUdp()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10),
                                                                     ConnectionSamples.Source,
                                                                     [
                                                                         ConnectionSamples.State(Proto.Tcp, Established, 3),
                                                                         ConnectionSamples.State(Proto.Tcp6, Established, 4),
                                                                         ConnectionSamples.State(Proto.Tcp, "TIME_WAIT", 1),
                                                                         ConnectionSamples.State(Proto.Udp, string.Empty, 2),
                                                                         ConnectionSamples.State(Proto.Udp6, Established, 5)
                                                                     ]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows5m = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var rows1h = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.HourlyTier, "1 = 1"));

        // Assert
        Assert.AreSequenceEqual(["0|ESTABLISHED|1|7|7|7", "0|TIME_WAIT|1|1|1|1", "0|UDP|1|7|7|7"], rows5m, "tcp and tcp6 are added under their state, udp and udp6 under UDP");
        Assert.AreSequenceEqual(rows5m, rows1h, "the hourly tier holds the same states");
    }

    /// <summary>
    /// A state a snapshot does not list is not stored for that snapshot: the row counts only the snapshots that name it, so that its sum over the snapshots of the bucket divides to the average and the averages of all
    /// states add up to the average of every counted socket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsCountsMissingStateAsZero()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(4), ConnectionSamples.State(Proto.Tcp, "TIME_WAIT", 2)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source, [ConnectionSamples.Established(6)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));
        var sockets = await RetentionSamples.CountAsync(directory.Path, "SELECT CAST(coalesce(sum(sum_value), 0) AS INTEGER) FROM connection_rollups_5m WHERE kind = 0", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["0|ESTABLISHED|2|4|6|10", "0|TIME_WAIT|1|2|2|2"], rows, "a state counts only the snapshots that name it; the read supplies the zero");
        Assert.AreSequenceEqual(["2|0|0|0|0|0|0"], totals, "the bucket counts both snapshots");
        Assert.AreEqual(12L, sockets, "the state sums add up to the sockets of both snapshots, so their averages add up to the average of every counted socket");
    }

    /// <summary>
    /// A state, a program and an address that a snapshot lists twice are added up within that snapshot and count as one snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsAddsDuplicateEntriesWithinSnapshot()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10),
                                                                     ConnectionSamples.Source,
                                                                     [ConnectionSamples.Established(3), ConnectionSamples.Established(4)],
                                                                     [ConnectionSamples.Process(1, Nginx, 2), ConnectionSamples.Process(2, Nginx, 3)],
                                                                     [ConnectionSamples.Remote(FirstAddress, 2), ConnectionSamples.Remote(FirstAddress, 3)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.HourlyTier, "1 = 1"));

        // Assert
        Assert.AreSequenceEqual(["0|ESTABLISHED|1|7|7|7", "1|nginx|1|5|5|5", "2|10.0.0.1|1|5|5|5"], rows, "each duplicate is added up within its snapshot, which counts once");
    }

    /// <summary>
    /// A program counts the sum of the counts of the processes with its command in a snapshot, and its row holds the minimum, maximum and sum of those sums over the snapshots that name it; the bucket's process sum covers all
    /// processes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsSumsProcessCountsPerProgram()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, processes: [ConnectionSamples.Process(1, Nginx, 2), ConnectionSamples.Process(2, Nginx, 3), ConnectionSamples.Process(3, "php", 4)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source, processes: [ConnectionSamples.Process(1, Nginx, 1)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));

        // Assert
        Assert.AreSequenceEqual(["1|nginx|2|1|5|6", "1|php|1|4|4|4"], rows, "the sums of the processes per program and snapshot, minimum, maximum and sum");
        Assert.AreSequenceEqual(["2|0|0|2|0|0|10"], totals, "the total holds both snapshots, two programs and the sum of all process counts");
    }

    /// <summary>
    /// A snapshot that lists more than 50 programs folds only the 50 with the highest sum, ties broken by program name in ordinal order; the cut programs stay in the bucket's process sum.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsCutsProgramsToFiftyPerSnapshot()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var processes = Enumerable.Range(1, 60).Select(index => ConnectionSamples.Process(index, string.Create(CultureInfo.InvariantCulture, $"p{index:D2}"), index >= 59 ? 5u : 1u)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, processes: processes));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var names = await RowsAsync(directory, "SELECT name FROM connection_rollups_5m WHERE kind = 1 ORDER BY name");
        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));

        // Assert
        Assert.HasCount(50, names, "fifty programs are folded");
        Assert.AreEqual("p01", names[0], "the first by name stays");
        Assert.Contains("p48", names, "the 48th by name stays among the programs of count 1");
        Assert.DoesNotContain("p49", names, "the ties past the 48th by name are cut");
        Assert.DoesNotContain("p58", names, "the last tie is cut");
        Assert.Contains("p59", names, "the highest sums stay");
        Assert.Contains("p60", names, "the highest sums stay");
        Assert.AreSequenceEqual(["1|0|0|50|0|0|68"], totals, "the total holds 50 programs and the sum of all 60 processes");
    }

    /// <summary>
    /// A bucket stores at most 500 program names: the first 500 in fold order (record ID, then name in ordinal order) stay named, a later name counts in the total only, a named program merges in later steps, and a full bucket
    /// takes no name in a later step.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsCapsProgramsAtFiveHundredPerBucket()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var records = Enumerable.Range(0, 10).Select(snapshot => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(snapshot), ConnectionSamples.Source, processes: [.. Enumerable.Range((snapshot * 50) + 1, snapshot == 9 ? 49 : 50).Select(Named)])).ToList();

        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, processes: [ConnectionSamples.Process(1, "zz", 1), ConnectionSamples.Process(2, "aa", 1), ConnectionSamples.Process(3, "n001", 1)]));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var namedAfterFirst = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_5m WHERE kind = 1", TestContext.CancellationToken);
        var firstRows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 1 AND name IN ('aa', 'n001', 'n499', 'zz')"));
        var firstTotals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(11), ConnectionSamples.Source, processes: [ConnectionSamples.Process(1, "n002", 1), ConnectionSamples.Process(2, "bb", 1)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var namedAfterSecond = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_5m WHERE kind = 1", TestContext.CancellationToken);
        var secondRows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 1 AND name IN ('bb', 'n002')"));
        var secondTotals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));

        // Assert
        Assert.AreEqual(500L, namedAfterFirst, "the bucket names 500 programs");
        Assert.AreSequenceEqual(["1|aa|1|1|1|1", "1|n001|2|1|1|2", "1|n499|1|1|1|1"], firstRows, "the 500th name in fold order is aa, a named program merges, zz is not stored");
        Assert.AreSequenceEqual(["11|0|0|500|0|0|502"], firstTotals, "the total holds every process, 500 programs named");
        Assert.AreEqual(500L, namedAfterSecond, "a later step adds no name to a full bucket");
        Assert.AreSequenceEqual(["1|n002|2|1|1|2"], secondRows, "a named program merges in the later step, a new name is not stored");
        Assert.AreSequenceEqual(["12|0|0|500|0|0|504"], secondTotals, "the later snapshot counts in full");
    }

    /// <summary>
    /// A snapshot without processes, and an empty one, count as snapshots that fold nothing: no program row appears and the process sum stays 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsFoldsSnapshotWithoutProcesses()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, [ConnectionSamples.Established(3)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.ConnectionSnapshots), "both snapshots are folded");
        Assert.AreSequenceEqual(["0|ESTABLISHED|1|3|3|3"], rows, "only the listed state has a row");
        Assert.AreSequenceEqual(["2|0|0|0|0|0|0"], totals, "the bucket counts both snapshots and no program or address");
    }

    /// <summary>
    /// A snapshot that lists more than 25 addresses folds only the 25 with the highest count, ties broken by address text in ordinal order; cutting a snapshot is not counted as a capped snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsCutsAddressesToTwentyFivePerSnapshot()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var remotes = Enumerable.Range(1, 30).Select(octet => ConnectionSamples.Remote(ConnectionSamples.Address(0, octet), octet >= 29 ? 9u : 1u)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, remotes: remotes));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var names = await RowsAsync(directory, "SELECT name FROM connection_rollups_1h WHERE kind = 2 ORDER BY name");
        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));

        // Assert
        Assert.HasCount(25, names, "twenty-five addresses are folded");
        Assert.Contains("10.0.0.29", names, "the highest counts stay");
        Assert.Contains("10.0.0.30", names, "the highest counts stay");
        Assert.Contains("10.0.0.4", names, "of the ties at count 1 the first 23 in ordinal order of the text stay: 1, 10 to 19, 2, 20 to 28, 3 and 4");
        Assert.DoesNotContain("10.0.0.5", names, "the ties past them are cut");
        Assert.DoesNotContain("10.0.0.9", names, "the last tie is cut");
        Assert.AreSequenceEqual(["1|0|0|0|25|0|0"], totals, "the bucket holds 25 addresses and counts no capped snapshot");
    }

    /// <summary>
    /// An IPv4-mapped IPv6 address stays apart from its IPv4 address.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsKeepsMappedAddressApart()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote("192.0.2.7", 2), ConnectionSamples.Remote("::ffff:192.0.2.7", 3)]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 2"));

        // Assert
        Assert.AreSequenceEqual(["2|192.0.2.7|1|2|2|2", "2|::ffff:192.0.2.7|1|3|3|3"], rows, "the mapped address is its own row");
    }

    /// <summary>
    /// A 5-minute bucket stores at most 500 addresses, the first met in fold order (record ID, then count descending, then address text): a new address past the cap is dropped, an address already stored merges, and the
    /// snapshots that lost an address are counted once each; the hourly bucket holds them all.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsCapsAddressesPerTierAndCountsCappedSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddMinutes(4);
        var records = Enumerable.Range(0, 19).Select(third => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(third), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(third, 1, 25, 1))).ToList();
        var ranked = ConnectionSamples.Remotes(20, 1, 24, 1).Append(ConnectionSamples.Remote(ConnectionSamples.Address(20, 25), 9)).ToArray();

        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddSeconds(19), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(19, 1, 24, 1)));
        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source, remotes: ranked));
        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddSeconds(21), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote(ConnectionSamples.Address(0, 1), 2), ConnectionSamples.Remote(ConnectionSamples.Address(30, 1), 1)]));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), now, TestContext.CancellationToken);

        var totals5m = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));
        var totals1h = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var storedLast = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "name IN ('10.0.20.25', '10.0.20.1', '10.0.30.1', '10.0.0.1')"));
        var hourly = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_1h WHERE kind = 2 AND name IN ('10.0.20.25', '10.0.20.1', '10.0.30.1')", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(22, steps.Sum(step => step.ConnectionSnapshots), "every snapshot is folded");
        Assert.AreEqual(0, steps.Sum(step => step.TrimmedAddresses), "the buckets are open and not trimmed");
        Assert.AreSequenceEqual(["22|0|2|0|500|0|0"], totals5m, "the 5-minute bucket stores 500 addresses and counts the two snapshots that lost addresses once each");
        Assert.AreSequenceEqual(["22|0|0|0|525|0|0"], totals1h, "the hourly bucket stores every address and caps no snapshot");
        Assert.AreSequenceEqual(["2|10.0.0.1|2|1|2|3", "2|10.0.20.25|1|9|9|9"], storedLast, "the 500th address is the one with the highest count of its snapshot, a stored address merges past the cap, the others are dropped");
        Assert.AreEqual(3L, hourly, "the hourly tier stores the three addresses the 5-minute tier dropped or kept");
    }

    /// <summary>
    /// An hourly bucket stores at most 1,500 addresses: the 61st snapshot of 25 new addresses is dropped and counted as capped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsCapsHourlyAddressesAtFifteenHundred()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddMinutes(56);
        var records = Enumerable.Range(0, 61).Select(third => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(third * 55), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(third, 1, 25, 1))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), now, TestContext.CancellationToken);

        var totals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var rows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.HourlyTier, "name IN ('10.0.59.25', '10.0.60.1')"));

        // Assert
        Assert.AreSequenceEqual(["61|0|1|0|1500|0|0"], totals, "the hourly bucket stores 1,500 addresses and caps one snapshot");
        Assert.AreSequenceEqual(["2|10.0.59.25|1|1|1|1"], rows, "the last address of the 60th snapshot is stored, the first of the 61st is dropped");
    }

    /// <summary>
    /// A bucket that ended at least 10 minutes before the step and holds more addresses than its kept number is trimmed to the addresses with the highest sum, ties broken by address text in ordinal order; the kept addresses keep
    /// their exact values, and the bucket counts the addresses trimmed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsTrimsEndedBucketToTopAddresses()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.RankedSnapshots(Samples.Base));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddMinutes(16), TestContext.CancellationToken);

        var totals5m = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));
        var totals1h = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var ties = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 2 AND sum_value = 9"));
        var below = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_5m WHERE kind = 2 AND sum_value < 9", TestContext.CancellationToken);
        var kept = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "name = '10.0.1.25'"));

        // Assert
        Assert.AreEqual(25, steps.Sum(step => step.TrimmedAddresses), "the step reports the 25 addresses it trimmed");
        Assert.AreSequenceEqual(["3|0|0|0|50|25|0"], totals5m, "the 5-minute bucket keeps 50 addresses and counts 25 trimmed");
        Assert.AreSequenceEqual(["3|0|0|0|75|0|0"], totals1h, "the hourly bucket is below its kept number and is not trimmed");
        Assert.AreSequenceEqual(["2|10.0.0.9|1|9|9|9", "2|10.0.1.9|1|9|9|9"], ties, "of the three addresses with the sum 9 at the cut the first two by text stay");
        Assert.AreEqual(0L, below, "no address below the cut stays");
        Assert.AreSequenceEqual(["2|10.0.1.25|1|25|25|25"], kept, "a kept address keeps its exact values");
    }

    /// <summary>
    /// A bucket that has not ended 10 minutes before the step is left untrimmed, and the same bucket is trimmed by a later step that reads no snapshot at the exact boundary of 10 minutes after its end.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsLeavesOpenBucketUntrimmed()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var boundary = Samples.Base.AddMinutes(15);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.RankedSnapshots(Samples.Base));

        // Act
        var open = await RetentionSamples.FoldAllAsync(store, policy, boundary.AddTicks(-1), TestContext.CancellationToken);
        var totalsOpen = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));
        var ended = await RetentionSamples.FoldAllAsync(store, policy, boundary, TestContext.CancellationToken);
        var totalsEnded = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));

        // Assert
        Assert.AreEqual(0, open.Sum(step => step.TrimmedAddresses), "a bucket that ended less than 10 minutes ago is not trimmed");
        Assert.AreSequenceEqual(["3|0|0|0|75|0|0"], totalsOpen, "all 75 addresses stay while the bucket is open");
        Assert.AreEqual(0, ended.Sum(step => step.ConnectionSnapshots), "the later step reads no snapshot");
        Assert.AreEqual(25, ended.Sum(step => step.TrimmedAddresses), "a bucket that ended 10 minutes ago is trimmed, also by a step that read nothing");
        Assert.AreSequenceEqual(["3|0|0|0|50|25|0"], totalsEnded, "the bucket keeps 50 addresses");
    }

    /// <summary>
    /// A late snapshot merged into a trimmed bucket adds its addresses, and the next step trims the bucket again to its kept number; the trimmed counts add up.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsTrimsAgainAfterLateMerge()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddMinutes(16);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.RankedSnapshots(Samples.Base));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(3), ConnectionSamples.Source, remotes: [.. Enumerable.Range(1, 25).Select(octet => ConnectionSamples.Remote(ConnectionSamples.Address(9, octet), (uint)(100 + octet)))]));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var totals5m = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.FiveMinutesTier));
        var totals1h = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var added = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_5m WHERE kind = 2 AND sum_value > 100", TestContext.CancellationToken);
        var lowest = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 2 AND sum_value <= 17"));

        // Assert
        Assert.AreEqual(1, steps.Sum(step => step.ConnectionSnapshots), "the late snapshot is folded");
        Assert.AreEqual(25, steps.Sum(step => step.TrimmedAddresses), "the second trim removes 25 addresses");
        Assert.AreSequenceEqual(["4|0|0|0|50|50|0"], totals5m, "the bucket keeps 50 addresses and counts the 50 trimmed over both steps");
        Assert.AreSequenceEqual(["4|0|0|0|100|0|0"], totals1h, "the hourly bucket holds 100 addresses and is not trimmed");
        Assert.AreEqual(25L, added, "the 25 new addresses with the highest sums stay");
        Assert.AreSequenceEqual(["2|10.0.0.17|1|17|17|17"], lowest, "of the old addresses the 25 with the highest sums stay, the last of them 10.0.0.17");
    }

    /// <summary>
    /// A step that stops at its bound of 500 snapshots trims nothing and reports more; the next step, which has caught up, trims the ended bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsTrimsNothingInStepAtBound()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddMinutes(16);
        var fillers = Enumerable.Range(0, StorageLimits.ConnectionRollupStepRows - 2).Select(index => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(index), Filler, [ConnectionSamples.Established(1)])).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. ConnectionSamples.RankedSnapshots(Samples.Base), .. fillers]);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var totalsFirst = await RowsAsync(directory, "SELECT addresses, trimmed FROM connection_rollup_totals_5m WHERE source = 'connections'");
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var totalsSecond = await RowsAsync(directory, "SELECT addresses, trimmed FROM connection_rollup_totals_5m WHERE source = 'connections'");

        // Assert
        Assert.AreEqual(StorageLimits.ConnectionRollupStepRows, first.ConnectionSnapshots, "the first step stops at its bound");
        Assert.IsTrue(first.More, "the first step reports more");
        Assert.AreEqual(0, first.TrimmedAddresses, "a step at its bound trims nothing");
        Assert.AreSequenceEqual(["75|0"], totalsFirst, "the ended bucket keeps its 75 addresses after the first step");
        Assert.AreEqual(1, second.ConnectionSnapshots, "the second step reads the rest, below its bound");
        Assert.AreEqual(25, second.TrimmedAddresses, "the step that has caught up trims the bucket");
        Assert.AreSequenceEqual(["50|25"], totalsSecond, "the bucket keeps 50 addresses after the second step");
    }

    /// <summary>
    /// An ended hour folded across two steps is trimmed only once the fold has caught up, so an address that ranks low after the first step and is large in the last snapshot keeps the sum of both, and the kept rows equal
    /// those of the whole hour folded before one trim.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsTrimsEndedHourOnlyOnceFoldCaughtUp()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(2);
        var records = new List<DataRecord>
                      {
                          ConnectionSamples.Snapshot(Samples.Base, ConnectionSamples.Source, remotes: [ConnectionSamples.Remote(LowAddress, 1), .. ConnectionSamples.Remotes(0, 1, 24, 5)])
                      };

        records.AddRange(Enumerable.Range(1, 4).Select(third => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(third), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(third, 1, 25, 5))));
        records.AddRange(Enumerable.Range(5, 495).Select(index => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(index * 5), ConnectionSamples.Source, [ConnectionSamples.Established(1)])));
        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddMinutes(50), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote(LowAddress, 1000)]));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var totalsFirst = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var lowFirst = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.HourlyTier, $"name = '{LowAddress}'"));
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var totalsSecond = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));
        var lowSecond = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.HourlyTier, $"name = '{LowAddress}'"));
        var fives = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollups_1h WHERE kind = 2 AND sum_value = 5", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StorageLimits.ConnectionRollupStepRows, first.ConnectionSnapshots, "the first step stops at its bound of 500 snapshots");
        Assert.AreEqual(0, first.TrimmedAddresses, "the first step trims nothing although the hour ended");
        Assert.AreSequenceEqual(["500|0|0|0|125|0|0"], totalsFirst, "the hour holds its 125 addresses after the first step");
        Assert.AreSequenceEqual(["2|10.0.200.1|1|1|1|1"], lowFirst, "the low address is still there with its sum of the first snapshot");
        Assert.AreEqual(1, second.ConnectionSnapshots, "the second step reads the 501st snapshot");
        Assert.AreEqual(100, second.TrimmedAddresses, "the caught-up step trims 75 addresses of the first 5-minute bucket and 25 of the hour");
        Assert.AreSequenceEqual(["501|0|0|0|100|25|0"], totalsSecond, "the hour is trimmed once, to its 100 addresses");
        Assert.AreSequenceEqual(["2|10.0.200.1|2|1|1000|1001"], lowSecond, "the low address keeps the sum of both snapshots and its minimum and maximum");
        Assert.AreEqual(99L, fives, "the other 99 addresses kept are those of sum 5 first in text order, as after one trim of the whole hour");
    }

    /// <summary>
    /// A snapshot marked <c>aggregates_incomplete</c> counts in the incomplete count of its buckets; a complete one and an imported one that is not complete but carries no mark do not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreFoldRollupsCountsIncompleteConnectionSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var late = Samples.Base.AddMinutes(20);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(1)], incomplete: true),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(2), ConnectionSamples.Source, [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Imported(Samples.Base.AddMinutes(3), ConnectionSamples.Source, ConnectionSamples.Established(1)),
                                          ConnectionSamples.Snapshot(late, ConnectionSamples.Source, [ConnectionSamples.Established(1)], incomplete: true));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var totals5m = await RowsAsync(directory, ConnectionSamples.Totals5m);
        var totals1h = await RowsAsync(directory, ConnectionSamples.Totals1h);

        // Assert
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "3|1|0|0|0|0|0"), ConnectionSamples.TotalsRow(ConnectionSamples.Source, late, "1|1|0|0|0|0|0")], totals5m, "the incomplete snapshots per 5-minute bucket");
        Assert.AreSequenceEqual([ConnectionSamples.TotalsRow(ConnectionSamples.Source, Samples.Base, "4|2|0|0|0|0|0")], totals1h, "the incomplete snapshots of the hour");
    }

    /// <summary>
    /// A hostile command of up to 1,024 bytes is stored as received, ranked with its own values, and an address is stored in the text form of the decoded snapshot, whatever its spelling on the wire.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task SqliteStoreFoldRollupsStoresHostileCommandAsReceived()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        string[] names = ["\u0000", "\u0001", "\u202E", "\U0001F600", "q\"u'o\\te", "100%_", "x'); DROP TABLE records; --", new string('a', 1024)];
        var processes = names.Select((name, index) => ConnectionSamples.Process(index + 1, name, (uint)(names.Length - index))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10), ConnectionSamples.Source, processes: processes));
        await ConnectionSamples.InsertRawAsync(directory.Path, Forged, Samples.Base.AddSeconds(20), "{\"complete\":true,\"remotes\":[{\"addr\":\"2001:DB8:0:0:0:0:0:1\",\"count\":3}]}", TestContext.CancellationToken);

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var rows = await RowsAsync(directory, "SELECT name, sum_value FROM connection_rollups_5m WHERE kind = 1 ORDER BY sum_value DESC");
        var addresses = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "kind = 2"));
        var kept = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM sqlite_master WHERE name = 'records'", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.ConnectionSnapshots), "both snapshots are folded");
        Assert.AreSequenceEqual(names.Select((name, index) => $"{name}|{names.Length - index}").ToList(), rows, "every command is stored as received with its own sum, in the order of its sum");
        Assert.AreSequenceEqual(["2|2001:db8::1|1|3|3|3"], addresses, "the address is stored in the text form of the decoded snapshot");
        Assert.AreEqual(1L, kept, "the SQL text in a command is not run");
    }

    /// <summary>
    /// Counts of 2^32 - 1 are added without wrapping, and a sum already at the largest finite value stays there when a later step merges into it, in both tiers and the total, with the steps succeeding and the position advancing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task SqliteStoreFoldRollupsClampsConnectionSums()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(1);
        var max = double.MaxValue.ToString(CultureInfo.InvariantCulture);
        var wide = uint.MaxValue;

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(10),
                                                                     ConnectionSamples.Source,
                                                                     [ConnectionSamples.State(Proto.Tcp, Established, wide), ConnectionSamples.State(Proto.Tcp6, Established, wide)],
                                                                     [ConnectionSamples.Process(1, "a", wide), ConnectionSamples.Process(2, "a", wide), ConnectionSamples.Process(3, "b", wide)],
                                                                     [ConnectionSamples.Remote(FirstAddress, wide), ConnectionSamples.Remote(FirstAddress, wide)]));

        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var firstRows = await RowsAsync(directory, ConnectionSamples.Items(ConnectionSamples.FiveMinutesTier, "1 = 1"));
        var firstTotals = await RowsAsync(directory, ConnectionSamples.TotalsOf(ConnectionSamples.HourlyTier));

        await RetentionSamples.ExecuteAsync(directory.Path,
                                            "UPDATE connection_rollups_5m SET sum_value = 1.7976931348623157e308; UPDATE connection_rollups_1h SET sum_value = 1.7976931348623157e308; UPDATE connection_rollup_totals_5m SET process_sum = 1.7976931348623157e308; UPDATE connection_rollup_totals_1h SET process_sum = 1.7976931348623157e308",
                                            TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(20), ConnectionSamples.Source, [ConnectionSamples.Established(1)], [ConnectionSamples.Process(1, "a", 1)], [ConnectionSamples.Remote(FirstAddress, 1)]));

        // Act
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var rows5m = await RowsAsync(directory, "SELECT name, seen, min_value, max_value, sum_value FROM connection_rollups_5m ORDER BY kind, name");
        var rows1h = await RowsAsync(directory, "SELECT name, seen, min_value, max_value, sum_value FROM connection_rollups_1h ORDER BY kind, name");
        var totals = await RowsAsync(directory, "SELECT process_sum FROM connection_rollup_totals_5m UNION ALL SELECT process_sum FROM connection_rollup_totals_1h");
        var watermark = await RetentionSamples.CountAsync(directory.Path, ConnectionSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first.ConnectionSnapshots, "the first step folds its snapshot");
        Assert.AreSequenceEqual(["0|ESTABLISHED|1|8589934590|8589934590|8589934590", "1|a|1|8589934590|8589934590|8589934590", "1|b|1|4294967295|4294967295|4294967295", "2|10.0.0.1|1|8589934590|8589934590|8589934590"], firstRows, "counts of 2^32 - 1 are added within a snapshot without wrapping");
        Assert.AreSequenceEqual(["1|0|0|2|1|0|12884901885"], firstTotals, "the process sum covers every process");
        Assert.AreEqual(1, second.ConnectionSnapshots, "the second step folds its snapshot");
        Assert.IsFalse(second.More, "nothing is left after the second step");
        Assert.AreEqual(highest, watermark, "the position advances to the last snapshot");
        Assert.AreSequenceEqual([$"ESTABLISHED|2|1|8589934590|{max}", $"a|2|1|8589934590|{max}", $"b|1|4294967295|4294967295|{max}", $"10.0.0.1|2|1|8589934590|{max}"], rows5m, "a sum at the largest finite value stays there, the extremes are exact");
        Assert.AreSequenceEqual(rows5m, rows1h, "the hourly rows are the same");
        Assert.AreSequenceEqual([max, max], totals, "the process sums stay clamped in both tiers");
    }

    /// <summary>
    /// A fold step whose connection part fails rolls back whole with the metric and program fold, leaving the database and every watermark as before, and the next step folds all three.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreFoldRollupsRollsBackConnectionPartWithStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(1);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(5), "cpu", 1),
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(15), ConnectionSamples.Source, [ConnectionSamples.Established(1)]));
        await RetentionSamples.ExecuteAsync(directory.Path, "CREATE TRIGGER connection_fails BEFORE INSERT ON connection_rollups_5m BEGIN SELECT RAISE(ABORT, 'disk full'); END", TestContext.CancellationToken);

        var before = await FingerprintAsync(directory);

        // Act
        await Assert.ThrowsExactlyAsync<StoreException>(() => store.FoldRollupsAsync(policy, now, TestContext.CancellationToken), "the failing step");

        var after = await FingerprintAsync(directory);

        await RetentionSamples.ExecuteAsync(directory.Path, "DROP TRIGGER connection_fails", TestContext.CancellationToken);

        var retry = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(before, after, "the failed step left the rollup tables, the watermarks and the records as before it");
        Assert.AreEqual(1, retry.Sum(step => step.Values), "the next step folds the metric");
        Assert.AreEqual(1, retry.Sum(step => step.Snapshots), "the next step folds the process snapshot");
        Assert.AreEqual(1, retry.Sum(step => step.ConnectionSnapshots), "the next step folds the connection snapshot");
        Assert.AreEqual(1L, await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM connection_rollup_totals_5m", TestContext.CancellationToken), "the connection bucket exists after the retry");
    }

    /// <summary>
    /// Gets the program name of a number: <c>n001</c> for 1, as one process of count 1.
    /// </summary>
    /// <param name="index">The number</param>
    /// <returns>The process</returns>
    private static ProcessConnections Named(int index)
    {
        return ConnectionSamples.Process(index, string.Create(CultureInfo.InvariantCulture, $"n{index:D3}"), 1);
    }

    /// <summary>
    /// Gets the number of rows the fold reads before the stored bytes pass the budget: the first row that takes the sum past it ends the step.
    /// </summary>
    /// <param name="lengths">The lengths of the stored data in the order of the rows</param>
    /// <returns>The number of rows</returns>
    private static int RowsWithinBudget(List<long> lengths)
    {
        var sum = 0L;

        for (var index = 0; index < lengths.Count; index++)
        {
            sum += lengths[index];

            if (sum > StorageLimits.ConnectionRollupStepBytes)
            {
                return index + 1;
            }
        }

        return lengths.Count;
    }

    /// <summary>
    /// Reads the lengths of the stored data of the connection snapshots in the order of their IDs.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the lengths</returns>
    private async Task<List<long>> LengthsAsync(string directory)
    {
        var rows = await ConnectionSamples.RowsAsync(directory, "SELECT length(CAST(data AS BLOB)) FROM records WHERE kind = 'connection_snapshot' ORDER BY id", TestContext.CancellationToken);

        return [.. rows.Select(row => long.Parse(row, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Reads the rows of a query on the store of a directory, each as its values joined with a bar.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the rows as text</returns>
    private Task<List<string>> RowsAsync(TempDirectory directory, string sql)
    {
        return ConnectionSamples.RowsAsync(directory.Path, sql, TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads what a failed fold step must leave unchanged: the counts of every rollup table, the records and the meta rows with the watermarks.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<List<string>> FingerprintAsync(TempDirectory directory)
    {
        var rows = await RowsAsync(directory,
                                   "SELECT (SELECT count(*) FROM metric_rollups_5m), (SELECT count(*) FROM metric_rollups_1h), (SELECT count(*) FROM program_rollups_5m), (SELECT count(*) FROM program_rollup_totals_5m), "
                                   + "(SELECT count(*) FROM program_rollups_1h), (SELECT count(*) FROM program_rollup_totals_1h), (SELECT count(*) FROM connection_rollups_5m), (SELECT count(*) FROM connection_rollup_totals_5m), "
                                   + "(SELECT count(*) FROM connection_rollups_1h), (SELECT count(*) FROM connection_rollup_totals_1h), (SELECT count(*) FROM records)");

        rows.AddRange(await RowsAsync(directory, "SELECT key, value FROM meta ORDER BY key"));

        return rows;
    }

    #endregion // Methods
}