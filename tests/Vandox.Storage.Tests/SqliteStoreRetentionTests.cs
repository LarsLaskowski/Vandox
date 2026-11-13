using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;
using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the removal of expired data, the resend rule and the kill holds of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public partial class SqliteStoreRetentionTests
{
    #region Constants

    private const string Marker = "marker";

    private const int LoadHours = 12;

    private const int LoadSeries = 5;

    private const string GapList = "SELECT kind || ' ' || state || ' ' || from_seq FROM gaps ORDER BY id";

    private const string SnapshotSeqs = "SELECT seq FROM records WHERE kind = 'connection_snapshot' ORDER BY seq";

    private const string KeptTables = "metric_rollups_5m:source,name,labels,bucket,min_value,max_value,sum_value,count,last_value,last_at,last_record_id;"
                                      + "metric_rollups_1h:source,name,labels,bucket,min_value,max_value,sum_value,count,last_value,last_at,last_record_id;"
                                      + "log_templates:id,source,program,template,first_seen,last_seen,count;log_template_hours:template_id,hour,count;log_source_hours:source,hour,count;"
                                      + "gaps:id,agent_id,kind,cause,state,first_seq,last_seq,from_seq,to_seq,from_at,to_at,detected_at,changed_at;"
                                      + "records:id,kind,origin,source,agent_id,seq,captured_at,received_at,boot_id,clock_offset_ns,data,live";

    private const string KeptRecordKinds = " WHERE kind IN ('kernel_event', 'service_state', 'backup_run', 'gap')";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An agent record resent after its stored copy was removed is acknowledged as a duplicate, not stored again and not merged into its bucket, and adds, changes or drops no gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreWriteBatchSkipsResendWhoseCopyWasRemoved()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);
        var old = TimelineSamples.OldRun(1, 5, Samples.Base, Samples.Base.AddMinutes(1));

        await store.WriteBatchAsync(old, TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(6, 1, now), TestContext.CancellationToken);
        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var removed = await CountAsync(directory, "SELECT count(*) FROM records WHERE seq BETWEEN 1 AND 5");
        var buckets = await RollupRowsAsync(directory);
        var gapsBefore = await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken);

        // Act
        var result = await store.WriteBatchAsync(TimelineSamples.Resend(old, now.AddSeconds(1)), TestContext.CancellationToken);

        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);

        var stored = await CountAsync(directory, "SELECT count(*) FROM records WHERE seq BETWEEN 1 AND 5");
        var gaps = await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, removed, "the five records were removed before the resend");
        Assert.AreEqual(new WriteResult(0, 5), result, "the resend is acknowledged as five duplicates");
        Assert.AreEqual(0L, stored, "nothing of the resend is stored");
        Assert.AreSequenceEqual(buckets, await RollupRowsAsync(directory), "the buckets are unchanged");
        Assert.IsEmpty(TimelineSamples.SequenceGaps(gaps), "no sequence gap is reported");
        Assert.AreEqual(TimelineSamples.Fingerprint(gapsBefore), TimelineSamples.Fingerprint(gaps), "the resend adds, changes or drops no gap");
    }

    /// <summary>
    /// A number inside a sequence gap that is open or final is a late fill: it is stored after a removal pass, not taken for a resend.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreWriteBatchStoresNumberInOpenOrFinalGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);

        await store.WriteBatchAsync(TimelineSamples.Run(1, 3, Samples.Base.AddSeconds(60)), TestContext.CancellationToken);
        await store.WriteBatchAsync(Rebooted(TimelineSamples.Run(10, 2, Samples.Base.AddSeconds(120))), TestContext.CancellationToken);
        await store.WriteBatchAsync(Rebooted(TimelineSamples.Run(12, 1, Samples.Base.AddSeconds(420))), TestContext.CancellationToken);
        await store.WriteBatchAsync(Rebooted(TimelineSamples.Run(20, 1, now)), TestContext.CancellationToken);

        var arranged = TimelineSamples.SequenceGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));

        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        var removed = await CountAsync(directory, "SELECT count(*) FROM records WHERE seq BETWEEN 1 AND 12");

        // Act
        var result = await store.WriteBatchAsync(Rebooted(TimelineSamples.Batch(now.AddSeconds(1), TimelineSamples.Metric(5, Samples.Base.AddSeconds(70)), TimelineSamples.Metric(15, Samples.Base.AddSeconds(450)))), TestContext.CancellationToken);
        var stored = await CountAsync(directory, "SELECT count(*) FROM records WHERE seq IN (5, 15)");

        // Assert
        Assert.HasCount(2, arranged, "two sequence gaps are arranged");
        Assert.StartsWith("4-9 final", arranged[0], "the gap of the reboot jump is final");
        Assert.StartsWith("13-19 open", arranged[1], "the jump of the latest batch is open");
        Assert.AreEqual(0L, removed, "the records below both gaps were removed");
        Assert.AreEqual(new WriteResult(2, 0), result, "both fills are stored");
        Assert.AreEqual(2L, stored, "the numbers in the final and the open gap are stored");
    }

    /// <summary>
    /// In one batch a resend of a removed number is skipped while a fill of an open gap and a new number are stored, decided against the rows as loaded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task SqliteStoreWriteBatchMixedResendFillAndNewStoresOnlyFillAndNew()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);

        await store.WriteBatchAsync(TimelineSamples.Run(1, 3, Samples.Base.AddSeconds(60)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(6, 1, Samples.Base.AddSeconds(120)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(7, 1, now), TestContext.CancellationToken);
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        // Act
        var result = await store.WriteBatchAsync(TimelineSamples.Batch(now.AddSeconds(1), TimelineSamples.Metric(9, now), TimelineSamples.Metric(2, Samples.Base.AddSeconds(58)), TimelineSamples.Metric(4, Samples.Base.AddSeconds(100))), TestContext.CancellationToken);
        var stored = await RowsAsync(directory, "SELECT seq FROM records WHERE agent_id = 'agent-1' ORDER BY seq");
        var gaps = await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken);
        var sequenceGaps = TimelineSamples.SequenceGaps(gaps);

        // Assert
        Assert.AreEqual(2, result.Stored, "the fill and the new number are stored");
        Assert.AreEqual(1, result.Duplicates, "the resend is a duplicate");
        Assert.AreSequenceEqual(["4", "7", "9"], stored, "the removed number 2 is not stored again");
        Assert.Contains(gap => gap.StartsWith("8-8 ", StringComparison.Ordinal), sequenceGaps, $"the jump to 9 opens the gap 8-8: {string.Join("; ", sequenceGaps)}");
        Assert.DoesNotContain(gap => gap.Kind == GapKind.Sequence && gap.FirstSeq <= 4 && gap.LastSeq >= 4, gaps, $"no sequence gap contains the filled number 4: {string.Join("; ", sequenceGaps)}");
    }

    /// <summary>
    /// Each kind is removed only once past its period: a record captured one second before the limit stays, one captured one second after it goes.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="days">The default period of the kind in days</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(RecordKind.Metric, 30)]
    [DataRow(RecordKind.LogLine, 90)]
    [DataRow(RecordKind.ProcessSnapshot, 30)]
    [DataRow(RecordKind.ConnectionSnapshot, 30)]
    [DataRow(RecordKind.MariaDbStatus, 30)]
    [DataRow(RecordKind.KernelEvent, 1095)]
    [DataRow(RecordKind.ServiceState, 1095)]
    [DataRow(RecordKind.BackupRun, 1095)]
    [DataRow(RecordKind.Gap, 1095)]
    public async Task SqliteStoreRemoveExpiredKeepsOneSecondBeforeAndRemovesOneSecondAfter(string kind, int days)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var expired = Samples.Base;
        var kept = Samples.Base.AddSeconds(2);
        var now = Samples.Base.AddDays(days).AddSeconds(1);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.OfKind(kind, RecordOrigin.Backend, expired), RetentionSamples.OfKind(kind, RecordOrigin.Backend, kept));
        await WriteMarkerAsync(store, now);

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE kind = '{kind}' AND captured_at = {RetentionSamples.Ns(expired)}"), $"the {kind} one second past its period is removed");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE kind = '{kind}' AND captured_at = {RetentionSamples.Ns(kept)}"), $"the {kind} one second before its period ends stays");
    }

    /// <summary>
    /// With the default periods incidents are kept however old they are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreRemoveExpiredKeepsIncidentsByDefault()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(5000);

        await store.SaveIncidentAsync(IncidentAt(Samples.Base), Samples.Base.AddDays(1), TestContext.CancellationToken);
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, steps.Sum(step => step.RemovedIncidents), "no incident is reported removed");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM incidents"), "the incident stays");
    }

    /// <summary>
    /// With a finite incident period an incident whose window ended before it is removed, a later one stays, and the removed number is not given again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreRemoveExpiredRemovesExpiredIncidentWithoutNote()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var expired = IncidentAt(Samples.Base);
        var kept = IncidentAt(Samples.Base.AddDays(30));
        var now = expired.End.AddDays(365).AddSeconds(1);

        await store.SaveIncidentAsync(kept, Samples.Base.AddDays(31), TestContext.CancellationToken);

        var expiredId = (await store.SaveIncidentAsync(expired, Samples.Base.AddDays(32), TestContext.CancellationToken)).Id;

        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, WithIncidents(RetentionSamples.Days(365)), now, [], TestContext.CancellationToken);
        var later = await store.SaveIncidentAsync(IncidentAt(Samples.Base.AddDays(60)), now, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, steps.Sum(step => step.RemovedIncidents), "one incident is reported removed");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM incidents WHERE id = {expiredId}"), "the expired incident is removed");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM incidents WHERE started_at = {RetentionSamples.Ns(kept.Start)}"), "the incident inside the period stays");
        Assert.IsGreaterThan(expiredId, later.Id, "the removed number, the highest one, is never given again");
    }

    /// <summary>
    /// An expired incident with a note stays.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreRemoveExpiredKeepsExpiredIncidentWithNote()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var incident = IncidentAt(Samples.Base);
        var now = incident.End.AddDays(400);

        await SaveWithNoteAsync(store, incident);
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, WithIncidents(RetentionSamples.Days(365)), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, steps.Sum(step => step.RemovedIncidents), "no incident is reported removed");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM incidents"), "the incident with a note stays");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM incident_notes"), "its note stays");
    }

    /// <summary>
    /// A record captured in the future is kept until its capture time is past its period.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task SqliteStoreRemoveExpiredKeepsFutureCaptureTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var future = Samples.Base.AddDays(10);
        var past = future.AddDays(30).AddSeconds(1);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, future, "cpu", 1), RetentionSamples.Line(RecordOrigin.Backend, future, "from the future"));
        await WriteMarkerAsync(store, past);

        // Act
        await RetentionSamples.RetainAsync(store, policy, Samples.Base, [], TestContext.CancellationToken);

        var atBase = await CountAsync(directory, $"SELECT count(*) FROM records WHERE captured_at = {RetentionSamples.Ns(future)}");

        await RetentionSamples.RetainAsync(store, policy, past, [], TestContext.CancellationToken);

        var metrics = await CountAsync(directory, $"SELECT count(*) FROM records WHERE kind = 'metric' AND captured_at = {RetentionSamples.Ns(future)}");
        var lines = await CountAsync(directory, $"SELECT count(*) FROM records WHERE kind = 'log_line' AND captured_at = {RetentionSamples.Ns(future)}");

        // Assert
        Assert.AreEqual(2L, atBase, "both future records stay while their capture time lies ahead");
        Assert.AreEqual(0L, metrics, "the metric goes once its capture time is past the raw period");
        Assert.AreEqual(1L, lines, "the log line stays inside the log period");
    }

    /// <summary>
    /// A removal pass never removes metric values the fold has not read yet; after the next fold they go, and their buckets hold every value.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreRemoveExpiredNeverRemovesMetricsAboveTheWatermark()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldMetrics("first", 3));
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldMetrics("second", 3));
        await WriteMarkerAsync(store, now);
        await RetentionSamples.MarkScannedAsync(store, [], TestContext.CancellationToken);

        // Act
        await RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken);

        var first = await CountAsync(directory, "SELECT count(*) FROM metrics WHERE name = 'first'");
        var second = await CountAsync(directory, "SELECT count(*) FROM metrics WHERE name = 'second'");

        await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);

        var afterFold = await CountAsync(directory, "SELECT count(*) FROM metrics WHERE name = 'second'");
        var folded = await CountAsync(directory, "SELECT sum(count) FROM metric_rollups_5m WHERE name IN ('first', 'second')");

        // Assert
        Assert.AreEqual(0L, first, "the folded values are removed");
        Assert.AreEqual(3L, second, "the values above the watermark stay");
        Assert.AreEqual(0L, afterFold, "the values go after the next fold");
        Assert.AreEqual(6L, folded, "the buckets hold all six values");
    }

    /// <summary>
    /// A step that fails rolls back completely, and the next pass completes the work.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreRemoveExpiredFailedStepLeavesDatabaseUnchanged()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(91);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldLines("old line", 3));
        await WriteMarkerAsync(store, now);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.MarkScannedAsync(store, [], TestContext.CancellationToken);
        await RetentionSamples.ExecuteAsync(directory.Path, "CREATE TRIGGER retention_fails BEFORE DELETE ON log_lines BEGIN SELECT RAISE(ABORT, 'disk full'); END", TestContext.CancellationToken);

        var before = await FingerprintAsync(directory);

        // Act
        await Assert.ThrowsExactlyAsync<StoreException>(() => RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken), "the failing step");

        var after = await FingerprintAsync(directory);

        await RetentionSamples.ExecuteAsync(directory.Path, "DROP TRIGGER retention_fails", TestContext.CancellationToken);

        var retry = await RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(before, after, "the failed step left the database as before it");
        Assert.AreEqual(3, retry.Sum(step => step.LogLines), "the next pass removes the lines");
        Assert.AreEqual(0L, await CountAsync(directory, "SELECT count(*) FROM log_fts WHERE log_fts MATCH 'old'"), "the search index holds none of them");
    }

    /// <summary>
    /// While another connection holds the write lock, a step waits the busy timeout, throws and changes nothing; the next pass completes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreRemoveExpiredBusyStepThrowsAndChangesNothing()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(91);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldLines("old line", 3));
        await WriteMarkerAsync(store, now);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.MarkScannedAsync(store, [], TestContext.CancellationToken);

        var before = await FingerprintAsync(directory);

        await using var blocker = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory.Path)};Pooling=False");

        await blocker.OpenAsync(TestContext.CancellationToken);
        await ExecuteOnAsync(blocker, "BEGIN IMMEDIATE");

        // Act
        await Assert.ThrowsExactlyAsync<StoreException>(() => RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken), "the busy step");

        var after = await FingerprintAsync(directory);

        await ExecuteOnAsync(blocker, "ROLLBACK");

        var retry = await RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(before, after, "the busy step changed nothing");
        Assert.AreEqual(3, retry.Sum(step => step.LogLines), "the next pass removes the lines");
    }

    /// <summary>
    /// The record with the highest ID is kept even when expired, so a record ID is never given again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreRemoveExpiredKeepsTheHighestRecordId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(91);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldMetrics("cpu", 3));

        var highest = await CountAsync(directory, "SELECT max(id) FROM records");

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        var left = await RowsAsync(directory, "SELECT id FROM records");

        await WriteMarkerAsync(store, now);

        var next = await CountAsync(directory, "SELECT max(id) FROM records");

        // Assert
        Assert.AreSequenceEqual([highest.ToString(CultureInfo.InvariantCulture)], left, "only the record with the highest ID stays");
        Assert.AreEqual(highest + 1, next, "the next record gets a new ID");
    }

    /// <summary>
    /// A database of version 13 is migrated to 14, and its stored metric values are not removed before the fold has read them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreMigrationFromVersion13FoldsBeforeRemoval()
    {
        // Arrange
        using var directory = new TempDirectory();
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(31);

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldMetrics("cpu", 5));
            await WriteMarkerAsync(store, now);
        }

        await RetentionSamples.ExecuteAsync(directory.Path, $"{RetentionSamples.UndoStepFourteen}; UPDATE meta SET value = '13' WHERE key = 'schema_version'", TestContext.CancellationToken);

        // Act
        await using var reopened = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var version = await RowsAsync(directory, "SELECT value FROM meta WHERE key = 'schema_version'");
        var tables = await CountAsync(directory, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('metric_rollups_5m', 'metric_rollups_1h', 'retention_holds')");
        var indexes = await CountAsync(directory, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('records_removable', 'gaps_to_at')");

        await RetentionSamples.MarkScannedAsync(reopened, [], TestContext.CancellationToken);
        await RetentionSamples.RemovePassAsync(reopened, policy, now, TestContext.CancellationToken);

        var beforeFold = await CountAsync(directory, "SELECT count(*) FROM metrics WHERE name = 'cpu'");

        await RetentionSamples.RetainAsync(reopened, policy, now, [], TestContext.CancellationToken);

        var afterFold = await CountAsync(directory, "SELECT count(*) FROM metrics WHERE name = 'cpu'");
        var folded = await CountAsync(directory, "SELECT sum(count) FROM metric_rollups_5m WHERE name = 'cpu'");

        // Assert
        Assert.AreSequenceEqual(["14"], version, "schema version after the migration");
        Assert.AreEqual(3L, tables, "the rollup and hold tables exist");
        Assert.AreEqual(2L, indexes, "the removal and gap indexes exist");
        Assert.AreEqual(5L, beforeFold, "no value is removed before the fold read it");
        Assert.AreEqual(0L, afterFold, "the values go after the fold");
        Assert.AreEqual(5L, folded, "the bucket holds the five values");
    }

    /// <summary>
    /// An ingest batch that arrives during a pass waits at most one step: it is committed before the next step starts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreWriteBatchWaitsAtMostOneRetentionStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(91);
        var held = Samples.Base.AddDays(-1);
        var calls = 0;
        var committedAtSecondStep = -1L;
        Task<WriteResult>? ingest = null;

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. ImportedLines(Samples.Base.AddDays(-2), 50), .. LinesFrom(held, 50, "held"), .. OldLines("old line", 200)]);
        await WriteMarkerAsync(store, now);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.MarkScannedAsync(store, [RetentionSamples.Hold(held.AddMinutes(-1), held.AddHours(1))], TestContext.CancellationToken);

        store.RetentionStepStarted = async () =>
                                     {
                                         calls++;

                                         if (calls == 1)
                                         {
                                             ingest = store.WriteBatchAsync(Samples.Batch(RetentionSamples.Line(RecordOrigin.Backend, now, "ingest during retention")), TestContext.CancellationToken);
                                         }
                                         else if (calls == 2)
                                         {
                                             committedAtSecondStep = await CountAsync(directory, "SELECT count(*) FROM log_lines WHERE message = 'ingest during retention'");
                                         }
                                     };

        // Act
        var steps = await RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken);

        store.RetentionStepStarted = null;

        var result = ingest is null ? default : await ingest;

        // Assert
        Assert.IsGreaterThanOrEqualTo(2, steps.Count, "the pass takes more than one step");
        Assert.AreEqual(steps.Count, calls, "the hook runs once per step");
        Assert.AreEqual(1L, committedAtSecondStep, "the batch queued during the first step is committed before the second step starts");
        Assert.AreEqual(new WriteResult(1, 0), result, "the batch is stored");
        Assert.AreEqual(200, steps.Sum(step => step.LogLines), "the pass removes the expired lines");
    }

    /// <summary>
    /// One step removes at most the step bound of records, of one kind, and the pass continues in further steps.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreRemoveExpiredStepTouchesAtMostTheStepBound()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(91);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldLines("old line", StorageLimits.RemovalStepRecords + 1000));
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, policy, now, [], TestContext.CancellationToken);
        var largest = steps.Max(step => Counts(step).Max());
        var kinds = steps.Max(step => Counts(step).Count(count => count > 0));

        // Assert
        Assert.AreEqual(StorageLimits.RemovalStepRecords + 1000, steps.Sum(step => step.LogLines), "every expired line is removed");
        Assert.IsLessThanOrEqualTo(StorageLimits.RemovalStepRecords, largest, "no step removes more than the step bound");
        Assert.AreEqual(1, kinds, "a step removes rows of one kind");
        Assert.IsGreaterThanOrEqualTo(2, steps.Count(step => step.LogLines > 0), "the lines take more than one step");
        Assert.AreEqual(0L, await CountAsync(directory, "SELECT count(*) FROM log_lines WHERE message LIKE 'old line%'"), "no expired line is left");
    }

    /// <summary>
    /// Imported rows and rows in a hold below the horizon, more than one step's worth and captured before the removable ones, do not stop the pass from reaching the removable rows.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreRemoveExpiredReachesRowsBehindKeptRows()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var now = Samples.Base.AddDays(91);
        var held = Samples.Base.AddDays(-1);
        var bulk = StorageLimits.RemovalStepRecords + 100;

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ImportedLines(Samples.Base.AddDays(-2), bulk));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, LinesFrom(held, bulk, "held"));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldLines("old line", 100));
        await WriteMarkerAsync(store, now);
        await RetentionSamples.FoldAllAsync(store, policy, now, TestContext.CancellationToken);
        await RetentionSamples.MarkScannedAsync(store, [RetentionSamples.Hold(held.AddMinutes(-1), held.AddHours(1))], TestContext.CancellationToken);

        // Act
        var steps = await RetentionSamples.RemovePassAsync(store, policy, now, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(100, steps.Sum(step => step.LogLines), "the removable lines behind the kept ones are removed");
        Assert.AreEqual(0L, await CountAsync(directory, "SELECT count(*) FROM log_lines WHERE message LIKE 'old line%'"), "no removable line is left");
        Assert.AreEqual((long)bulk, await CountAsync(directory, "SELECT count(*) FROM records WHERE origin = 'import'"), "the imported lines stay");
        Assert.AreEqual((long)bulk, await CountAsync(directory, "SELECT count(*) FROM log_lines WHERE message LIKE 'held%'"), "the held lines stay");
    }

    /// <summary>
    /// Every statement of the retention queries is planned with index searches only, never a full scan of a table.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreRemoveExpiredQueriesUseIndexSearches()
    {
        // Arrange
        using var directory = new TempDirectory();

        await using (await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            // the store creates the schema of this build
        }

        var queries = typeof(SqliteStore).Assembly.GetType("Vandox.Storage.RetentionQueries");
        var statements = queries is null ? [] : StatementsOf(queries);
        var failures = new List<string>();

        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory.Path)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        // Act
        foreach (var (name, sql) in statements)
        {
            var plan = await PlanAsync(connection, sql);

            failures.AddRange(plan.Where(detail => detail.StartsWith("SCAN ", StringComparison.Ordinal) && detail != "SCAN CONSTANT ROW").Select(detail => $"{name}: {detail}"));

            if (plan.Count > 0 && plan.Any(detail => detail.StartsWith("SEARCH ", StringComparison.Ordinal)) == false)
            {
                failures.Add($"{name}: no index search");
            }
        }

        // Assert
        Assert.IsNotNull(queries, "the retention statements are constants of RetentionQueries");
        Assert.IsNotEmpty(statements, "RetentionQueries holds statements");
        Assert.IsEmpty(failures, string.Join("; ", failures));
    }

    /// <summary>
    /// One fold step reads at most the step bound of values; the next step reads the rest.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreFoldRollupsStepReadsAtMostTheStepBound()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = RetentionSamples.Defaults();
        var records = Enumerable.Range(0, StorageLimits.RollupStepRows + 100).Select(index => RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddMilliseconds(index * 10), "cpu", index % 7)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, records);

        // Act
        var first = await store.FoldRollupsAsync(policy, Samples.Base.AddDays(1), TestContext.CancellationToken);
        var second = await store.FoldRollupsAsync(policy, Samples.Base.AddDays(1), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StorageLimits.RollupStepRows, first.Values, "the first step reads the step bound");
        Assert.IsTrue(first.More, "the first step reports more values");
        Assert.AreEqual(100, second.Values, "the second step reads the rest");
        Assert.IsFalse(second.More, "nothing is left after the second step");
    }

    /// <summary>
    /// Under a constant agent load with raw values, log lines and snapshots kept for one period P, the file grows from P to 2P by at most the kept summaries, events, gaps and template statistics written in that span (the summed lengths of their columns and index entries plus 25 %) plus 10 % of its size at P.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task SqliteStoreRetentionKeepsSizeWithinAllowanceUnderConstantLoad()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var period = TimeSpan.FromHours(LoadHours);
        var policy = new RetentionPolicy
                     {
                         Raw = period,
                         FiveMinuteRollups = RetentionSamples.Days(365),
                         HourlyRollups = RetentionSamples.Days(1095),
                         Logs = period,
                         Snapshots = period,
                         Events = RetentionSamples.Days(1095),
                         KillWindowBefore = TimeSpan.FromHours(1),
                         KillWindowAfter = TimeSpan.FromMinutes(30)
                     };

        // Act
        await SimulateAsync(store, policy, 0, LoadHours * 4);

        var sizeAtP = await FileBytesAsync(directory);
        var keptAtP = await KeptBytesAsync(directory);
        var removed = await SimulateAsync(store, policy, LoadHours * 4, LoadHours * 8);
        var sizeAt2P = await FileBytesAsync(directory);
        var keptAt2P = await KeptBytesAsync(directory);
        var allowance = ((keptAt2P - keptAtP) * 1.25) + (sizeAtP * 0.1);

        // Assert
        Assert.IsGreaterThan(0, removed, "the passes between P and 2P remove values");
        Assert.IsLessThanOrEqualTo(sizeAtP + allowance, (double)sizeAt2P, $"size at P {sizeAtP} bytes, at 2P {sizeAt2P} bytes, allowance {allowance:F0} bytes");
    }

    /// <summary>
    /// After removal the log search, the logs view and the context of a hit return no removed line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreSearchReturnsNoRemovedLine()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(91);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, OldLines("zebra crossing old", 3));
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Line(RecordOrigin.Backend, now.AddHours(-1), "zebra crossing new"));
        await WriteMarkerAsync(store, now);

        var kept = await CountAsync(directory, "SELECT record_id FROM log_lines WHERE message = 'zebra crossing new'");

        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        // Act
        var hits = await store.SearchLogsAsync(new LogSearch
                                               {
                                                   Text = "zebra",
                                                   From = Samples.Base.AddDays(-1),
                                                   To = now.AddDays(1),
                                                   Limit = 100
                                               },
                                               TestContext.CancellationToken);
        var page = await store.FindLogsAsync(new LogFilter
                                             {
                                                 From = Samples.Base.AddDays(-1),
                                                 To = now.AddDays(1),
                                                 Limit = 100,
                                                 ScanLimit = 100000
                                             },
                                             TestContext.CancellationToken);
        var context = await store.LogContextAsync(new LogContextQuery
                                                  {
                                                      RecordId = kept,
                                                      Before = 10,
                                                      After = 10
                                                  },
                                                  TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(new[] { kept }, hits.Select(hit => hit.Id).ToArray(), "the search finds only the kept line");
        Assert.AreSequenceEqual(new[] { kept }, page.Lines.Select(line => line.Id).ToArray(), "the logs view shows only the kept line");
        Assert.IsEmpty(context.Before, "the context of the kept line holds no removed line");
    }

    /// <summary>
    /// After the records of an agent were removed no sequence gap is reported for their numbers, the time gaps stay as they were, and the next batch is no new start.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreRemoveExpiredReportsNoGapForRemovedNumbers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);

        await store.WriteBatchAsync(TimelineSamples.Run(1, 10, Samples.Base.AddSeconds(60)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(11, 1, now), TestContext.CancellationToken);

        var timeGapsBefore = TimelineSamples.TimeGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        var removed = await CountAsync(directory, "SELECT count(*) FROM records WHERE seq <= 10");
        var timeGapsAfter = TimelineSamples.TimeGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));
        var next = await store.WriteBatchAsync(TimelineSamples.Run(12, 1, now.AddSeconds(60)), TestContext.CancellationToken);
        var gaps = await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken);
        var live = await TimelineSamples.LiveOfAsync(RetentionSamples.DatabasePath(directory.Path), TimelineSamples.AgentId, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, removed, "the ten records were removed");
        Assert.IsEmpty(TimelineSamples.SequenceGaps(gaps), "no sequence gap is reported for the removed numbers");
        Assert.HasCount(1, timeGapsBefore, "the silence from the old records to the latest one is a time gap");
        Assert.AreEqual(TimelineSamples.Fingerprint(timeGapsBefore), TimelineSamples.Fingerprint(timeGapsAfter), "the pass leaves the time gaps as they were");
        Assert.AreEqual(0, next.Timeline?.Opened ?? 0, "the next batch opens no gap");
        Assert.IsTrue(live[12], "the next record is live, not a new start");
    }

    /// <summary>
    /// A removal pass deletes the sample times below the raw horizon except each agent's newest and stores the horizon as the sample-time floor; with an unlimited raw period nothing is pruned and no floor is stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredPrunesSampleTimesBelowTheRawHorizonKeepingTheNewest()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var unlimitedDirectory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        await using var unlimitedStore = await SqliteStore.OpenAsync(unlimitedDirectory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);
        var horizon = now.AddDays(-30);
        var defaults = RetentionSamples.Defaults();
        var unlimited = new RetentionPolicy
                        {
                            Raw = null,
                            FiveMinuteRollups = defaults.FiveMinuteRollups,
                            HourlyRollups = defaults.HourlyRollups,
                            Logs = defaults.Logs,
                            Snapshots = defaults.Snapshots,
                            Events = defaults.Events,
                            KillWindowBefore = defaults.KillWindowBefore,
                            KillWindowAfter = defaults.KillWindowAfter
                        };

        await WriteSampleTimesAsync(store, now);
        await WriteSampleTimesAsync(unlimitedStore, now);

        // Act
        await RetentionSamples.RetainAsync(store, defaults, now, [], TestContext.CancellationToken);
        await RetentionSamples.RetainAsync(unlimitedStore, unlimited, now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM agent_sample_times WHERE agent_id = 'agent-1' AND captured_at < {RetentionSamples.Ns(horizon)}"), "the sample times of agent-1 below the horizon are pruned");
        Assert.AreEqual(3L, await CountAsync(directory, $"SELECT count(*) FROM agent_sample_times WHERE agent_id = 'agent-1' AND captured_at >= {RetentionSamples.Ns(horizon)}"), "the sample times of agent-1 above the horizon stay");
        Assert.AreSequenceEqual([RetentionSamples.Ns(Samples.Base.AddSeconds(13))], await RowsAsync(directory, "SELECT captured_at FROM agent_sample_times WHERE agent_id = 'agent-2'"), "agent-2, all of whose times lie below the horizon, keeps exactly its newest");
        Assert.AreSequenceEqual([RetentionSamples.Ns(horizon)], await RowsAsync(directory, "SELECT value FROM meta WHERE key = 'sample_time_floor'"), "the floor is the raw horizon");
        Assert.AreEqual(12L, await CountAsync(unlimitedDirectory, "SELECT count(*) FROM agent_sample_times"), "with raw unlimited no sample time is pruned");
        Assert.AreEqual(0L, await CountAsync(unlimitedDirectory, "SELECT count(*) FROM meta WHERE key = 'sample_time_floor'"), "with raw unlimited no floor is stored");
    }

    /// <summary>
    /// An agent that returns after a silence longer than the raw period, with continuous numbers, gets a time gap from its last sample time before the silence to its new one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreWriteBatchAfterSilencePastTheRawPeriodOpensTimeGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);
        var back = now.AddHours(1);

        await store.WriteBatchAsync(TimelineSamples.Run(1, 3, Samples.Base.AddSeconds(60)), TestContext.CancellationToken);
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        // Act
        var result = await store.WriteBatchAsync(TimelineSamples.Run(4, 1, back), TestContext.CancellationToken);
        var gaps = await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken);
        var timeGaps = TimelineSamples.TimeGaps(gaps);

        // Assert
        Assert.AreEqual(1, result.Stored, "the record after the silence is stored");
        Assert.IsEmpty(TimelineSamples.SequenceGaps(gaps), "the numbers are continuous");
        Assert.HasCount(1, timeGaps, "the silence is one time gap");
        Assert.AreEqual(Samples.Base.AddSeconds(59), timeGaps[0].From, "the gap starts at the last sample time before the silence");
        Assert.AreEqual(back.AddSeconds(-1), timeGaps[0].To, "the gap ends at the new sample time");
    }

    /// <summary>
    /// A late value below the sample-time floor that lies in no time gap adds no time gap; one that lies inside a stored time gap splits it there.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreWriteBatchLateValueBelowTheSampleFloorOpensNoTimeGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(31);
        var inGap = Samples.Base.AddHours(2);

        await store.WriteBatchAsync(TimelineSamples.OldRun(1, 60, Samples.Base, Samples.Base.AddMinutes(1)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.OldRun(70, 60, Samples.Base.AddHours(5), Samples.Base.AddHours(5).AddMinutes(1)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(130, 1, now), TestContext.CancellationToken);
        await RetentionSamples.RetainAsync(store, RetentionSamples.Defaults(), now, [], TestContext.CancellationToken);

        var before = TimelineSamples.TimeGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));

        // Act
        var noGap = await store.WriteBatchAsync(TimelineSamples.Batch(now.AddSeconds(1), TimelineSamples.Metric(62, Samples.Base.AddMilliseconds(30500))), TestContext.CancellationToken);
        var afterNoGap = TimelineSamples.TimeGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));
        var split = await store.WriteBatchAsync(TimelineSamples.Batch(now.AddSeconds(2), TimelineSamples.Metric(65, inGap)), TestContext.CancellationToken);
        var afterSplit = TimelineSamples.TimeGaps(await TimelineSamples.GapsOfAsync(store, TimelineSamples.AgentId, string.Empty, TestContext.CancellationToken));

        // Assert
        Assert.Contains(gap => gap.From < inGap && gap.To > inGap, before, "a stored time gap contains the second late value");
        Assert.AreEqual(1, noGap.Stored, "the late value in no gap is stored, a fill of the sequence gap");
        Assert.AreEqual(TimelineSamples.Fingerprint(before), TimelineSamples.Fingerprint(afterNoGap), "the late value in no gap adds, changes or drops no time gap");
        Assert.AreEqual(1, split.Stored, "the late value in the time gap is stored");
        Assert.DoesNotContain(gap => gap.From < inGap && gap.To > inGap, afterSplit, "no time gap contains the late value any more");
        Assert.Contains(gap => gap.To == inGap, afterSplit, "one part of the split gap ends at the late value");
        Assert.Contains(gap => gap.From == inGap, afterSplit, "the other part starts at the late value");
        Assert.HasCount(before.Count + 1, afterSplit, "the split adds one part and no other gap");
    }

    /// <summary>
    /// Template and source hours past the hourly period stay while the hour still holds a stored line, here an imported one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreRemoveExpiredKeepsTemplateHoursHoldingLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var hour = Samples.Base;
        var other = Samples.Base.AddHours(1);
        var now = Samples.Base.AddDays(2).AddHours(3);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Line(RecordOrigin.Import, hour.AddMinutes(10), "job 4711 finished"),
                                          RetentionSamples.Line(RecordOrigin.Backend, hour.AddMinutes(20), "job 12 finished"),
                                          RetentionSamples.Line(RecordOrigin.Backend, other.AddMinutes(5), "job 99 finished"));
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, ShortPeriods(), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, steps.Sum(step => step.LogLines), "the two agent-side lines are removed");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM records WHERE origin = 'import'"), "the imported line stays");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM log_template_hours WHERE hour = {RetentionSamples.Ns(hour)}"), "the template hour that holds the imported line stays");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM log_source_hours WHERE hour = {RetentionSamples.Ns(hour)}"), "the source hour that holds the imported line stays");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM log_template_hours WHERE hour = {RetentionSamples.Ns(other)}"), "the template hour without a stored line is removed");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM log_source_hours WHERE hour = {RetentionSamples.Ns(other)}"), "the source hour without a stored line is removed");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM log_templates"), "the template keeps its remaining hour");
        Assert.AreEqual(1, steps.Sum(step => step.TemplateHours), "one template hour is reported removed");
    }

    /// <summary>
    /// Template hours past the hourly period go once their lines are removed, a template without hours goes, and expired lines of later hours keep their statistics.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task SqliteStoreRemoveExpiredRemovesTemplateHoursPastHourlyPeriod()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var hour = Samples.Base;
        var recent = Samples.Base.AddDays(1);
        var now = Samples.Base.AddDays(2).AddHours(3);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Line(RecordOrigin.Backend, hour.AddMinutes(10), "job 1 finished"),
                                          RetentionSamples.Line(RecordOrigin.Backend, hour.AddMinutes(20), "kernel panic here"),
                                          RetentionSamples.Line(RecordOrigin.Backend, recent.AddMinutes(5), "job 3 finished"));
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, ShortPeriods(), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, await CountAsync(directory, "SELECT count(*) FROM log_lines"), "every line is past the log period");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM log_template_hours WHERE hour = {RetentionSamples.Ns(hour)}"), "the template hours past the hourly period are removed");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM log_source_hours WHERE hour = {RetentionSamples.Ns(hour)}"), "the source hour past the hourly period is removed");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM log_template_hours WHERE hour = {RetentionSamples.Ns(recent)}"), "the template hour inside the hourly period stays");
        Assert.AreEqual(1L, await CountAsync(directory, $"SELECT count(*) FROM log_source_hours WHERE hour = {RetentionSamples.Ns(recent)}"), "the source hour inside the hourly period stays");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM log_templates"), "the template without hours is removed, the other stays");
        Assert.AreEqual(2, steps.Sum(step => step.TemplateHours), "two template hours are reported removed");
    }

    /// <summary>
    /// Imported records stay past every period.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredKeepsImportedRecords()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var kinds = new[] { RecordKind.Metric, RecordKind.LogLine, RecordKind.ProcessSnapshot, RecordKind.ConnectionSnapshot, RecordKind.MariaDbStatus, RecordKind.KernelEvent, RecordKind.ServiceState, RecordKind.BackupRun, RecordKind.Gap };
        var now = Samples.Base.AddDays(30);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. kinds.Select(kind => RetentionSamples.OfKind(kind, RecordOrigin.Import, Samples.Base)), .. kinds.Select(kind => RetentionSamples.OfKind(kind, RecordOrigin.Backend, Samples.Base))]);
        await WriteMarkerAsync(store, now);

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Uniform(RetentionSamples.Days(1)), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual((long)kinds.Length, await CountAsync(directory, "SELECT count(*) FROM records WHERE origin = 'import'"), "every imported record stays");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE origin = 'backend' AND captured_at = {RetentionSamples.Ns(Samples.Base)}"), "the same records of the backend are removed");
    }

    /// <summary>
    /// OOM kills, boots, service states, backup runs and gap records stay until the events period has passed, also when the data periods are shorter.
    /// </summary>
    /// <param name="kind">The record kind, or <c>oom_kill</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow("oom_kill")]
    [DataRow(RecordKind.KernelEvent)]
    [DataRow(RecordKind.ServiceState)]
    [DataRow(RecordKind.BackupRun)]
    [DataRow(RecordKind.Gap)]
    public async Task SqliteStoreRemoveExpiredKeepsEventsUntilEventsPeriod(string kind)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = ShortPeriods(RetentionSamples.Days(10));
        var inside = Samples.Base.AddDays(9);
        var past = Samples.Base.AddDays(10).AddSeconds(1);
        var query = $"SELECT count(*) FROM records WHERE origin = 'backend' AND captured_at = {RetentionSamples.Ns(Samples.Base)}";

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.OfKind(kind, RecordOrigin.Backend, Samples.Base));
        await WriteMarkerAsync(store, past);

        // Act
        await RetentionSamples.RetainAsync(store, policy, inside, [], TestContext.CancellationToken);

        var keptInside = await CountAsync(directory, query);

        await RetentionSamples.RetainAsync(store, policy, past, [], TestContext.CancellationToken);

        var keptPast = await CountAsync(directory, query);

        // Assert
        Assert.AreEqual(1L, keptInside, $"the {kind} record stays inside the events period");
        Assert.AreEqual(0L, keptPast, $"the {kind} record goes once the events period has passed");
    }

    /// <summary>
    /// Gap rows go only past the gap horizon, the longest finite period: a closed sequence gap and a time gap that reads final go, a time gap that reads open, an open sequence gap and a gap inside a hold stay.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredRemovesGapRowsOnlyPastTheGapHorizon()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = new RetentionPolicy
                     {
                         Raw = RetentionSamples.Days(20),
                         FiveMinuteRollups = RetentionSamples.Days(2),
                         HourlyRollups = RetentionSamples.Days(3),
                         Logs = RetentionSamples.Days(1),
                         Snapshots = RetentionSamples.Days(1),
                         Events = RetentionSamples.Days(5),
                         KillWindowBefore = TimeSpan.FromHours(1),
                         KillWindowAfter = TimeSpan.FromMinutes(30)
                     };
        var eventsPassed = Samples.Base.AddDays(6);
        var horizonPassed = Samples.Base.AddDays(20).AddHours(3);

        await InsertGapAsync(directory, "sequence", "closed", (4, 9), (3, 10), 0);
        await InsertGapAsync(directory, "time", "open", null, (20, 21), 2);
        await InsertGapAsync(directory, "sequence", "open", (31, 33), (30, 34), 4);
        await InsertGapAsync(directory, "time", "open", null, (30, 34), 6);
        await InsertGapAsync(directory, "sequence", "closed", (40, 45), (39, 46), 60);
        await WriteMarkerAsync(store, horizonPassed);
        await RetentionSamples.FoldAllAsync(store, policy, eventsPassed, TestContext.CancellationToken);
        await RetentionSamples.MarkScannedAsync(store, [RetentionSamples.Hold(Samples.Base.AddMinutes(50), Samples.Base.AddHours(2))], TestContext.CancellationToken);

        // Act
        var early = await RetentionSamples.RemovePassAsync(store, policy, eventsPassed, TestContext.CancellationToken);
        var afterEvents = await RowsAsync(directory, GapList);
        var late = await RetentionSamples.RemovePassAsync(store, policy, horizonPassed, TestContext.CancellationToken);
        var afterHorizon = await RowsAsync(directory, GapList);

        // Assert
        Assert.AreEqual(0, early.Sum(step => step.Gaps), "no gap row goes before the raw period, the longest one, has passed");
        Assert.AreSequenceEqual(["sequence closed 3", "time open 20", "sequence open 30", "time open 30", "sequence closed 39"], afterEvents, "every gap row stays once only the events period has passed");
        Assert.AreEqual(2, late.Sum(step => step.Gaps), "two gap rows are reported removed");
        Assert.AreSequenceEqual(["sequence open 30", "time open 30", "sequence closed 39"], afterHorizon, "the closed sequence gap and the time gap that reads final go; the open gaps and the gap in the hold stay");
    }

    /// <summary>
    /// A sequence gap that is open or final is never removed, however old.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredNeverRemovesOpenOrFinalSequenceGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(100);

        await InsertGapAsync(directory, "sequence", "open", (4, 9), (3, 10), 0);
        await InsertGapAsync(directory, "sequence", "final", (14, 19), (13, 20), 2);
        await InsertGapAsync(directory, "sequence", "closed", (24, 29), (23, 30), 4);
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, RetentionSamples.Uniform(RetentionSamples.Days(1)), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, steps.Sum(step => step.Gaps), "only the closed gap is removed");
        Assert.AreSequenceEqual(["sequence open 3", "sequence final 13"], await RowsAsync(directory, GapList), "the open and the final sequence gap stay");
    }

    /// <summary>
    /// Connection snapshots with a listener finding stay with their marks and comparisons until the events period; others go with the snapshots period, except the agent's newest mark.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredKeepsListenerFindingsWithTheirSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = ShortPeriods(RetentionSamples.Days(10));
        var snapshotsPassed = Samples.Base.AddDays(2);
        var eventsPassed = Samples.Base.AddDays(11);
        var baseline = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));

        await store.WriteBatchAsync(ListenerSamples.Batch(TimelineSamples.AgentId,
                                                          ListenerSamples.Record(1, 0, baseline),
                                                          ListenerSamples.Record(2, 60, ListenerSamples.Found("all", [ListenerSamples.Tcp("0.0.0.0", 23, 7, "telnetd")], 0)),
                                                          ListenerSamples.Record(3, 120, ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 25))),
                                                          ListenerSamples.Record(4, 180, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22))),
                                                          ListenerSamples.Record(5, 240, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22)))),
                                    TestContext.CancellationToken);
        await store.WriteBatchAsync(ListenerSamples.Batch(TimelineSamples.AgentId, TimelineSamples.Metric(6, eventsPassed)), TestContext.CancellationToken);

        // Act
        await RetentionSamples.RetainAsync(store, policy, snapshotsPassed, [], TestContext.CancellationToken);

        var snapshotsAfterFirst = await RowsAsync(directory, SnapshotSeqs);
        var marksAfterFirst = await RowsAsync(directory, "SELECT seq FROM connection_marks ORDER BY seq");
        var comparisonsAfterFirst = await RowsAsync(directory, "SELECT seq FROM listener_comparisons ORDER BY seq");

        await RetentionSamples.RetainAsync(store, policy, eventsPassed, [], TestContext.CancellationToken);

        var snapshotsAfterSecond = await RowsAsync(directory, SnapshotSeqs);

        // Assert
        Assert.AreSequenceEqual(["2", "3", "5"], snapshotsAfterFirst, "the findings and the newest mark stay past the snapshots period");
        Assert.AreSequenceEqual(["2", "3", "5"], marksAfterFirst, "the marks of the kept snapshots stay, the others go");
        Assert.AreSequenceEqual(["3"], comparisonsAfterFirst, "the comparison of the start finding stays");
        Assert.AreSequenceEqual(["5"], snapshotsAfterSecond, "past the events period only the newest mark stays");
    }

    /// <summary>
    /// The newest connection mark of each agent stays past the snapshots period.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredKeepsNewestConnectionMarkPerAgent()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = Samples.Base.AddDays(2);

        foreach (var agent in new[] { "agent-1", "agent-2" })
        {
            await store.WriteBatchAsync(ListenerSamples.Batch(agent,
                                                              ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22))),
                                                              ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22)))),
                                        TestContext.CancellationToken);
        }

        await WriteMarkerAsync(store, now);

        // Act
        await RetentionSamples.RetainAsync(store, ShortPeriods(RetentionSamples.Days(10)), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["agent-1:2", "agent-2:2"], await RowsAsync(directory, "SELECT agent_id || ':' || seq FROM records WHERE kind = 'connection_snapshot' ORDER BY agent_id, seq"), "each agent keeps its newest mark");
    }

    /// <summary>
    /// Records captured inside a hold stay past their periods, those outside go, and the hold stays while incidents are kept.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredKeepsRecordsInHolds()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var inside = Samples.Base;
        var outside = Samples.Base.AddHours(2);
        var now = Samples.Base.AddDays(5);
        var hold = RetentionSamples.Hold(inside.AddHours(-1), inside.AddMinutes(30));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. KindsAt(inside), .. KindsAt(outside)]);
        await WriteMarkerAsync(store, now);

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Uniform(RetentionSamples.Days(1)), now, [hold], TestContext.CancellationToken);

        var holds = await store.HoldsAsync(Samples.Base.AddDays(-1), Samples.Base.AddDays(1), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE captured_at = {RetentionSamples.Ns(inside)}"), "the records in the hold stay");
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE captured_at = {RetentionSamples.Ns(outside)}"), "the records outside the hold go");
        Assert.AreSequenceEqual(RetentionSamples.Describe([hold]), RetentionSamples.Describe(holds), "the hold stays");
    }

    /// <summary>
    /// A hold past the incident period goes unless a stored incident with a note overlaps it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task SqliteStoreRemoveExpiredKeepsHoldOverlappingIncidentWithNote()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var incident = IncidentAt(Samples.Base);
        var noted = RetentionSamples.Hold(Samples.Base.AddMinutes(30), Samples.Base.AddHours(1));
        var lone = RetentionSamples.Hold(Samples.Base.AddHours(5), Samples.Base.AddHours(6));
        var now = Samples.Base.AddDays(40);
        var policy = new RetentionPolicy
                     {
                         Raw = RetentionSamples.Days(1),
                         FiveMinuteRollups = RetentionSamples.Days(1),
                         HourlyRollups = RetentionSamples.Days(1),
                         Logs = RetentionSamples.Days(1),
                         Snapshots = RetentionSamples.Days(1),
                         Events = RetentionSamples.Days(1),
                         Incidents = RetentionSamples.Days(30),
                         KillWindowBefore = TimeSpan.FromHours(1),
                         KillWindowAfter = TimeSpan.FromMinutes(30)
                     };

        await SaveWithNoteAsync(store, incident);
        await WriteMarkerAsync(store, now);

        // Act
        var steps = await RetentionSamples.RetainAsync(store, policy, now, [noted, lone], TestContext.CancellationToken);
        var holds = await store.HoldsAsync(Samples.Base.AddDays(-1), Samples.Base.AddDays(1), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, steps.Sum(step => step.Holds), "one hold is reported removed");
        Assert.AreSequenceEqual(RetentionSamples.Describe([noted]), RetentionSamples.Describe(holds), "the hold under the incident with a note stays");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM incidents"), "the incident with a note stays");
    }

    /// <summary>
    /// The window data of a stored incident without a hold follows the periods, while the incident itself stays.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC13")]
    public async Task SqliteStoreRemoveExpiredRemovesWindowDataOfIncidentWithoutKernelKill()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var incident = IncidentAt(Samples.Base);
        var inside = Samples.Base.AddHours(1);
        var now = Samples.Base.AddDays(5);

        await store.SaveIncidentAsync(incident, Samples.Base.AddDays(1), TestContext.CancellationToken);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, KindsAt(inside));
        await WriteMarkerAsync(store, now);

        // Act
        await RetentionSamples.RetainAsync(store, RetentionSamples.Uniform(RetentionSamples.Days(1)), now, [], TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, await CountAsync(directory, $"SELECT count(*) FROM records WHERE captured_at = {RetentionSamples.Ns(inside)}"), "the window data of the incident is removed");
        Assert.AreEqual(1L, await CountAsync(directory, "SELECT count(*) FROM incidents"), "the incident stays");
    }

    /// <summary>
    /// Saved holds are merged with the stored holds they overlap, so stored holds never overlap, and the scan progress is stored with them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task SqliteStoreSaveKillScanMergesOverlappingHolds()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = RetentionSamples.Hold(Samples.Base, Samples.Base.AddHours(1));
        var bridge = RetentionSamples.Hold(Samples.Base.AddMinutes(30), Samples.Base.AddHours(2));
        var lone = RetentionSamples.Hold(Samples.Base.AddHours(5), Samples.Base.AddHours(6));
        var tail = RetentionSamples.Hold(Samples.Base.AddMinutes(90), Samples.Base.AddHours(3));

        await store.SaveKillScanAsync([first], Progress(5), TestContext.CancellationToken);

        // Act
        await store.SaveKillScanAsync([bridge, lone, tail], Progress(9), TestContext.CancellationToken);

        var holds = await store.HoldsAsync(Samples.Base.AddDays(-1), Samples.Base.AddDays(1), TestContext.CancellationToken);
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(RetentionSamples.Describe([RetentionSamples.Hold(Samples.Base, Samples.Base.AddHours(3)), lone]), RetentionSamples.Describe(holds), "the overlapping holds are one hold, the lone one stays apart");
        Assert.AreEqual(2L, await CountAsync(directory, "SELECT count(*) FROM retention_holds"), "two rows are stored");
        Assert.AreEqual(9L, progress.ScannedId, "the progress of the second save is stored");
    }

    /// <summary>
    /// The holds read for a range are the stored holds that overlap it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task SqliteStoreHoldsReturnsHoldsOverlappingTheRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = RetentionSamples.Hold(Samples.Base, Samples.Base.AddHours(1));
        var second = RetentionSamples.Hold(Samples.Base.AddHours(5), Samples.Base.AddHours(6));
        var third = RetentionSamples.Hold(Samples.Base.AddHours(10), Samples.Base.AddHours(11));

        await store.SaveKillScanAsync([first, second, third], Progress(0), TestContext.CancellationToken);

        // Act
        var both = await store.HoldsAsync(Samples.Base.AddMinutes(30), Samples.Base.AddMinutes(330), TestContext.CancellationToken);
        var none = await store.HoldsAsync(Samples.Base.AddHours(2), Samples.Base.AddHours(3), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(RetentionSamples.Describe([first, second]), RetentionSamples.Describe(both), "the two holds that overlap the range");
        Assert.IsEmpty(none, "no hold overlaps a range between holds");
    }

    /// <summary>
    /// The kill scan reads kernel events above an ID in ID order, a page at a time, and its progress starts at zero with the highest stored ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task SqliteStoreScanRecordsReadsKernelEventsAboveId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base, "cpu", 1),
                                          RetentionSamples.OomKillEvent(RecordOrigin.Backend, Samples.Base.AddSeconds(1)),
                                          RetentionSamples.OfKind(RecordKind.KernelEvent, RecordOrigin.Backend, Samples.Base.AddSeconds(2)),
                                          RetentionSamples.Line(RecordOrigin.Backend, Samples.Base.AddSeconds(3), "oom-killer: kill process 4711 (php-fpm)"),
                                          RetentionSamples.OomKillEvent(RecordOrigin.Backend, Samples.Base.AddSeconds(4)));

        var kernel = (await RowsAsync(directory, "SELECT id FROM records WHERE kind = 'kernel_event' ORDER BY id")).Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToList();

        // Act
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);
        var all = await store.ScanRecordsAsync(Scan(0, 10), TestContext.CancellationToken);
        var page = await store.ScanRecordsAsync(Scan(kernel[0], 1), TestContext.CancellationToken);
        var rest = await store.ScanRecordsAsync(Scan(kernel[0], 10), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, progress.ScannedId, "nothing is scanned yet");
        Assert.AreEqual(5L, progress.HighestId, "the highest stored record ID");
        Assert.AreSequenceEqual(kernel, all.Select(record => record.Id).ToList(), "only kernel events, in ID order");
        Assert.AreSequenceEqual(new[] { kernel[1] }, page.Select(record => record.Id).ToArray(), "a page of one after the first event");
        Assert.AreSequenceEqual(kernel.Skip(1).ToList(), rest.Select(record => record.Id).ToList(), "the events above the ID");
        Assert.AreEqual(KernelEvent.TypeOomKill, ((KernelEvent)all[0].Record.Data!).Type, "the records carry their payload");
    }

    /// <summary>
    /// Creates the agent batch of one quarter hour of the constant load, numbered on from the previous ones: metrics every 10 seconds per series, a log line a minute, a process and a MariaDB snapshot, and in the first quarter of each hour one service state and one gap record (two more log lines in the others, so each batch has as many records).
    /// </summary>
    /// <param name="quarter">The quarter hours after <see cref="Samples.Base"/></param>
    /// <returns>The batch, received at the end of the quarter hour</returns>
    private static RecordBatch LoadOf(int quarter)
    {
        var start = Samples.Base.AddMinutes(quarter * 15);
        var metrics = Enumerable.Range(0, LoadSeries * 90).Select(index => RetentionSamples.Metric(RecordOrigin.Agent, start.AddSeconds(index / LoadSeries * 10), $"series{index % LoadSeries}", index % 13));
        var lines = Enumerable.Range(0, 15).Select(index => RetentionSamples.Line(RecordOrigin.Agent, start.AddMinutes(index).AddSeconds(1), $"request {(quarter * 15) + index} served in {index % 17} ms"));
        DataRecord[] snapshots = [RetentionSamples.OfKind(RecordKind.ProcessSnapshot, RecordOrigin.Agent, start.AddSeconds(2)), RetentionSamples.OfKind(RecordKind.MariaDbStatus, RecordOrigin.Agent, start.AddSeconds(3))];
        DataRecord[] hourly = quarter % 4 == 0
                                  ? [RetentionSamples.OfKind(RecordKind.ServiceState, RecordOrigin.Agent, start.AddMinutes(7).AddSeconds(4)), TimelineSamples.AgentGap(0, start.AddMinutes(11).AddSeconds(5), GapCause.Unknown, 0, 0)]
                                  : [RetentionSamples.Line(RecordOrigin.Agent, start.AddMinutes(7).AddSeconds(4), "cache warmed"), RetentionSamples.Line(RecordOrigin.Agent, start.AddMinutes(11).AddSeconds(5), "cache warmed")];
        var records = metrics.Concat(lines).Concat(snapshots).Concat(hourly).OrderBy(record => record.CapturedAt).ToArray();
        var first = (ulong)(quarter * records.Length) + 1;

        for (var index = 0; index < records.Length; index++)
        {
            records[index].Seq = first + (ulong)index;
        }

        return TimelineSamples.Batch(start.AddMinutes(15), records);
    }

    /// <summary>
    /// Builds the query that lists the columns of every index entry of a table, leaving out the primary key of a table without row ID, which is the table itself.
    /// </summary>
    /// <param name="table">The table</param>
    /// <returns>The query</returns>
    private static string IndexColumnsQuery(string table)
    {
        return $"SELECT ii.name FROM pragma_index_list('{table}') AS il JOIN pragma_index_info(il.name) AS ii WHERE ii.name IS NOT NULL AND NOT (il.origin = 'pk' AND (SELECT wr FROM pragma_table_list('{table}')) = 1)";
    }

    /// <summary>
    /// Creates a policy with short data periods: raw, logs and snapshots one day, 5-minute and hourly rollups two days.
    /// </summary>
    /// <param name="events">The events period; three days when not given</param>
    /// <returns>The policy</returns>
    private static RetentionPolicy ShortPeriods(TimeSpan? events = null)
    {
        return new RetentionPolicy
               {
                   Raw = RetentionSamples.Days(1),
                   FiveMinuteRollups = RetentionSamples.Days(2),
                   HourlyRollups = RetentionSamples.Days(2),
                   Logs = RetentionSamples.Days(1),
                   Snapshots = RetentionSamples.Days(1),
                   Events = events ?? RetentionSamples.Days(3),
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Creates the default policy with another incident period.
    /// </summary>
    /// <param name="incidents">The incident period</param>
    /// <returns>The policy</returns>
    private static RetentionPolicy WithIncidents(TimeSpan? incidents)
    {
        var defaults = RetentionSamples.Defaults();

        return new RetentionPolicy
               {
                   Raw = defaults.Raw,
                   FiveMinuteRollups = defaults.FiveMinuteRollups,
                   HourlyRollups = defaults.HourlyRollups,
                   Logs = defaults.Logs,
                   Snapshots = defaults.Snapshots,
                   Events = defaults.Events,
                   Incidents = incidents,
                   KillWindowBefore = defaults.KillWindowBefore,
                   KillWindowAfter = defaults.KillWindowAfter
               };
    }

    /// <summary>
    /// Creates a small incident whose window starts at an instant and lasts two hours.
    /// </summary>
    /// <param name="start">The start of the window</param>
    /// <returns>The incident</returns>
    private static Incident IncidentAt(DateTimeOffset start)
    {
        return new Incident
               {
                   Start = start,
                   End = start.AddHours(2),
                   WindowBeforeSeconds = 3600,
                   WindowAfterSeconds = 1800,
                   FirstKillAt = start.AddHours(1),
                   LastKillAt = start.AddHours(1),
                   KillCount = 1,
                   Hypothesis = new IncidentHypothesis
                                {
                                    SnapshotCount = 2,
                                    GrowthKnown = true
                                }
               };
    }

    /// <summary>
    /// Creates a scan progress.
    /// </summary>
    /// <param name="id">The scanned and the highest ID</param>
    /// <returns>The progress</returns>
    private static KillScanProgress Progress(long id)
    {
        return new KillScanProgress
               {
                   ScannedId = id,
                   HighestId = id
               };
    }

    /// <summary>
    /// Creates a kill scan query.
    /// </summary>
    /// <param name="aboveId">The ID to read after</param>
    /// <param name="limit">The page size</param>
    /// <returns>The query</returns>
    private static KillScanQuery Scan(long aboveId, int limit)
    {
        return new KillScanQuery
               {
                   AboveId = aboveId,
                   Limit = limit
               };
    }

    /// <summary>
    /// Creates metric values captured one second apart from <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="count">The number of values</param>
    /// <returns>The records</returns>
    private static DataRecord[] OldMetrics(string name, int count)
    {
        return Enumerable.Range(0, count).Select(index => RetentionSamples.Metric(RecordOrigin.Backend, Samples.Base.AddSeconds(index), name, index)).ToArray();
    }

    /// <summary>
    /// Creates backend log lines captured ten milliseconds apart from <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="message">The start of each message</param>
    /// <param name="count">The number of lines</param>
    /// <returns>The records</returns>
    private static DataRecord[] OldLines(string message, int count)
    {
        return LinesFrom(Samples.Base, count, message);
    }

    /// <summary>
    /// Creates backend log lines captured ten milliseconds apart.
    /// </summary>
    /// <param name="start">The capture time of the first line</param>
    /// <param name="count">The number of lines</param>
    /// <param name="message">The start of each message</param>
    /// <returns>The records</returns>
    private static DataRecord[] LinesFrom(DateTimeOffset start, int count, string message)
    {
        return Enumerable.Range(0, count).Select(index => RetentionSamples.Line(RecordOrigin.Backend, start.AddMilliseconds(index * 10), $"{message} {index}")).ToArray();
    }

    /// <summary>
    /// Creates imported log lines captured ten milliseconds apart.
    /// </summary>
    /// <param name="start">The capture time of the first line</param>
    /// <param name="count">The number of lines</param>
    /// <returns>The records</returns>
    private static DataRecord[] ImportedLines(DateTimeOffset start, int count)
    {
        return Enumerable.Range(0, count).Select(index => RetentionSamples.Line(RecordOrigin.Import, start.AddMilliseconds(index * 10), $"imported line {index}")).ToArray();
    }

    /// <summary>
    /// Creates a metric, a log line and a process snapshot of the backend captured at one instant.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <returns>The records</returns>
    private static DataRecord[] KindsAt(DateTimeOffset at)
    {
        return [RetentionSamples.OfKind(RecordKind.Metric, RecordOrigin.Backend, at), RetentionSamples.OfKind(RecordKind.LogLine, RecordOrigin.Backend, at), RetentionSamples.OfKind(RecordKind.ProcessSnapshot, RecordOrigin.Backend, at)];
    }

    /// <summary>
    /// Lists the removal counts of a step.
    /// </summary>
    /// <param name="step">The step</param>
    /// <returns>The counts</returns>
    private static int[] Counts(RemovalStepResult step)
    {
        return [step.MetricValues, step.LogLines, step.Snapshots, step.EventRecords, step.Gaps, step.FiveMinuteValues, step.HourlyValues, step.TemplateHours, step.RemovedIncidents, step.Holds];
    }

    /// <summary>
    /// Moves a batch to the boot after a reboot.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <returns>The batch</returns>
    private static RecordBatch Rebooted(RecordBatch batch)
    {
        batch.BootId = TimelineSamples.OtherBootId;

        return batch;
    }

    /// <summary>
    /// Lists the string constants of a type that hold SQL statements, each statement of a constant on its own.
    /// </summary>
    /// <param name="type">The type</param>
    /// <returns>The name of each constant with one statement</returns>
    private static List<(string Name, string Sql)> StatementsOf(Type type)
    {
        return type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                   .Where(field => field.FieldType == typeof(string))
                   .SelectMany(field => ((string)field.GetValue(null)!).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(sql => (field.Name, sql)))
                   .Where(entry => StatementStart().IsMatch(entry.sql))
                   .ToList();
    }

    /// <summary>
    /// Matches the start of a statement that the query planner plans.
    /// </summary>
    /// <returns>The expression</returns>
    [GeneratedRegex("^(SELECT|DELETE|UPDATE|INSERT|WITH|REPLACE)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatementStart();

    /// <summary>
    /// Matches a statement parameter.
    /// </summary>
    /// <returns>The expression</returns>
    [GeneratedRegex("[$@:][A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    /// <summary>
    /// Reads the query plan of a statement, with every parameter bound to NULL.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that returns the details of the plan</returns>
    private async Task<List<string>> PlanAsync(SqliteConnection connection, string sql)
    {
        var details = new List<string>();

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a constant of the production code
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
#pragma warning restore CA2100

        foreach (var name in Parameter().Matches(sql).Select(match => match.Value).Distinct(StringComparer.Ordinal))
        {
            command.Parameters.AddWithValue(name, DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    /// <summary>
    /// Runs a statement on a connection.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task ExecuteOnAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = sql;
#pragma warning restore CA2100

        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes a backend metric that is the record with the highest ID, captured at an instant.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="at">The capture time</param>
    /// <returns>A task that completes when the record is stored</returns>
    private async Task WriteMarkerAsync(SqliteStore store, DateTimeOffset at)
    {
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, RetentionSamples.Metric(RecordOrigin.Backend, at, Marker, 1));
    }

    /// <summary>
    /// Writes metric values of two agents: agent-1 five below the raw horizon and three just before now, agent-2 four below the horizon.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The time of the pass</param>
    /// <returns>A task that completes when the batches are stored</returns>
    private async Task WriteSampleTimesAsync(SqliteStore store, DateTimeOffset now)
    {
        var other = Enumerable.Range(0, 4).Select(index => TimelineSamples.Metric((ulong)index + 1, Samples.Base.AddSeconds(10 + index))).ToArray();

        await store.WriteBatchAsync(TimelineSamples.OldRun(1, 5, Samples.Base.AddSeconds(55), Samples.Base.AddMinutes(1)), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.BatchOf("agent-2", TimelineSamples.BootId, Samples.Base.AddMinutes(1), other), TestContext.CancellationToken);
        await store.WriteBatchAsync(TimelineSamples.Run(6, 3, now), TestContext.CancellationToken);
    }

    /// <summary>
    /// Saves an incident and a note for it.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="incident">The incident</param>
    /// <returns>A task that completes when both are stored</returns>
    private async Task SaveWithNoteAsync(SqliteStore store, Incident incident)
    {
        var saved = await store.SaveIncidentAsync(incident, incident.End.AddDays(1), TestContext.CancellationToken);

        await store.SaveIncidentNoteAsync(new IncidentNoteEdit
                                          {
                                              IncidentId = saved.Id,
                                              WrittenFor = saved.Id,
                                              Text = "found the cause",
                                              SavedAt = incident.End.AddDays(1)
                                          },
                                          TestContext.CancellationToken);
    }

    /// <summary>
    /// Inserts a gap row of agent <c>agent-1</c> behind the store's back.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="kind">The gap kind</param>
    /// <param name="state">The stored state</param>
    /// <param name="numbers">The first and last missing number of a sequence gap, or <c>null</c></param>
    /// <param name="neighbours">The numbers of the records before and after the gap</param>
    /// <param name="minutes">The minutes after <see cref="Samples.Base"/> the gap starts; it lasts one minute</param>
    /// <returns>A task that completes when the row is stored</returns>
    private Task InsertGapAsync(TempDirectory directory, string kind, string state, (int First, int Last)? numbers, (int From, int To) neighbours, int minutes)
    {
        var from = Samples.Base.AddMinutes(minutes);
        var first = numbers is null ? "NULL" : numbers.Value.First.ToString(CultureInfo.InvariantCulture);
        var last = numbers is null ? "NULL" : numbers.Value.Last.ToString(CultureInfo.InvariantCulture);
        var sql = string.Create(CultureInfo.InvariantCulture,
                                $"INSERT INTO gaps(agent_id, kind, cause, state, first_seq, last_seq, from_seq, to_seq, from_at, to_at, detected_at, changed_at) VALUES ('agent-1', '{kind}', 'no_data', '{state}', {first}, {last}, {neighbours.From}, {neighbours.To}, {RetentionSamples.Ns(from)}, {RetentionSamples.Ns(from.AddMinutes(1))}, {RetentionSamples.Ns(from.AddMinutes(2))}, {RetentionSamples.Ns(from.AddMinutes(2))})");

        return RetentionSamples.ExecuteAsync(directory.Path, sql, TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes the load of each quarter hour of a span and runs a fold, a kill scan mark and a removal pass at the end of each, so a period holds 48 passes as 2 days of hourly passes do.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="fromQuarter">The first quarter hour</param>
    /// <param name="toQuarter">The quarter hour after the last one</param>
    /// <returns>A task that returns the metric values the passes removed</returns>
    private async Task<int> SimulateAsync(SqliteStore store, RetentionPolicy policy, int fromQuarter, int toQuarter)
    {
        var removed = 0;

        for (var quarter = fromQuarter; quarter < toQuarter; quarter++)
        {
            await store.WriteBatchAsync(LoadOf(quarter), TestContext.CancellationToken);

            var steps = await RetentionSamples.RetainAsync(store, policy, Samples.Base.AddMinutes((quarter + 1) * 15), [], TestContext.CancellationToken);

            removed += steps.Sum(step => step.MetricValues);
        }

        return removed;
    }

    /// <summary>
    /// Reads the size of the database as page count times page size, the WAL included.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the size in bytes</returns>
    private async Task<long> FileBytesAsync(TempDirectory directory)
    {
        return await CountAsync(directory, "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()");
    }

    /// <summary>
    /// Sums the lengths of every column of the rollup, template and gap tables and of the event and gap records, and of the columns of each of their index entries.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the bytes</returns>
    private async Task<long> KeptBytesAsync(TempDirectory directory)
    {
        var total = 0L;

        foreach (var parts in KeptTables.Split(';').Select(entry => entry.Split(':')))
        {
            var indexColumns = await RowsAsync(directory, IndexColumnsQuery(parts[0]));
            var columns = parts[1].Split(',').Concat(indexColumns).Select(column => $"coalesce(length({column}), 0)");
            var filter = parts[0] == "records" ? KeptRecordKinds : string.Empty;

            total += await CountAsync(directory, $"SELECT coalesce(sum({string.Join(" + ", columns)}), 0) FROM {parts[0]}{filter}");
        }

        return total;
    }

    /// <summary>
    /// Reads the first value of a query as a number.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the value</returns>
    private Task<long> CountAsync(TempDirectory directory, string sql)
    {
        return RetentionSamples.CountAsync(directory.Path, sql, TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<List<string>> RowsAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory.Path)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal passed by the calling test
        command.CommandText = sql;
#pragma warning restore CA2100

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(column => Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>
    /// Reads every row of both rollup tables.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the rows as text</returns>
    private Task<List<string>> RollupRowsAsync(TempDirectory directory)
    {
        return RowsAsync(directory,
                         "SELECT '5m', source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id FROM metric_rollups_5m "
                         + "UNION ALL SELECT '1h', source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id FROM metric_rollups_1h ORDER BY 1, 2, 3, 4, 5");
    }

    /// <summary>
    /// Reads the counts and values a failed or busy step must leave unchanged.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<List<string>> FingerprintAsync(TempDirectory directory)
    {
        var rows = new List<string>();

        rows.AddRange(await RowsAsync(directory, "SELECT 'records', count(*), max(id) FROM records"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'log_lines', count(*) FROM log_lines"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'log_fts', count(*) FROM log_fts WHERE log_fts MATCH 'old'"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'metrics', count(*) FROM metrics"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'template hours', count(*), sum(count) FROM log_template_hours"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'source hours', count(*), sum(count) FROM log_source_hours"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'meta', key, value FROM meta ORDER BY key"));

        return rows;
    }

    #endregion // Methods
}