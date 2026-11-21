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
        string[] names = ["\u0000", "\u0001", "‮", "\U0001F600", "q\"u'o\\te", "100%_", "x'); DROP TABLE records; --", new string('a', 1024)];
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

    #endregion // Methods
}