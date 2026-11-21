using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the fold of process snapshots into the per-program rollups of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreProgramRollupTests
{
    #region Constants

    private const string Nginx = "nginx";

    private const string InvalidPrefix = "store: invalid query: ";

    private const string RangeText = "store: invalid query: from and to must be within the storable range";

    private const int ProgramHours = 1500;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every decodable process snapshot is folded into the 5-minute and the hourly bucket of its source: per program the number of snapshots naming it and the minimum, sum and maximum of RSS, PSS and CPU %, and
    /// per bucket the snapshots, the PSS snapshots, the incomplete snapshots, the named programs and the sums of all programs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsFoldsProgramsIntoBothTiers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = Samples.Base.AddMinutes(1);
        var second = Samples.Base.AddMinutes(3);
        var third = Samples.Base.AddMinutes(20);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(first, ProgramSamples.Source, ProgramSamples.Program(Nginx, 2, 100, 10, 60), ProgramSamples.Program("php", 1, 50, 5, 30)),
                                          ProgramSamples.Snapshot(second, ProgramSamples.Source, ProgramSamples.Program(Nginx, 2, 200, 20, 120), ProgramSamples.Program("php", 1, 70, 6, 40)),
                                          ProgramSamples.Snapshot(third, ProgramSamples.Source, ProgramSamples.Program(Nginx, 2, 400, 40, 240)));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var programs5m = (await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken)).Where(row => row.Contains("|nginx|") || row.Contains("|php|")).ToList();
        var programs1h = (await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows1h, TestContext.CancellationToken)).Where(row => row.Contains("|nginx|")).ToList();
        var totals5m = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var totals1h = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals1h, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3, steps.Sum(step => step.Snapshots), "every snapshot is folded once");
        Assert.AreEqual(0, steps.Sum(step => step.SkippedSnapshots), "no snapshot is skipped");
        Assert.AreSequenceEqual([
                                    ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, Nginx, "2|100|200|300|60|120|180|10|20|30"),
                                    ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, "php", "2|50|70|120|30|40|70|5|6|11"),
                                    ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base.AddMinutes(20), Nginx, "1|400|400|400|240|240|240|40|40|40")
                                ],
                                programs5m,
                                "the 5-minute rows per program");
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, Nginx, "3|100|400|700|60|240|420|10|40|70")], programs1h, "the hourly row of the program in all three snapshots");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "2|2|0|2|420|250|41"), ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base.AddMinutes(20), "1|1|0|1|400|240|40")], totals5m, "the 5-minute totals");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "3|3|0|2|820|490|81")], totals1h, "the hourly total");
        Assert.AreEqual(highest, watermark, "the watermark is the last snapshot read");
    }

    /// <summary>
    /// A second fold over the same snapshots changes nothing, and a snapshot added afterwards is folded alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsFoldsEachSnapshotOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)));

        var firstSteps = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var afterFirst = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);

        // Act
        var repeat = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var afterRepeat = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)));

        var added = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, firstSteps.Sum(step => step.Snapshots), "the first fold reads the snapshot");
        Assert.HasCount(1, afterFirst, "the first fold writes the program row");
        Assert.AreEqual(0, repeat.Sum(step => step.Snapshots), "the repeated fold reads no snapshot");
        Assert.AreSequenceEqual(afterFirst, afterRepeat, "the repeated fold leaves the rows as they are");
        Assert.AreEqual(1, added.Sum(step => step.Snapshots), "the later snapshot is folded alone");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "2|0|0|1|400|0|4")], totals, "each snapshot counts once in the total");
    }

    /// <summary>
    /// Imported snapshots are folded into the buckets of their source beside those of the other origins.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsFoldsImportedSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(RecordOrigin.Import, Samples.Base.AddSeconds(10), ProgramSamples.Source, true, 1, [ProgramSamples.Program(Nginx, 1, 100, 1)]),
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var programs = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals1h, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.Snapshots), "the imported and the backend snapshot are folded");
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, Nginx, "2|100|300|400|null|null|0|1|3|4")], programs, "one row holds both origins");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "2|0|0|1|400|0|4")], totals, "the hourly total counts both");
    }

    /// <summary>
    /// A stored snapshot whose data does not decode is skipped and counted, the position moves past it, and the snapshots around it are folded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsSkipsUndecodableSnapshotsAndCountsThem()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var at = Samples.Base.AddSeconds(30);
        string?[] texts = ["not json", null, "{\"programs\":\"x\"}", "{\"programs\":[null]}", "{\"programs\":[{\"program\":null}]}"];
        byte[][] invalid = [ProgramSamples.JsonWithNameBytes(0xFF), ProgramSamples.JsonWithNameBytes(0xC3), ProgramSamples.JsonWithNameBytes(0xED, 0xA0, 0x80), ProgramSamples.JsonWithNameBytes(0xC0, 0xAF)];

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)));

        foreach (var text in texts)
        {
            await ProgramSamples.InsertRawAsync(directory.Path, "forged", at, text, TestContext.CancellationToken);
        }

        foreach (var bytes in invalid)
        {
            await ProgramSamples.InsertBytesAsync(directory.Path, "forged", at, bytes, TestContext.CancellationToken);
        }

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(50), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.Snapshots), "the two decodable snapshots are folded");
        Assert.AreEqual(9, steps.Sum(step => step.SkippedSnapshots), "the nine undecodable snapshots are counted");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "2|0|0|1|400|0|4")], totals, "only the decodable snapshots reach the buckets");
        Assert.AreEqual(highest, watermark, "the position moves past the skipped rows to the last row read");
        Assert.IsFalse(steps[^1].More, "nothing is left to fold");
    }

    /// <summary>
    /// A snapshot is incomplete when its programs' counts do not add up to its total (summed beyond 32 bits), when its total is absent and it is not marked complete, or when it lists no program; each counts in its
    /// bucket as one snapshot and its absent programs as zeros.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsCountsIncompleteSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var at = Samples.Base.AddSeconds(10);
        ProgramAggregate[] two = [ProgramSamples.Program("a", 2, 10, 1)];

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "equal", true, 2, two),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "different", true, 5, two),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "absent-false", false, null, two),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "absent-true", true, null, two),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "none", true, 1, null),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "empty", true, 1, []),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, at, "wide", true, 1, [ProgramSamples.Program("a", uint.MaxValue, 10, 1), ProgramSamples.Program("b", 2, 10, 1)]));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var rows = await ProgramSamples.RowsAsync(directory.Path, "SELECT source, snapshots, incomplete, named FROM program_rollup_totals_5m ORDER BY source", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(7, steps.Sum(step => step.Snapshots), "every snapshot is folded, none fails on its counts");
        Assert.AreEqual(0, steps.Sum(step => step.SkippedSnapshots), "none is skipped");
        Assert.AreSequenceEqual(["absent-false|1|1|1", "absent-true|1|0|1", "different|1|1|1", "empty|1|1|0", "equal|1|0|1", "none|1|1|0", "wide|1|1|2"], rows, "snapshots, incomplete snapshots and named programs of each source");
    }

    /// <summary>
    /// A fold step reads at most 500 snapshots, commits its position, and the next step continues there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsStopsAtTheSnapshotRowBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var rows = StorageLimits.ProgramRollupStepRows + 1;
        var records = Enumerable.Range(0, rows).Select(index => ProgramSamples.Snapshot(Samples.Base.AddSeconds(index), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var expectedWatermark = await RetentionSamples.CountAsync(directory.Path, $"SELECT id FROM records WHERE kind = 'process_snapshot' ORDER BY id LIMIT 1 OFFSET {StorageLimits.ProgramRollupStepRows - 1}", TestContext.CancellationToken);
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var folded = await RetentionSamples.CountAsync(directory.Path, "SELECT coalesce(sum(snapshots), 0) FROM program_rollup_totals_1h", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StorageLimits.ProgramRollupStepRows, first.Snapshots, "the first step reads 500 snapshots");
        Assert.IsTrue(first.More, "the first step reports more rows");
        Assert.AreEqual(expectedWatermark, watermark, "the position of the first step is the last row it read");
        Assert.AreEqual(1, second.Snapshots, "the second step reads the rest");
        Assert.IsFalse(second.More, "nothing is left after the second step");
        Assert.AreEqual((long)rows, folded, "every snapshot is folded exactly once");
    }

    /// <summary>
    /// A fold step stops after the snapshot that takes the stored bytes read past 2 MiB, commits its position, and the next step continues there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsStopsAtTheSnapshotByteBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var wide = Enumerable.Range(0, 50).Select(index => ProgramSamples.Program(string.Create(CultureInfo.InvariantCulture, $"{index:D2}{new string('w', 998)}"), 1, 100, 1)).ToArray();
        var records = Enumerable.Range(0, 45).Select(index => ProgramSamples.Snapshot(Samples.Base.AddSeconds(index), ProgramSamples.Source, wide)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        var lengths = await LengthsAsync(directory.Path);
        var expectedFirst = RowsWithinBudget(lengths);

        // Act
        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var expectedWatermark = await RetentionSamples.CountAsync(directory.Path, $"SELECT id FROM records WHERE kind = 'process_snapshot' ORDER BY id LIMIT 1 OFFSET {expectedFirst - 1}", TestContext.CancellationToken);
        var rest = await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        var folded = await RetentionSamples.CountAsync(directory.Path, "SELECT coalesce(sum(snapshots), 0) FROM program_rollup_totals_1h", TestContext.CancellationToken);

        // Assert
        Assert.IsGreaterThan(40_000, lengths[0], "a snapshot is tens of kilobytes");
        Assert.IsLessThan(45, expectedFirst, "the byte budget ends the first step before all rows");
        Assert.AreEqual(expectedFirst, first.Snapshots, "the first step reads up to and including the snapshot that takes the bytes past the budget");
        Assert.IsTrue(first.More, "the first step reports more rows");
        Assert.AreEqual(expectedWatermark, watermark, "the position of the first step is the last row it read");
        Assert.AreEqual(45 - expectedFirst, rest.Sum(step => step.Snapshots), "the next steps continue after the position");
        Assert.AreEqual(45L, folded, "every snapshot is folded exactly once");
    }

    /// <summary>
    /// A fold step cancelled while it runs commits nothing, and a later step folds everything.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsCancelledStepCommitsNothing()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var source = new CancellationTokenSource();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(1);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 200, 2)));

        store.RetentionStepStarted = () =>
                                     {
                                         source.Cancel();

                                         return Task.CompletedTask;
                                     };

        // Act
        var cancelled = false;

        try
        {
            _ = await store.FoldRollupsAsync(policy, now, source.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        store.RetentionStepStarted = null;

        var tables = await RetentionSamples.CountAsync(directory.Path, "SELECT (SELECT count(*) FROM program_rollups_5m) + (SELECT count(*) FROM program_rollups_1h) + (SELECT count(*) FROM program_rollup_totals_5m) + (SELECT count(*) FROM program_rollup_totals_1h)", TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var next = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(cancelled, "the cancelled step throws an operation cancelled exception");
        Assert.AreEqual(0L, tables, "no rollup row is committed by the cancelled step");
        Assert.AreEqual(0L, watermark, "the position is not advanced by the cancelled step");
        Assert.AreEqual(2, next.Snapshots, "the next step folds both snapshots");
    }

    /// <summary>
    /// A bucket already past the period of its tier is not created, while the other tier and newer buckets are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsSkipsProgramBucketsPastTheirTier()
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
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(recent.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)),
                                          ProgramSamples.Snapshot(Samples.Base.AddDays(-3), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 500, 5)));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var programs5m = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);
        var programs1h = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows1h, TestContext.CancellationToken);
        var totals5m = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var totals1h = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals1h, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow(ProgramSamples.Source, recent, Nginx, "1|300|300|300|null|null|0|3|3|3")], programs5m, "no 5-minute row older than 1 day");
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, Nginx, "1|100|100|100|null|null|0|1|1|1"), ProgramSamples.ProgramRow(ProgramSamples.Source, recent, Nginx, "1|300|300|300|null|null|0|3|3|3")], programs1h, "no hourly row older than 2 days");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, recent, "1|0|0|1|300|0|3")], totals5m, "no 5-minute total older than 1 day");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "1|0|0|1|100|0|1"), ProgramSamples.TotalsRow(ProgramSamples.Source, recent, "1|0|0|1|300|0|3")], totals1h, "no hourly total older than 2 days");
    }

    /// <summary>
    /// A bucket holding only RSS snapshots takes a PSS snapshot in a later step, and a bucket holding a PSS snapshot takes an RSS snapshot: the RSS columns and the totals are right in both, PSS counts only the PSS snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task SqliteStoreFoldRollupsMergesPssIntoRssOnlyBucket()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var at = Samples.Base.AddSeconds(10);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(at, "rss-first", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(at, "pss-first", ProgramSamples.Program(Nginx, 1, 300, 3, 200)));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var afterFirst = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(at.AddSeconds(10), "rss-first", ProgramSamples.Program(Nginx, 1, 300, 3, 200)),
                                          ProgramSamples.Snapshot(at.AddSeconds(10), "pss-first", ProgramSamples.Program(Nginx, 1, 100, 1)));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var programs5m = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);
        var programs1h = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows1h, TestContext.CancellationToken);
        var totals5m = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var totals1h = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals1h, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow("pss-first", Samples.Base, Nginx, "1|300|300|300|200|200|200|3|3|3"), ProgramSamples.ProgramRow("rss-first", Samples.Base, Nginx, "1|100|100|100|null|null|0|1|1|1")], afterFirst, "after the first step the RSS-only row has no PSS minimum and maximum");

        List<string> merged = [ProgramSamples.ProgramRow("pss-first", Samples.Base, Nginx, "2|100|300|400|200|200|200|1|3|4"), ProgramSamples.ProgramRow("rss-first", Samples.Base, Nginx, "2|100|300|400|200|200|200|1|3|4")];

        Assert.AreSequenceEqual(merged, programs5m, "both orders give the same 5-minute rows");
        Assert.AreSequenceEqual(merged, programs1h, "and the same hourly rows");

        List<string> totals = [ProgramSamples.TotalsRow("pss-first", Samples.Base, "2|1|0|1|400|200|4"), ProgramSamples.TotalsRow("rss-first", Samples.Base, "2|1|0|1|400|200|4")];

        Assert.AreSequenceEqual(totals, totals5m, "the 5-minute totals count one PSS snapshot of two");
        Assert.AreSequenceEqual(totals, totals1h, "the hourly totals too");
    }

    /// <summary>
    /// The programs of a bucket sum, per measure, to the total of its source: a program a snapshot does not name adds nothing but is counted by the snapshots of the bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsProgramSumsAddUpToTheSourceTotal()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1), ProgramSamples.Program("php", 1, 50, 2)),
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)),
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(30), ProgramSamples.Source));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var seen = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, seen FROM program_rollups_5m ORDER BY program", TestContext.CancellationToken);
        var sums = await ProgramSamples.RowsAsync(directory.Path, "SELECT sum(rss_sum), sum(cpu_sum) FROM program_rollups_5m", TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, "SELECT snapshots, incomplete, rss_sum, cpu_sum FROM program_rollup_totals_5m", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["nginx|2", "php|1"], seen, "each program counts the snapshots that name it");
        Assert.AreSequenceEqual(["450|6"], sums, "the sums of the programs");
        Assert.AreSequenceEqual(["3|1|450|6"], totals, "equal the sums of the total, which counts the three snapshots and the one without programs as incomplete");
    }

    /// <summary>
    /// A snapshot with more than 50 programs folds the 50 with the highest RSS and the 50 with the highest CPU %, ties by ordinal program name; every other program stays only in the total of the bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsCutsToFiftyProgramsPerKey()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var programs = Enumerable.Range(0, 49).Select(index => ProgramSamples.Program(string.Create(CultureInfo.InvariantCulture, $"h{index:D2}"), 1, (ulong)(10_000 + index), 1)).ToList();

        programs.AddRange(Enumerable.Range(0, 11).Select(index => ProgramSamples.Program(string.Create(CultureInfo.InvariantCulture, $"t{index:D2}"), 1, 500, index == 10 ? 90 : 1)));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), "cut", [.. programs]));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var names = await ProgramSamples.RowsAsync(directory.Path, "SELECT program FROM program_rollups_5m WHERE source = 'cut' ORDER BY program", TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var expected = Enumerable.Range(0, 49).Select(index => string.Create(CultureInfo.InvariantCulture, $"h{index:D2}")).Append("t00").Append("t10").ToList();

        // Assert
        Assert.AreSequenceEqual(expected, names, "the 49 highest RSS and the tie at rank 50 by ordinal name, with the one program of the highest CPU %");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow("cut", Samples.Base, "1|0|0|51|496676|0|149")], totals, "the total holds all 60 programs, 51 of them named");
    }

    /// <summary>
    /// A PSS snapshot is cut by its PSS, not by its RSS.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsCutsAPssSnapshotByPss()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var programs = Enumerable.Range(0, 60).Select(index => ProgramSamples.Program(string.Create(CultureInfo.InvariantCulture, $"q{index:D2}"), 1, (ulong)(2000 - index), index + 1, (ulong)(100 + index))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), "cutpss", programs));

        // Act
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var names = await ProgramSamples.RowsAsync(directory.Path, "SELECT program FROM program_rollups_5m WHERE source = 'cutpss' ORDER BY program", TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals5m, TestContext.CancellationToken);
        var expected = Enumerable.Range(10, 50).Select(index => string.Create(CultureInfo.InvariantCulture, $"q{index:D2}")).ToList();

        // Assert
        Assert.AreSequenceEqual(expected, names, "the 50 programs of the highest PSS, which are also the 50 of the highest CPU %");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow("cutpss", Samples.Base, "1|1|0|50|118230|7770|1830")], totals, "the total holds all 60 programs");
    }

    /// <summary>
    /// A bucket names at most 500 programs per source: the first 500 names in fold order stay named, a further name goes only into the total, and a named program stays named in a later step.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreFoldRollupsCapsNamedProgramsInFoldOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(3);
        var records = Enumerable.Range(0, 10).Select(snapshot => ProgramSamples.Snapshot(Samples.Base.AddSeconds(snapshot), ProgramSamples.Source, [.. Enumerable.Range((snapshot * 50) + 1, 50).Select(Named)])).ToList();

        records.Add(ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, Named(1), Named(501)));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var namedAfterFirst = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM program_rollups_5m", TestContext.CancellationToken);
        var firstRows = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, seen, rss_sum FROM program_rollups_5m WHERE program IN ('n001', 'n500', 'n501') ORDER BY program", TestContext.CancellationToken);
        var firstTotals = await ProgramSamples.RowsAsync(directory.Path, "SELECT snapshots, named, rss_sum, cpu_sum FROM program_rollup_totals_5m", TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(11), ProgramSamples.Source, Named(2), Named(502)));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var namedAfterSecond = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM program_rollups_5m", TestContext.CancellationToken);
        var secondRows = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, seen, rss_sum FROM program_rollups_5m WHERE program IN ('n002', 'n502') ORDER BY program", TestContext.CancellationToken);
        var secondTotals = await ProgramSamples.RowsAsync(directory.Path, "SELECT snapshots, named, rss_sum, cpu_sum FROM program_rollup_totals_5m", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(500L, namedAfterFirst, "the bucket names 500 programs");
        Assert.AreSequenceEqual(["n001|2|200", "n500|1|100"], firstRows, "the first names stay named, a named program merges later, the 501st name is not stored");
        Assert.AreSequenceEqual(["11|500|50200|502"], firstTotals, "the total holds every program, 500 of them named");
        Assert.AreEqual(500L, namedAfterSecond, "a later step adds no name to a full bucket");
        Assert.AreSequenceEqual(["n002|2|200"], secondRows, "a named program merges in the later step, a new name is not stored");
        Assert.AreSequenceEqual(["12|500|50400|504"], secondTotals, "the total holds the later snapshot in full");
    }

    /// <summary>
    /// A late snapshot is merged into its bucket whose snapshots were removed: the extremes, the sums and the counts become exact, and the late snapshot is removed with its period like the others.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreFoldRollupsMergesLateSnapshotIntoBucket()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 10)));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, now, "marker", 1));
        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var removed = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM records WHERE kind = 'process_snapshot'", TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 30)));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, now, "marker", 2));

        // Act
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var programs = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.ProgramRows5m, TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, ProgramSamples.Totals1h, TestContext.CancellationToken);

        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var left = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM records WHERE kind = 'process_snapshot'", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, removed, "the first snapshot was removed after its fold");
        Assert.AreSequenceEqual([ProgramSamples.ProgramRow(ProgramSamples.Source, Samples.Base, Nginx, "2|100|300|400|null|null|0|10|30|40")], programs, "the late snapshot is merged into the bucket");
        Assert.AreSequenceEqual([ProgramSamples.TotalsRow(ProgramSamples.Source, Samples.Base, "2|0|0|1|400|0|40")], totals, "the total counts both snapshots");
        Assert.AreEqual(0L, left, "the late snapshot is removed after its fold");
    }

    /// <summary>
    /// Sums that overflow are clamped to the largest finite value in a step and when a later step merges into the bucket, in both tiers, and the steps succeed and advance their position; an RSS near 2^64 bytes is folded as received.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreFoldRollupsClampsOverflowingProgramSums()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddHours(1);
        var max = double.MaxValue.ToString(CultureInfo.InvariantCulture);
        var huge = 1e308.ToString(CultureInfo.InvariantCulture);
        var rss = ((double)ulong.MaxValue).ToString(CultureInfo.InvariantCulture);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, ProgramSamples.Program("a", 1, ulong.MaxValue, 1e308), ProgramSamples.Program("b", 1, ulong.MaxValue, 1e308)));

        var first = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var firstTotals = await ProgramSamples.RowsAsync(directory.Path, "SELECT cpu_sum FROM program_rollup_totals_5m UNION ALL SELECT cpu_sum FROM program_rollup_totals_1h", TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(20), ProgramSamples.Source, ProgramSamples.Program("a", 1, 1, 1e308)));

        // Act
        var second = await store.FoldRollupsAsync(policy, now, TestContext.CancellationToken);
        var rows5m = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, rss_max, cpu_max, cpu_sum FROM program_rollups_5m ORDER BY program", TestContext.CancellationToken);
        var rows1h = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, rss_max, cpu_max, cpu_sum FROM program_rollups_1h ORDER BY program", TestContext.CancellationToken);
        var totals = await ProgramSamples.RowsAsync(directory.Path, "SELECT cpu_sum FROM program_rollup_totals_5m UNION ALL SELECT cpu_sum FROM program_rollup_totals_1h", TestContext.CancellationToken);
        var watermark = await RetentionSamples.CountAsync(directory.Path, ProgramSamples.Watermark, TestContext.CancellationToken);
        var highest = await RetentionSamples.CountAsync(directory.Path, "SELECT max(id) FROM records", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first.Snapshots, "the first step folds its snapshot");
        Assert.AreSequenceEqual([max, max], firstTotals, "the total of two programs of 1e308 is clamped to the largest finite value in both tiers");
        Assert.AreEqual(1, second.Snapshots, "the second step folds its snapshot");
        Assert.IsFalse(second.More, "nothing is left after the second step");
        Assert.AreEqual(highest, watermark, "the position advances to the last snapshot");
        Assert.AreSequenceEqual([$"a|{rss}|{huge}|{max}", $"b|{rss}|{huge}|{huge}"], rows5m, "the sum of the program of the two steps is clamped, its maximum and the other program are not");
        Assert.AreSequenceEqual(rows5m, rows1h, "the hourly rows are the same");
        Assert.AreSequenceEqual([max, max], totals, "the totals stay clamped");
    }

    /// <summary>
    /// Hostile program names of up to 1,024 bytes are stored as received: quotes, a percent sign, SQL text, a NUL and other control characters, a right-to-left override and an emoji, each ranked with its own values.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreFoldRollupsStoresHostileProgramNamesAsReceived()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        string[] names = ["\u0000", "\u0001", "\u202E", "\U0001F600", "q\"u'o\\te", "100%_", "x'); DROP TABLE records; --", new string('a', 1024)];
        var programs = names.Select((name, index) => ProgramSamples.Program(name, 1, (ulong)((names.Length - index) * 1000), 1)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddSeconds(10), ProgramSamples.Source, programs));

        // Act
        var steps = await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);
        var rows = await ProgramSamples.RowsAsync(directory.Path, "SELECT program, rss_sum FROM program_rollups_5m ORDER BY rss_sum DESC", TestContext.CancellationToken);
        var kept = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM sqlite_master WHERE name = 'records'", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, steps.Sum(step => step.Snapshots), "the snapshot is folded");
        Assert.AreSequenceEqual(names.Select((name, index) => $"{name}|{(names.Length - index) * 1000}").ToList(), rows, "every name is stored as received with its own values, in the order of its memory");
        Assert.AreEqual(1L, kept, "the SQL text in a name is not run");
    }

    /// <summary>
    /// The read returns the n programs with the highest average of the measure over the range, in that order, each with its average, minimum and maximum per step; a tie is broken by program name, a step without snapshots is
    /// a gap, and the sum of all other programs is returned per step.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReturnsTopProgramsByMeasure()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);
        var to = Samples.Base.AddMinutes(10);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1),
                                                                  ProgramSamples.Source,
                                                                  ProgramSamples.Program("a", 1, 100, 1),
                                                                  ProgramSamples.Program("b", 1, 500, 0.5),
                                                                  ProgramSamples.Program("c", 1, 200, 3),
                                                                  ProgramSamples.Program("d", 1, 50, 2)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(2),
                                                                  ProgramSamples.Source,
                                                                  ProgramSamples.Program("a", 1, 300, 2),
                                                                  ProgramSamples.Program("b", 1, 500, 0.5),
                                                                  ProgramSamples.Program("c", 1, 200, 3),
                                                                  ProgramSamples.Program("d", 1, 50, 2)));
        await FoldAsync(store);

        // Act
        var memory = await store.ProgramRollupsAsync(Query(Samples.Base, to, five, ProgramMeasure.Memory, 3), TestContext.CancellationToken);
        var cpu = await store.ProgramRollupsAsync(Query(Samples.Base, to, five, ProgramMeasure.Cpu, 2), TestContext.CancellationToken);
        var all = await store.ProgramRollupsAsync(Query(Samples.Base, to, five, ProgramMeasure.Memory, 10), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base, Samples.Base.AddMinutes(5)], memory.Times, "the steps of the range");
        Assert.AreSequenceEqual(["b", "a", "c"], Names(memory), "memory: the highest average first, the tie of a and c by name");
        Assert.AreEqual(ProgramMemoryKey.Rss, memory.MemoryKey, "snapshots without PSS read as RSS");
        Assert.AreSequenceEqual(["500/500/500", "gap"], Points(memory, "b"), "b in both snapshots");
        Assert.AreSequenceEqual(["200/100/300", "gap"], Points(memory, "a"), "a averages 100 and 300");
        Assert.AreSequenceEqual(["200/200/200", "gap"], Points(memory, "c"), "c");
        Assert.AreSequenceEqual([50.0, null], memory.Others, "the programs left out add up to d, and a step without snapshots has none");
        Assert.AreSequenceEqual(["c", "d"], Names(cpu), "CPU: ranked by the average CPU %");
        Assert.IsNull(cpu.MemoryKey, "a CPU read names no memory key");
        Assert.AreSequenceEqual(["3/3/3", "gap"], Points(cpu, "c"), "c in CPU");
        Assert.AreSequenceEqual(["2/2/2", "gap"], Points(cpu, "d"), "d in CPU");
        Assert.AreSequenceEqual([2.0, null], cpu.Others, "a and b make the other programs of the CPU read");
        Assert.AreSequenceEqual(["b", "a", "c", "d"], Names(all), "a count above the programs returns every program");
        Assert.AreSequenceEqual([0.0, null], all.Others, "nothing is left over when every program is named");
    }

    /// <summary>
    /// A program a snapshot of its step does not name counts as 0 in its minimum and average, so the averages of all programs add up to the average total.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SqliteStoreProgramRollupsCountsMissingProgramAsZero()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 50, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(2), ProgramSamples.Source, ProgramSamples.Program("a", 1, 300, 1)));
        await FoldAsync(store);

        // Act
        var fiveMinutes = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5), ProgramMeasure.Memory, 2), TestContext.CancellationToken);
        var hourly = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddHours(1), TimeSpan.FromHours(1), ProgramMeasure.Memory, 2), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["a", "b"], Names(fiveMinutes), "both programs are named");
        Assert.AreSequenceEqual(["200/100/300"], Points(fiveMinutes, "a"), "a is in both snapshots");
        Assert.AreSequenceEqual(["25/0/50"], Points(fiveMinutes, "b"), "b is missing from one snapshot: its minimum is 0 and its average adds that 0");
        Assert.AreSequenceEqual([0.0], fiveMinutes.Others, "the averages of the programs add up to the total of 225");
        Assert.AreSequenceEqual(["200/100/300"], Points(hourly, "a"), "the hourly tier gives the same for a");
        Assert.AreSequenceEqual(["25/0/50"], Points(hourly, "b"), "and for b");
        Assert.AreSequenceEqual([0.0], hourly.Others, "and the same other programs");
    }

    /// <summary>
    /// Memory is PSS when every snapshot of the range is a PSS snapshot, else RSS for the whole range, which the result names.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReadsRssWhenRangeMixesPssAndRss()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1, 90), ProgramSamples.Program("b", 1, 200, 1, 10)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(2), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1, 50), ProgramSamples.Program("b", 1, 200, 1, 10)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(7), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 200, 1)));
        await FoldAsync(store);

        // Act
        var pss = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), five, ProgramMeasure.Memory, 2), TestContext.CancellationToken);
        var mixed = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 2), TestContext.CancellationToken);
        var cpu = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), five, ProgramMeasure.Cpu, 2), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ProgramMemoryKey.Pss, pss.MemoryKey, "a range of PSS snapshots only reads PSS");
        Assert.AreSequenceEqual(["a", "b"], Names(pss), "ranked by PSS, so a before b");
        Assert.AreSequenceEqual(["70/50/90"], Points(pss, "a"), "the PSS of a over its two PSS snapshots");
        Assert.AreSequenceEqual(["10/10/10"], Points(pss, "b"), "the PSS of b");
        Assert.AreEqual(ProgramMemoryKey.Rss, mixed.MemoryKey, "one RSS snapshot in the range makes the whole range RSS");
        Assert.AreSequenceEqual(["b", "a"], Names(mixed), "ranked by RSS, so b before a");
        Assert.AreSequenceEqual(["200/200/200", "200/200/200"], Points(mixed, "b"), "b in RSS in both steps");
        Assert.AreSequenceEqual(["100/100/100", "100/100/100"], Points(mixed, "a"), "a in RSS in both steps, also in the step of PSS snapshots");
        Assert.IsNull(cpu.MemoryKey, "CPU names no memory key");
    }

    /// <summary>
    /// The result holds, per step, the average of the programs outside the top n and the number of folded snapshots that did not list every program.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReturnsOthersAndIncompletePerStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 60, 1), ProgramSamples.Program("c", 1, 40, 1)),
                                          ProgramSamples.Snapshot(RecordOrigin.Backend, Samples.Base.AddMinutes(6), ProgramSamples.Source, true, 5, [ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 60, 1)]),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(7), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 20, 1)));
        await FoldAsync(store);

        // Act
        var result = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(5), ProgramMeasure.Memory, 1), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["a"], Names(result), "only the top program is named");
        Assert.AreSequenceEqual(["100/100/100", "100/100/100", "gap"], Points(result, "a"), "the top program per step, the last step has no snapshot");
        Assert.AreSequenceEqual([100.0, 40.0, null], result.Others, "b and c in the first step, the average of b over two snapshots in the second");
        Assert.AreSequenceEqual([0, 1, 0], result.Incomplete, "the snapshot whose counts differ from its total is the incomplete one of the second step");
    }

    /// <summary>
    /// A step without a folded snapshot is a gap in every line and in the other programs, while a program a step's snapshots do not name reads 0 there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsMarksStepsWithoutSnapshotsAsGaps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1), ProgramSamples.Program("b", 1, 10, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(11), ProgramSamples.Source, ProgramSamples.Program("a", 1, 300, 1)));
        await FoldAsync(store);

        // Act
        var result = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(5), ProgramMeasure.Memory, 2), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["a", "b"], Names(result), "both programs are named");
        Assert.AreSequenceEqual(["100/100/100", "gap", "300/300/300"], Points(result, "a"), "the empty middle step is a gap");
        Assert.AreSequenceEqual(["10/10/10", "gap", "0/0/0"], Points(result, "b"), "a step with snapshots that do not name the program reads 0, not a gap");
        Assert.AreSequenceEqual([0.0, null, 0.0], result.Others, "the other programs are a gap in the middle step");
        Assert.AreSequenceEqual([0, 0, 0], result.Incomplete, "no snapshot is incomplete");
    }

    /// <summary>
    /// A step reads the tier of its resolution: a 30-minute step the 5-minute buckets, which a range starting before their period gives as gaps and then values with the starts-before flag set, a range wholly before it
    /// as no lines with the flag set; an hourly step reads the hourly buckets, which still hold that range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReadsTheTierOfTheStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = new RetentionPolicy
                     {
                         Raw = RetentionSamples.Days(1),
                         FiveMinuteRollups = RetentionSamples.Days(1),
                         HourlyRollups = RetentionSamples.Days(365),
                         Logs = RetentionSamples.Days(1),
                         Snapshots = RetentionSamples.Days(1),
                         Events = RetentionSamples.Days(365)
                     };
        var now = Samples.Base.AddDays(10);
        var half = TimeSpan.FromMinutes(30);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(now.AddDays(-3).AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(now.AddHours(-1).AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 300, 3)));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        // Act
        var across = await store.ProgramRollupsAsync(Query(now.AddHours(-26), now, half, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var before = await store.ProgramRollupsAsync(Query(now.AddDays(-4), now.AddDays(-2), half, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var hourly = await store.ProgramRollupsAsync(Query(now.AddDays(-3), now.AddDays(-2), TimeSpan.FromHours(1), ProgramMeasure.Memory, 5), TestContext.CancellationToken);

        // Assert
        var index = across.Times.ToList().IndexOf(now.AddHours(-1));
        var points = Points(across, Nginx);

        Assert.HasCount(52, across.Times, "26 hours in steps of 30 minutes");
        Assert.AreEqual(50, index, "the step that holds the folded snapshot");
        Assert.AreEqual("300/300/300", points[index], "the 5-minute bucket still holds the snapshot");
        Assert.HasCount(1, points.Where(point => point != "gap").ToList(), "the steps before the 5-minute buckets are gaps");
        Assert.IsTrue(across.StartsBeforeBuckets, "the range starts before the oldest 5-minute bucket");
        Assert.IsEmpty(before.Programs, "a range wholly before the 5-minute buckets has no lines");
        Assert.IsTrue(before.StartsBeforeBuckets, "and starts before them");
        Assert.AreEqual("100/100/100", Points(hourly, Nginx)[0], "the hourly bucket still holds the old snapshot");
        Assert.IsFalse(hourly.StartsBeforeBuckets, "the range does not start before the oldest hourly bucket");
    }

    /// <summary>
    /// The result says up to when the source's snapshots are folded through the earliest capture time of its snapshots not yet folded, and whether the range starts before the oldest bucket of the tier.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReturnsEarliestUnfoldedAndStartsBefore()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);
        var hour = TimeSpan.FromHours(1);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddSeconds(5), "other", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)));

        var beforeFold = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);

        await FoldAsync(store);
        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(10), "other", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(30), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)));

        // Act
        var pending = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);

        await FoldAsync(store);

        var folded = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var earlier = await store.ProgramRollupsAsync(Query(Samples.Base.AddMinutes(-10), Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var later = await store.ProgramRollupsAsync(Query(Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var hourlyEarlier = await store.ProgramRollupsAsync(Query(Samples.Base.AddHours(-1), Samples.Base.AddHours(1), hour, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var hourlyAt = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddHours(1), hour, ProgramMeasure.Memory, 5), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(Samples.Base.AddMinutes(1), beforeFold.EarliestUnfolded, "before the fold: the earliest snapshot of the source, not that of the other source");
        Assert.AreEqual(Samples.Base.AddMinutes(30), pending.EarliestUnfolded, "a snapshot stored after the fold is the earliest not yet folded");
        Assert.IsNull(folded.EarliestUnfolded, "after the fold nothing is left");
        Assert.IsTrue(earlier.StartsBeforeBuckets, "the range starts before the oldest 5-minute bucket");
        Assert.IsFalse(folded.StartsBeforeBuckets, "the range starts at the oldest 5-minute bucket");
        Assert.IsFalse(later.StartsBeforeBuckets, "the range starts after it");
        Assert.IsTrue(hourlyEarlier.StartsBeforeBuckets, "the same for the hourly tier");
        Assert.IsFalse(hourlyAt.StartsBeforeBuckets, "an hourly range starting at the oldest hourly bucket does not start before it");
    }

    /// <summary>
    /// An unknown source, an empty range and a range without a bucket return an empty result without an error.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsReturnsEmptyResult()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program(Nginx, 1, 100, 1)));
        await FoldAsync(store);

        // Act
        var unknown = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), five, ProgramMeasure.Memory, 5, "nobody"), TestContext.CancellationToken);
        var empty = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base, five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var reversed = await store.ProgramRollupsAsync(Query(Samples.Base.AddMinutes(10), Samples.Base, five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var none = await store.ProgramRollupsAsync(Query(Samples.Base.AddHours(5), Samples.Base.AddHours(6), five, ProgramMeasure.Cpu, 5), TestContext.CancellationToken);

        // Assert
        AssertEmpty(unknown, "an unknown source");
        AssertEmpty(empty, "an empty range");
        AssertEmpty(reversed, "a range that ends before it starts");
        AssertEmpty(none, "a range without a bucket");
        Assert.IsNull(none.EarliestUnfolded, "nothing is waiting to be folded");
        Assert.IsFalse(none.StartsBeforeBuckets, "the range starts after the oldest bucket");
    }

    /// <summary>
    /// The steps lie on the grid of multiples of the step since the Unix epoch: the first one is the step that holds the start of the range, and it holds all of its bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsAlignsStepsToTheEpoch()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);
        var twoDays = TimeSpan.FromDays(2);
        var twoDayStart = DateTimeOffset.UnixEpoch.AddDays((long)(Samples.Base - DateTimeOffset.UnixEpoch).TotalDays / 2 * 2);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, ProgramSamples.Program("a", 1, 999, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(6), ProgramSamples.Source, ProgramSamples.Program("a", 1, 100, 1)));
        await FoldAsync(store);

        // Act
        var minutes = await store.ProgramRollupsAsync(Query(Samples.Base.AddMinutes(7), Samples.Base.AddMinutes(20), five, ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var halfDays = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddHours(1), TimeSpan.FromHours(12), ProgramMeasure.Memory, 5), TestContext.CancellationToken);
        var days = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddHours(1), twoDays, ProgramMeasure.Memory, 5), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10), Samples.Base.AddMinutes(15)], minutes.Times, "the first step is the 5-minute step that holds the start of the range");
        Assert.AreSequenceEqual(["100/100/100", "gap", "gap"], Points(minutes, "a"), "the first step holds its whole bucket, and the step before the range is not read");
        Assert.AreSequenceEqual([Samples.Base.AddHours(-10)], halfDays.Times, "12-hour steps start at midnight and noon UTC");
        Assert.AreSequenceEqual(["549.5/100/999"], Points(halfDays, "a"), "the half day holds the hourly bucket of both snapshots");
        Assert.AreSequenceEqual([twoDayStart], days.Times, "steps of two days are multiples of two days since the epoch");
    }

    /// <summary>
    /// A query with a count outside 1 to 50, a step that is not one of the query's steps, more than 2,000 steps counted on the epoch grid, a bound outside the storable range or an unknown measure is refused with the exact text;
    /// the largest valid ranges are not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsRejectsInvalidQuery()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var five = TimeSpan.FromMinutes(5);
        var from = Samples.Base;
        var to = Samples.Base.AddHours(1);
        var countText = InvalidPrefix + "count must be 1 to 50";
        var stepText = InvalidPrefix + "step must be one of the query's steps";
        var stepsText = InvalidPrefix + "the range holds more than 2000 steps";
        var rangeText = InvalidPrefix + "from and to must be within the storable range";
        var refused = new List<(ProgramRollupQuery Query, string Text, string Why)>
                      {
                          (Query(from, to, five, ProgramMeasure.Memory, 0), countText, "count 0"),
                          (Query(from, to, five, ProgramMeasure.Memory, 51), countText, "count 51"),
                          (Query(from, to, five, ProgramMeasure.Memory, -1), countText, "a negative count"),
                          (Query(from, to, TimeSpan.FromMinutes(7), ProgramMeasure.Memory, 5), stepText, "a step of 7 minutes"),
                          (Query(from, to, TimeSpan.Zero, ProgramMeasure.Memory, 5), stepText, "a step of zero"),
                          (Query(from, to, TimeSpan.FromHours(4), ProgramMeasure.Memory, 5), stepText, "a step of 4 hours"),
                          (Query(from, to, TimeSpan.FromHours(36), ProgramMeasure.Memory, 5), stepText, "a step that is not whole days"),
                          (Query(from, from.AddDays(365), five, ProgramMeasure.Memory, 5), stepsText, "a year at 5 minutes"),
                          (Query(from.AddMinutes(1), from.AddMinutes(1) + (2000 * five), five, ProgramMeasure.Memory, 5), stepsText, "2,000 steps that overlap 2,001 steps of the epoch grid"),
                          (Query(DateTimeOffset.MinValue, to, five, ProgramMeasure.Memory, 5), rangeText, "a start outside the storable range"),
                          (Query(from, DateTimeOffset.MaxValue, five, ProgramMeasure.Memory, 5), rangeText, "an end outside the storable range"),
                          (Query(from, to, five, (ProgramMeasure)42, 5), InvalidPrefix + "unknown measure", "an unknown measure")
                      };
        var accepted = new List<(ProgramRollupQuery Query, string Why)>
                       {
                           (Query(from, from + (2000 * five), five, ProgramMeasure.Memory, 50), "2,000 steps on the epoch grid and a count of 50"),
                           (Query(from, from.AddDays(6), TimeSpan.FromDays(3), ProgramMeasure.Cpu, 1), "whole days"),
                           (Query(from, to, TimeSpan.FromHours(12), ProgramMeasure.Memory, 1), "12 hours")
                       };

        // Act
        var refusals = new List<string>();

        foreach (var (query, text, why) in refused)
        {
            refusals.Add(await RefusalAsync(store, query, text, why));
        }

        var results = new List<ProgramRollupResult>();

        foreach (var (query, _) in accepted)
        {
            results.Add(await store.ProgramRollupsAsync(query, TestContext.CancellationToken));
        }

        // Assert
        Assert.AreSequenceEqual(refused.Select(entry => entry.Text).ToList(), refusals, "each invalid query is refused with its exact text");
        Assert.HasCount(accepted.Count, results, "every valid query returns a result");
    }

    /// <summary>
    /// A token cancelled while the read's statement runs ends the read in an operation cancelled exception through the reader's interrupt.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreProgramRollupsHonorsCancellation()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = RetentionSamples.Ns(Samples.Base);
        var buckets = $"WITH RECURSIVE c(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM c WHERE x < {ProgramHours - 1}) ";

        await RetentionSamples.ExecuteAsync(directory.Path, buckets + $"INSERT INTO program_rollup_totals_1h SELECT 'processes', {start} + x * 3600000000000, 1, 0, 0, 1, 100.0, 0.0, 1.0 FROM c", TestContext.CancellationToken);
        await RetentionSamples.ExecuteAsync(directory.Path, buckets + $"INSERT INTO program_rollups_1h SELECT 'processes', {start} + x * 3600000000000, 'a', 1, 100.0, 100.0, 100.0, NULL, NULL, 0.0, 1.0, 1.0, 1.0 FROM c", TestContext.CancellationToken);

        await using var lease = await store.LeaseReaderAsync(source.Token);

        lease.Connection.CreateAggregate("sum",
                                         0d,
                                         (double total, double value) =>
                                         {
                                             source.Cancel();

                                             return total + value;
                                         },
                                         total => total);

        var query = Query(Samples.Base, Samples.Base.AddHours(ProgramHours), TimeSpan.FromHours(1), ProgramMeasure.Memory, 5);

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => ProgramRollupQueries.ReadAsync(lease.Connection, query, source.Token), "the cancelled token ends the read");

        // Assert
        Assert.IsTrue(source.IsCancellationRequested, "the token was cancelled while the statement ran");
        Assert.IsNotNull(exception, "the read ends in a cancellation, not in a result or a store error");
    }

    /// <summary>
    /// Sums of forged CPU values that overflow a double are clamped in the read: every average, minimum, maximum and other-programs value is finite.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreProgramRollupsClampsOverflowingSums()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        int[] minutes = [1, 6, 11];
        var snapshots = minutes.Select(minute => ProgramSamples.Snapshot(Samples.Base.AddMinutes(minute), ProgramSamples.Source, ProgramSamples.Program("a", 1, ulong.MaxValue, 1e308), ProgramSamples.Program("b", 1, ulong.MaxValue, 1e308))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, snapshots);
        await FoldAsync(store);

        // Act
        var cpuOne = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15), ProgramMeasure.Cpu, 1), TestContext.CancellationToken);
        var cpuTwo = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15), ProgramMeasure.Cpu, 2), TestContext.CancellationToken);
        var memory = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15), ProgramMeasure.Memory, 1), TestContext.CancellationToken);

        // Assert
        AssertFinite(cpuOne, "CPU with one program named");
        AssertFinite(cpuTwo, "CPU with both programs named");
        AssertFinite(memory, "memory");
        Assert.HasCount(1, cpuOne.Programs, "one program is named");

        var point = cpuOne.Programs[0].Points[0]!.Value;

        Assert.IsGreaterThan(0.0, point.Average, "the clamped average is positive");
        Assert.AreEqual(1e308, point.Minimum, "the minimum is the value as folded");
        Assert.AreEqual(1e308, point.Maximum, "the maximum is the value as folded");
        Assert.IsGreaterThanOrEqualTo(0.0, cpuOne.Others[0]!.Value, "the other programs are never negative");
    }

    /// <summary>
    /// Hostile program names of up to 1,024 bytes come back as received, each ranked with its own values: quotes, a percent sign, SQL text, a NUL and other control characters, a right-to-left override and an emoji.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreProgramRollupsReturnsHostileProgramNamesAsStored()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        string[] names = ["\u0000", "\u0001", "\u202E", "\U0001F600", "q\"u'o\\te", "100%_", "x'); DROP TABLE records; --", new string('a', 1024)];
        var programs = names.Select((name, index) => ProgramSamples.Program(name, 1, (ulong)((names.Length - index) * 1000), 1)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), ProgramSamples.Source, programs));
        await FoldAsync(store);

        // Act
        var result = await store.ProgramRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5), ProgramMeasure.Memory, 8), TestContext.CancellationToken);
        var kept = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM sqlite_master WHERE name = 'records'", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(names, Names(result), "every name comes back as received, ranked by its memory");

        for (var index = 0; index < names.Length; index++)
        {
            var value = ((names.Length - index) * 1000).ToString(CultureInfo.InvariantCulture);

            Assert.AreSequenceEqual([$"{value}/{value}/{value}"], Points(result, names[index]), $"the values of the name at rank {index}");
        }

        Assert.AreSequenceEqual([0.0], result.Others, "no program is left out");
        Assert.AreEqual(1L, kept, "the SQL text in a name is not run");
    }

    /// <summary>
    /// The source list returns each source with per-program buckets in the range, by name, with the start of its latest bucket of either tier within the range; a bucket after the range is left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreProgramSourcesListsLatestBucketInRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), "web", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddHours(2).AddMinutes(7), "web", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddHours(5), "web", ProgramSamples.Program(Nginx, 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(30).AddSeconds(10), "db", ProgramSamples.Program("mysql", 1, 100, 1)),
                                          ProgramSamples.Snapshot(Samples.Base.AddHours(5).AddMinutes(1), "late", ProgramSamples.Program("cron", 1, 100, 1)));
        await FoldAsync(store);

        // Act
        var list = await store.ProgramSourcesAsync(new ProgramSourceQuery
                                                   {
                                                       From = Samples.Base,
                                                       To = Samples.Base.AddHours(3)
                                                   },
                                                   TestContext.CancellationToken);
        var empty = await store.ProgramSourcesAsync(new ProgramSourceQuery
                                                    {
                                                        From = Samples.Base.AddDays(2),
                                                        To = Samples.Base.AddDays(3)
                                                    },
                                                    TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["db", "web"], list.Sources.Select(source => source.Source).ToList(), "the sources with a bucket in the range, by name");
        Assert.AreSequenceEqual([Samples.Base.AddMinutes(30), Samples.Base.AddHours(2).AddMinutes(5)], list.Sources.Select(source => source.LatestBucket).ToList(), "the latest bucket of each source within the range, the 5-minute one of the later tier start");
        Assert.IsFalse(list.More, "the list is complete");
        Assert.IsEmpty(empty.Sources, "a range without a bucket lists no source");
        Assert.IsFalse(empty.More, "and has no more");
    }

    /// <summary>
    /// The source list holds at most 256 sources and says when there are more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreProgramSourcesCapsAtTheLimit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = new ProgramSourceQuery
                    {
                        From = Samples.Base,
                        To = Samples.Base.AddHours(1)
                    };

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. Enumerable.Range(0, StorageLimits.MaxProgramSources).Select(SourceSnapshot)]);
        await FoldAsync(store);

        var exact = await store.ProgramSourcesAsync(query, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, SourceSnapshot(StorageLimits.MaxProgramSources));
        await FoldAsync(store);

        // Act
        var capped = await store.ProgramSourcesAsync(query, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(StorageLimits.MaxProgramSources, exact.Sources, "256 sources fit");
        Assert.IsFalse(exact.More, "and are not more than the list holds");
        Assert.HasCount(StorageLimits.MaxProgramSources, capped.Sources, "257 sources are cut to 256");
        Assert.IsTrue(capped.More, "the list says there are more");
        Assert.AreEqual("s255", capped.Sources[^1].Source, "the first 256 by name are kept");
    }

    /// <summary>
    /// A source query with a bound outside the storable range is refused with the exact text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreProgramSourcesRejectsRangeOutsideStorableRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var start = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ProgramSourcesAsync(new ProgramSourceQuery
                                                                                                           {
                                                                                                               From = DateTimeOffset.MinValue,
                                                                                                               To = Samples.Base
                                                                                                           },
                                                                                                           TestContext.CancellationToken),
                                                                           "a start outside the range");
        var end = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ProgramSourcesAsync(new ProgramSourceQuery
                                                                                                         {
                                                                                                             From = Samples.Base,
                                                                                                             To = DateTimeOffset.MaxValue
                                                                                                         },
                                                                                                         TestContext.CancellationToken),
                                                                         "an end outside the range");

        // Assert
        Assert.AreEqual(RangeText, start.Message, "the text for the start");
        Assert.AreEqual(RangeText, end.Message, "the text for the end");
    }

    /// <summary>
    /// Creates a query of the test source.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="step">The step</param>
    /// <param name="measure">The measure</param>
    /// <param name="count">The number of programs</param>
    /// <param name="source">The source</param>
    /// <returns>The query</returns>
    private static ProgramRollupQuery Query(DateTimeOffset from, DateTimeOffset to, TimeSpan step, ProgramMeasure measure, int count, string source = ProgramSamples.Source)
    {
        return new ProgramRollupQuery
               {
                   Source = source,
                   From = from,
                   To = to,
                   Step = step,
                   Measure = measure,
                   Count = count
               };
    }

    /// <summary>
    /// Gets the names of the lines of a result, in order.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The names</returns>
    private static List<string> Names(ProgramRollupResult result)
    {
        return [.. result.Programs.Select(line => line.Program)];
    }

    /// <summary>
    /// Describes the points of a program as average, minimum and maximum joined with slashes, a gap as <c>gap</c>.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="program">The program</param>
    /// <returns>The points as text</returns>
    private static List<string> Points(ProgramRollupResult result, string program)
    {
        var line = result.Programs.Single(candidate => candidate.Program == program);

        return [.. line.Points.Select(Describe)];
    }

    /// <summary>
    /// Describes one point.
    /// </summary>
    /// <param name="point">The point, <c>null</c> for a gap</param>
    /// <returns>The text</returns>
    private static string Describe(ProgramRollupPoint? point)
    {
        return point is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value.Average}/{value.Minimum}/{value.Maximum}") : "gap";
    }

    /// <summary>
    /// Creates the snapshot of the source with the given number, in the first bucket.
    /// </summary>
    /// <param name="number">The number</param>
    /// <returns>The record</returns>
    private static DataRecord SourceSnapshot(int number)
    {
        return ProgramSamples.Snapshot(Samples.Base.AddMinutes(1), string.Create(CultureInfo.InvariantCulture, $"s{number:D3}"), ProgramSamples.Program(Nginx, 1, 100, 1));
    }

    /// <summary>
    /// Checks that a result has no lines, steps or counts.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="why">The case</param>
    private static void AssertEmpty(ProgramRollupResult result, string why)
    {
        Assert.IsEmpty(result.Programs, $"{why}: no lines");
        Assert.IsEmpty(result.Times, $"{why}: no steps");
        Assert.IsEmpty(result.Others, $"{why}: no other programs");
        Assert.IsEmpty(result.Incomplete, $"{why}: no incomplete counts");
    }

    /// <summary>
    /// Checks that every value of a result is a finite number.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="why">The case</param>
    private static void AssertFinite(ProgramRollupResult result, string why)
    {
        var values = result.Programs.SelectMany(line => line.Points).Where(point => point is not null).SelectMany(point => new[] { point!.Value.Average, point.Value.Minimum, point.Value.Maximum }).Concat(result.Others.Where(other => other is not null).Select(other => other!.Value)).ToList();

        Assert.IsNotEmpty(values, $"{why}: the result holds values");
        Assert.IsTrue(values.All(double.IsFinite), $"{why}: every value is finite");
    }

    /// <summary>
    /// Creates the program of a number: its name is the number in three digits.
    /// </summary>
    /// <param name="number">The number</param>
    /// <returns>The program of 100 bytes and 1 % CPU</returns>
    private static ProgramAggregate Named(int number)
    {
        return ProgramSamples.Program(string.Create(CultureInfo.InvariantCulture, $"n{number:D3}"), 1, 100, 1);
    }

    /// <summary>
    /// Counts the rows a step reads: up to and including the row that takes the summed bytes past the budget.
    /// </summary>
    /// <param name="lengths">The stored bytes of each snapshot, in ID order</param>
    /// <returns>The number of rows</returns>
    private static int RowsWithinBudget(List<long> lengths)
    {
        var sum = 0L;

        for (var index = 0; index < lengths.Count; index++)
        {
            sum += lengths[index];

            if (sum > StorageLimits.ProgramRollupStepBytes)
            {
                return index + 1;
            }
        }

        return lengths.Count;
    }

    /// <summary>
    /// Reads the stored UTF-8 length of the data of every process snapshot.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the lengths in ID order</returns>
    private async Task<List<long>> LengthsAsync(string directory)
    {
        var rows = await ProgramSamples.RowsAsync(directory, "SELECT length(CAST(data AS BLOB)) FROM records WHERE kind = 'process_snapshot' ORDER BY id", TestContext.CancellationToken);

        return [.. rows.Select(row => long.Parse(row, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Runs a refused read and returns the message of its exception.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="query">The query</param>
    /// <param name="expected">The expected message</param>
    /// <param name="why">The case</param>
    /// <returns>A task that returns the message</returns>
    private async Task<string> RefusalAsync(SqliteStore store, ProgramRollupQuery query, string expected, string why)
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ProgramRollupsAsync(query, TestContext.CancellationToken), why);

        Assert.AreEqual(expected, exception.Message, why);

        return exception.Message;
    }

    /// <summary>
    /// Folds every stored snapshot with the default periods.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that completes when everything is folded</returns>
    private async Task FoldAsync(SqliteStore store)
    {
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddDays(1), TestContext.CancellationToken);
    }

    #endregion // Methods
}