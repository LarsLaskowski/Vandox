using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the live or backfilled classification, the gaps and the clock offset of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreTimelineTests
{
    #region Constants

    private const string AgentId = TimelineSamples.AgentId;

    private const long NanosecondsPerSecond = 1_000_000_000L;

    private const string OpenSequenceGap = "open sequence_missing";

    private const string PoisonTrigger = "CREATE TRIGGER poison_records BEFORE INSERT ON records WHEN NEW.seq = 99 BEGIN SELECT RAISE(ABORT, 'poisoned'); END";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _t0 = Samples.Base;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// After the agent's clock steps forward, fresh records are backfilled until the next counted sample measures the step, and a future-stamped log line never raises the offset.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineClockSteppedForwardFreshRecordsBackfilled()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.Zero, 100);

        await WriteAllAsync(store, replay.AdvanceTo(_t0.AddMinutes(10)));

        var steady = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        // Act
        var logSeq = replay.ReserveSeq();
        var logBatch = TimelineSamples.Batch(replay.Now, TimelineSamples.LogLine(logSeq, replay.Now.AddHours(5)));

        await WriteAsync(store, logBatch);

        var afterLog = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        replay.StepClock(TimeSpan.FromHours(1));

        var batches = replay.AdvanceTo(_t0.AddSeconds(670));

        Assert.HasCount(3, batches, "the unsent tick before the step, the first tick after it and the next self tick");

        await WriteAsync(store, batches[0].Batch);

        var beforeStep = await LiveAsync(directory);

        await WriteAsync(store, batches[1].Batch);

        var stepped = await LiveAsync(directory);
        var clockAtStep = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        await WriteAsync(store, batches[2].Batch);

        var measured = await LiveAsync(directory);
        var clockAfter = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(steady, "the clock of the agent is measured");
        Assert.IsNotNull(steady.MeasuredAt, "a counted sample measured it");
        Assert.IsGreaterThanOrEqualTo(-4 * NanosecondsPerSecond, steady.OffsetNs ?? long.MinValue, "the offset of a clock in step");
        Assert.IsLessThanOrEqualTo(0, steady.OffsetNs ?? long.MaxValue, "no clock is measured ahead of itself");
        Assert.AreEqual(steady.OffsetNs, afterLog!.OffsetNs, "a log line with a time five hours ahead does not raise the offset");
        Assert.AreEqual(steady.MeasuredAt, afterLog.MeasuredAt, "a log line is no sample");
        Assert.IsFalse((await LiveAsync(directory))[logSeq], "the future-stamped log line is backfilled");
        AssertAll(beforeStep, batches[0].Batch, true, "records captured before the step");
        AssertAll(stepped, batches[1].Batch, false, "fresh records stamped one hour ahead of a measurement that does not know the step");
        Assert.AreEqual(steady.OffsetNs, clockAtStep!.OffsetNs, "the offset does not move before the next counted sample");
        AssertAll(measured, batches[2].Batch, true, "records of the batch whose counted sample measured the step");
        Assert.IsGreaterThanOrEqualTo(3595 * NanosecondsPerSecond, clockAfter!.OffsetNs ?? long.MinValue, "the step is measured");
        Assert.IsLessThanOrEqualTo(3600 * NanosecondsPerSecond, clockAfter.OffsetNs ?? long.MaxValue, "the step is measured from below");
    }

    /// <summary>
    /// A backend that is off from 22:00 to 11:00 opens one sequence gap and one time gap, stores no outage record live, keeps the offset until both gaps close, and stores the current records of the batch after it live.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineThirteenHourOutageNeverStoresOutageRecordLive()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var start = new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.Zero);
        var outageStart = start.AddHours(1);
        var backendBack = outageStart.AddHours(13);
        var replay = new AgentReplay(start, TimeSpan.Zero, 1000);

        await WriteAllAsync(store, replay.AdvanceTo(outageStart));

        var lastStored = (await LiveAsync(directory)).Keys.Max();
        var clockBefore = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        replay.SetBackend(false);
        replay.AdvanceTo(outageStart.AddMinutes(90).AddSeconds(20));
        replay.Restart();
        replay.AdvanceTo(backendBack);
        replay.SetBackend(true);

        var batches = replay.AdvanceTo(backendBack.AddMinutes(3));

        // Act
        await WriteAsync(store, batches[0].Batch);

        var openedGaps = await GapsAsync(store);
        var firstLive = batches[0].Batch.Records.Min(record => record.Seq);
        var firstMetric = batches[0].Batch.Records.Where(record => record.Kind == RecordKind.Metric).Min(record => record.CapturedAt);
        var closeIndex = 0;
        var clockHeld = true;

        for (var index = 1; index < batches.Count && closeIndex == 0; index++)
        {
            await WriteAsync(store, batches[index].Batch);

            var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);

            clockHeld &= clock!.OffsetNs == clockBefore!.OffsetNs && clock.MeasuredAt == clockBefore.MeasuredAt;

            if ((await GapsAsync(store, GapState.Open)).Count == 0)
            {
                closeIndex = index;
            }
        }

        var closedGaps = await GapsAsync(store);

        for (var index = closeIndex + 1; index < batches.Count; index++)
        {
            await WriteAsync(store, batches[index].Batch);
        }

        var live = await LiveAsync(directory);

        // Assert
        Assert.HasCount(2, openedGaps, "one sequence gap and one time gap open with the first batch");
        Assert.IsTrue(openedGaps.All(gap => gap.State == GapState.Open), "both gaps are open");
        Assert.AreSequenceEqual([$"{lastStored + 1}-{firstLive - 1} {OpenSequenceGap}"], TimelineSamples.SequenceGaps(openedGaps), "the numbers between the last stored one and the batch");
        Assert.HasCount(1, TimelineSamples.TimeGaps(openedGaps), "one time gap");
        Assert.AreEqual(GapCause.NoData, TimelineSamples.TimeGaps(openedGaps)[0].Cause, "the cause of the time gap");
        Assert.AreEqual(replay.TrueCaptureOf(lastStored), TimelineSamples.TimeGaps(openedGaps)[0].From, "the time gap starts at the last stored sample");
        Assert.AreEqual(firstMetric, TimelineSamples.TimeGaps(openedGaps)[0].To, "the time gap ends at the first current sample");
        Assert.IsGreaterThan(0, closeIndex, "the backfill brings the last missing number");
        Assert.IsTrue(batches[closeIndex].Backfill, "the last missing number comes with the backfill");
        Assert.IsTrue(clockHeld, "the offset stays unchanged until both gaps are closed");
        Assert.HasCount(2, closedGaps, "no further gap");
        Assert.IsTrue(closedGaps.All(gap => gap.State == GapState.Closed), "both gaps are closed with the last missing number");

        var closing = batches[closeIndex].Batch;
        var closingSeqs = closing.Records.Select(record => record.Seq).ToHashSet();

        foreach (var seq in live.Keys.Except(closingSeqs).Where(seq => replay.TrueCaptureOf(seq) >= outageStart && replay.TrueCaptureOf(seq) < backendBack))
        {
            Assert.IsFalse(live[seq], $"record {seq} captured at {replay.TrueCaptureOf(seq):HH:mm:ss} during the outage");
        }

        for (var index = 0; index < closeIndex; index++)
        {
            AssertAll(live, batches[index].Batch, false, $"batch {index} arrived before the closing batch");
        }

        foreach (var record in closing.Records)
        {
            var expected = closing.ReceivedAt - replay.TrueCaptureOf(record.Seq) <= TimeSpan.FromSeconds(90);

            Assert.AreEqual(expected, live[record.Seq], $"record {record.Seq} of the closing batch is live exactly when it is at most 90 seconds old on arrival");
        }

        Assert.IsNotEmpty(closing.Records.Where(record => live[record.Seq]).ToList(), "the closing batch stores at least one current record live");

        var later = batches.Skip(closeIndex + 1).Where(batch => batch.Live).ToList();

        Assert.IsNotEmpty(later, "a live batch follows the closing one");
        AssertAll(live, later[0].Batch, true, "the current records of the batch after the closing one");
    }

    /// <summary>
    /// A restart of the agent in the same boot during the backfill, with a resent batch, stores each record once with its first classification and keeps the gap open until its numbers arrive.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineSameBootRestartDuringBackfillKeepsFirstClassification()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.Zero, 100, backfillBatchSize: 40);
        var steady = replay.AdvanceTo(_t0.AddMinutes(5));

        await WriteAllAsync(store, steady);

        var lastStored = (await LiveAsync(directory)).Keys.Max();
        var backendBack = _t0.AddMinutes(65);

        replay.SetBackend(false);
        replay.AdvanceTo(backendBack);
        replay.SetBackend(true);

        var first = replay.AdvanceTo(backendBack.AddSeconds(38));

        Assert.HasCount(3, first, "the current batch and two backfill batches");

        foreach (var batch in first)
        {
            await WriteAsync(store, batch.Batch);
        }

        var firstLive = first[0].Batch.Records.Min(record => record.Seq);
        var shrunk = await GapsAsync(store);

        replay.Restart();

        // Act
        var resent = await WriteAsync(store, TimelineSamples.Resend(first[2].Batch, replay.Now));
        var afterResend = await GapsAsync(store);
        var rest = replay.AdvanceTo(backendBack.AddMinutes(2));

        await WriteAllAsync(store, rest);

        var steadyResent = await WriteAsync(store, TimelineSamples.Resend(steady[^1].Batch, backendBack.AddMinutes(10)));
        var live = await LiveAsync(directory);
        var closed = await GapsAsync(store);

        // Assert
        Assert.AreSequenceEqual([$"{lastStored + 81}-{firstLive - 1} {OpenSequenceGap}"], TimelineSamples.SequenceGaps(shrunk), "the gap shrank by the two backfill batches");
        Assert.AreEqual(0, resent.Stored, "no resent record is stored again");
        Assert.AreEqual(40, resent.Duplicates, "every resent record is a duplicate");
        Assert.AreEqual(TimelineSamples.Fingerprint(shrunk), TimelineSamples.Fingerprint(afterResend), "the resend changes no gap");
        AssertAll(live, first[2].Batch, false, "the resent records keep their first classification");
        Assert.AreEqual(0, steadyResent.Stored, "a later resend of a live batch stores nothing");
        AssertAll(live, steady[^1].Batch, true, "a record stored live stays live when it arrives again late");
        Assert.IsTrue(closed.Where(gap => gap.Kind == GapKind.Sequence).All(gap => gap.State == GapState.Closed), "the gap closes when its numbers have arrived");
        Assert.HasCount(1, TimelineSamples.SequenceGaps(closed), "no other sequence gap");
    }

    /// <summary>
    /// A reboot with a numbering jump and a new boot ID makes the gap final with the first batch stored five minutes after the wait started, and the records of that batch are live.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineRebootJumpBecomesFinalAfterFiveMinutes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var opened = _t0.AddSeconds(120);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));

        // Act
        var jumpBatch = Rebooted(TimelineSamples.Run(1000, 2, opened));
        var jump = await WriteAsync(store, jumpBatch);
        var tooEarlyBatch = Rebooted(TimelineSamples.Run(1002, 1, opened.AddSeconds(299)));

        await WriteAsync(store, tooEarlyBatch);

        var stillOpen = await GapsAsync(store);
        var finalBatch = Rebooted(TimelineSamples.Run(1003, 1, opened.AddSeconds(300)));
        var final = await WriteAsync(store, finalBatch);
        var finalGaps = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.AreEqual(1, jump.Timeline!.Opened, "the jump opens one gap");
        Assert.AreEqual(1, jump.Timeline.Open, "one open sequence gap");
        Assert.AreSequenceEqual([$"4-999 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(stillOpen), "299 seconds after the opening the gap is open");
        Assert.AreEqual(1, final.Timeline!.Final, "the batch after the wait makes the gap final");
        Assert.AreEqual(0, final.Timeline.Open, "no open sequence gap is left");
        Assert.AreSequenceEqual(["4-999 final sequence_missing"], TimelineSamples.SequenceGaps(finalGaps), "the gap is final with its cause");
        AssertAll(live, jumpBatch, false, "the jump's own records, an open gap lies below");
        AssertAll(live, tooEarlyBatch, false, "the record 299 seconds after the opening, the gap is still open");
        AssertAll(live, finalBatch, true, "the record that finalizes the gap and later ones are live again");
    }

    /// <summary>
    /// The wait for the final state counts again from the first batch stored after the backend started.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineWaitRestartsAfterReopen()
    {
        // Arrange
        using var directory = new TempDirectory();
        var opened = _t0.AddSeconds(120);
        var afterStart = opened.AddMinutes(6);

        await using (var first = await Open(directory))
        {
            await WriteAsync(first, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
            await WriteAsync(first, Rebooted(TimelineSamples.Run(1000, 2, opened)));
        }

        await using var store = await Open(directory);

        // Act
        var reopened = await GapsAsync(store);
        var firstBatch = Rebooted(TimelineSamples.Run(1002, 1, afterStart));

        await WriteAsync(store, firstBatch);

        var afterFirst = await GapsAsync(store);
        var tooEarlyBatch = Rebooted(TimelineSamples.Run(1003, 1, afterStart.AddSeconds(299)));

        await WriteAsync(store, tooEarlyBatch);

        var afterEarly = await GapsAsync(store);
        var finalBatch = Rebooted(TimelineSamples.Run(1004, 1, afterStart.AddSeconds(300)));

        await WriteAsync(store, finalBatch);

        var afterFinal = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.AreSequenceEqual([$"4-999 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(reopened), "the open gap survives the restart");
        Assert.AreSequenceEqual([$"4-999 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(afterFirst), "six minutes after the opening, the first batch after the start does not finalize the gap");
        Assert.AreSequenceEqual([$"4-999 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(afterEarly), "299 seconds after the first batch after the start");
        Assert.AreSequenceEqual(["4-999 final sequence_missing"], TimelineSamples.SequenceGaps(afterFinal), "300 seconds after the first batch after the start");
        AssertAll(live, firstBatch, false, "the first batch after the start is backfilled");
        AssertAll(live, tooEarlyBatch, false, "the batch before the end of the new wait is backfilled");
        AssertAll(live, finalBatch, true, "the batch after the new wait is live");
    }

    /// <summary>
    /// A first batch after a restart that the store refuses leaves the wait to be restarted by the next batch it stores.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineRefusedFirstBatchAfterReopenKeepsWaitReset()
    {
        // Arrange
        using var directory = new TempDirectory();
        var opened = _t0.AddSeconds(120);
        var afterStart = opened.AddMinutes(6);

        await using (var first = await Open(directory))
        {
            await WriteAsync(first, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
            await WriteAsync(first, Rebooted(TimelineSamples.Run(1000, 2, opened)));
        }

        await using var store = await Open(directory);

        await TimelineSamples.ExecuteAsync(DbPath(directory), PoisonTrigger, TestContext.CancellationToken);

        var refusedBatch = Rebooted(TimelineSamples.BatchOf(AgentId, TimelineSamples.OtherBootId, afterStart, [TimelineSamples.Metric(1002, afterStart.AddSeconds(-1)), TimelineSamples.Metric(99, afterStart.AddSeconds(-1))]));

        // Act
        var refusal = await Assert.ThrowsExactlyAsync<StoreException>(() => store.WriteBatchAsync(refusedBatch, TestContext.CancellationToken), "the write is rolled back");
        var firstBatch = Rebooted(TimelineSamples.Run(1002, 1, afterStart.AddSeconds(1)));

        await WriteAsync(store, firstBatch);

        var afterFirst = await GapsAsync(store);
        var finalBatch = Rebooted(TimelineSamples.Run(1003, 1, afterStart.AddSeconds(301)));

        await WriteAsync(store, finalBatch);

        var afterFinal = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.Contains("store: writing batch", refusal.Message, "the refusal is a failed write");
        Assert.AreSequenceEqual([$"4-999 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(afterFirst), "the refused batch did not start the wait: the next one does, and does not finalize");
        Assert.AreSequenceEqual(["4-999 final sequence_missing"], TimelineSamples.SequenceGaps(afterFinal), "300 seconds after the first stored batch");
        AssertAll(live, firstBatch, false, "the first stored batch after the start is backfilled");
        AssertAll(live, finalBatch, true, "the batch after the wait is live");
        Assert.IsFalse(live.ContainsKey(99), "the refused record is not stored");
    }

    /// <summary>
    /// A gap record with cause spool_dropped makes the numbers it names final with its cause, and a later fresh record is live.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentSpoolDroppedGapFinalizesNamedNumbers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 10, _t0.AddSeconds(60)));

        var received = _t0.AddSeconds(120);
        var batch = TimelineSamples.Batch(received, TimelineSamples.AgentGap(50, received.AddSeconds(-1), GapCause.SpoolDropped, 11, 49), TimelineSamples.Metric(51, received.AddSeconds(-1)));

        // Act
        var result = await WriteAsync(store, batch);
        var gaps = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.AreEqual(0, result.Timeline!.Open, "no open sequence gap is left");
        Assert.AreSequenceEqual(["11-49 final spool_dropped"], TimelineSamples.SequenceGaps(gaps), "the named numbers are final with the agent's cause");
        Assert.IsTrue(live[51], "the gap does not hold back the next record");
    }

    /// <summary>
    /// A gap record that names only numbers at or above its own makes nothing final.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentGapAtOrAboveOwnNumberFinalizesNothing()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 10, _t0.AddSeconds(60)));

        var received = _t0.AddSeconds(120);
        var batch = TimelineSamples.Batch(received, TimelineSamples.AgentGap(50, received.AddSeconds(-1), GapCause.SpoolDropped, 50, 70));

        // Act
        await WriteAsync(store, batch);

        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreSequenceEqual([$"11-49 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(gaps), "numbers at or above the gap record's own number are never final");
    }

    /// <summary>
    /// A gap record that names numbers below and above its own makes only those below final.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentGapPartlyAboveOwnNumberFinalizesOnlyBelow()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 10, _t0.AddSeconds(60)));

        var received = _t0.AddSeconds(120);
        var batch = TimelineSamples.Batch(received, TimelineSamples.AgentGap(50, received.AddSeconds(-1), GapCause.SpoolDropped, 40, 60));

        // Act
        await WriteAsync(store, batch);

        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreSequenceEqual([$"11-39 {OpenSequenceGap}", "40-49 final spool_dropped"], TimelineSamples.SequenceGaps(gaps), "the numbers below the gap record are final, the others stay missing");
    }

    /// <summary>
    /// An agent stopped cleanly for an hour leaves a final time gap with cause no_data and no sequence gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineCleanStopHourIsFinalTimeGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(3719);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));

        // Act
        await WriteAsync(store, TimelineSamples.Run(4, 2, received));

        var gaps = await GapsAsync(store);

        // Assert
        Assert.HasCount(1, gaps, "only the time gap");
        Assert.AreEqual(GapKind.Time, gaps[0].Kind, "its kind");
        Assert.AreEqual(GapCause.NoData, gaps[0].Cause, "its cause");
        Assert.AreEqual(GapState.Final, gaps[0].State, "no sequence gap lies between its neighbours");
        Assert.AreEqual(_t0.AddSeconds(59), gaps[0].From, "it starts at the last sample before the stop");
        Assert.AreEqual(_t0.AddSeconds(3717), gaps[0].To, "it ends at the first sample after it");
        Assert.AreEqual(AgentId, gaps[0].AgentId, "its agent");
    }

    /// <summary>
    /// A time gap lies open while an open sequence gap lies between its neighbouring records, and reads final once none does.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineTimeGapOpenWhileSequenceGapBetween()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(3719);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, TimelineSamples.Run(10, 2, received));

        // Act
        var open = TimelineSamples.TimeGaps(await GapsAsync(store));

        await WriteAsync(store, TimelineSamples.Run(12, 1, received.AddSeconds(300)));

        var gaps = await GapsAsync(store);
        var final = TimelineSamples.TimeGaps(gaps);

        // Assert
        Assert.HasCount(1, open, "one time gap");
        Assert.AreEqual(GapState.Open, open[0].State, "an open sequence gap lies between the neighbouring records");
        Assert.AreSequenceEqual(["4-9 final sequence_missing"], TimelineSamples.SequenceGaps(gaps), "the sequence gap is final after the wait");
        Assert.AreEqual(GapState.Final, final.Single(gap => gap.From == _t0.AddSeconds(59)).State, "the same time gap reads final once no open sequence gap lies between its neighbours");
    }

    /// <summary>
    /// Records below the agent's first number open no sequence gap, but a time gap among the metrics is detected.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineTimeGapBelowFirstNumberDetected()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(1810);

        await WriteAsync(store, TimelineSamples.Run(100, 2, _t0.AddSeconds(1800)));

        // Act
        await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.Metric(50, _t0), TimelineSamples.Metric(51, _t0.AddSeconds(30))));

        var gaps = await GapsAsync(store);

        // Assert
        Assert.HasCount(1, gaps, "one gap");
        Assert.AreEqual(GapKind.Time, gaps[0].Kind, "a time gap, no sequence gap below the first number");
        Assert.AreEqual(_t0.AddSeconds(30), gaps[0].From, "it starts at the newest backfilled sample");
        Assert.AreEqual(_t0.AddSeconds(1798), gaps[0].To, "it ends at the first sample of the first batch");
        Assert.AreEqual(GapState.Final, gaps[0].State, "no open sequence gap lies between");
    }

    /// <summary>
    /// A time gap is stored beside the agent's own gap record, and readers see both.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineTimeGapBesideAgentGapStored()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(300);

        await WriteAsync(store, TimelineSamples.Run(1, 2, _t0.AddSeconds(60)));

        // Act
        await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.AgentGap(3, _t0.AddSeconds(299), GapCause.Unknown, 0, 0), TimelineSamples.Metric(4, _t0.AddSeconds(299))));

        var gaps = await GapsAsync(store);
        var records = await store.RecordsAsync(new RecordQuery
                                               {
                                                   Kind = RecordKind.Gap,
                                                   From = _t0.AddHours(-1),
                                                   To = _t0.AddHours(1),
                                                   Limit = 100
                                               },
                                               TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, gaps, "one time gap");
        Assert.AreEqual(GapCause.NoData, gaps[0].Cause, "the backend's own cause");
        Assert.AreEqual(_t0.AddSeconds(59), gaps[0].From, "from the last sample");
        Assert.AreEqual(_t0.AddSeconds(299), gaps[0].To, "to the next sample");
        Assert.HasCount(1, records, "the agent's gap record is stored too");
        Assert.AreEqual(GapCause.Unknown, ((Gap)records[0].Record.Data!).Cause, "with the agent's cause");
    }

    /// <summary>
    /// A time gap keeps the agent's raw capture times when a new offset is measured.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineTimeGapIgnoresOffsetChange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var stamp = _t0.AddHours(2);

        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(31), TimelineSamples.Metric(1, stamp), TimelineSamples.Metric(2, stamp.AddSeconds(30))));
        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(631), TimelineSamples.Metric(3, stamp.AddSeconds(630))));

        var before = TimelineSamples.TimeGaps(await GapsAsync(store));

        // Act
        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(661), TimelineSamples.SelfSample(4, stamp.AddSeconds(660), 10)));

        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var after = TimelineSamples.TimeGaps(await GapsAsync(store));

        // Assert
        Assert.HasCount(1, before, "one time gap before the measurement");
        Assert.AreEqual(7199 * NanosecondsPerSecond, clock!.OffsetNs, "the counted sample measures the clock two hours ahead");
        Assert.AreEqual(stamp.AddSeconds(30), before[0].From, "the raw capture time of the sample before the gap");
        Assert.AreEqual(stamp.AddSeconds(630), before[0].To, "the raw capture time of the sample after it");
        Assert.AreEqual(TimelineSamples.Fingerprint(before), TimelineSamples.Fingerprint(after), "the new offset moves no time gap");
    }

    /// <summary>
    /// With the agent's clock two hours ahead, no record the agent stamps with a time far from its receipt is live, whatever the kind.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineClockAheadNeverMakesOldRecordLive()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.FromHours(2), 100);
        var steady = replay.AdvanceTo(_t0.AddMinutes(8));

        await WriteAllAsync(store, steady);

        var metricSeq = replay.ReserveSeq();
        var logSeq = replay.ReserveSeq();
        var received = replay.Now;
        var stale = TimelineSamples.Batch(received, TimelineSamples.Metric(metricSeq, received), TimelineSamples.LogLine(logSeq, received.AddHours(2).AddMinutes(-20)));

        // Act
        await WriteAsync(store, stale);

        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var live = await LiveAsync(directory);

        // Assert
        Assert.IsFalse(live[metricSeq], "a metric whose time looks current only because the clock is two hours ahead is two hours old");
        Assert.IsFalse(live[logSeq], "a log line from 20 minutes before its receipt");
        AssertAll(live, steady[^1].Batch, true, "the last batch of the live stream");
        Assert.IsGreaterThanOrEqualTo(7196 * NanosecondsPerSecond, clock!.OffsetNs ?? long.MinValue, "the offset is measured");
        Assert.IsLessThanOrEqualTo(7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "the offset is a lower bound");

        foreach (var seq in live.Keys.Where(seq => seq != metricSeq && seq != logSeq))
        {
            Assert.IsTrue(live[seq], $"record {seq} of the live stream");
        }
    }

    /// <summary>
    /// With the agent's clock two hours behind, the first counted sample alone measures nothing, so records are backfilled until the batch that carries the second counted sample, which confirms the first; from that batch on records sent live are live, and an old record is not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineClockBehindLiveFromConfirmingSample()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.FromHours(-2), 100);
        var steady = replay.AdvanceTo(_t0.AddMinutes(6));
        var counted = Enumerable.Range(0, steady.Count).Where(index => HasCountedSample(steady[index])).ToList();

        Assert.IsGreaterThanOrEqualTo(3, counted.Count, "the replay brings several counted samples");

        var confirming = counted[1];
        AgentClock? afterFirst = null;

        for (var index = 0; index < steady.Count; index++)
        {
            await WriteAsync(store, steady[index].Batch);

            if (index == counted[0])
            {
                afterFirst = await store.ClockAsync(AgentId, TestContext.CancellationToken);
            }
        }

        var staleSeq = replay.ReserveSeq();
        var received = replay.Now;

        await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.Metric(staleSeq, received.AddHours(-2).AddMinutes(-20))));

        // Act
        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var live = await LiveAsync(directory);

        // Assert
        Assert.IsNull(afterFirst?.MeasuredAt, "the first counted sample alone measures nothing");
        Assert.IsGreaterThanOrEqualTo(-7204 * NanosecondsPerSecond, clock!.OffsetNs ?? long.MinValue, "the offset is measured from the confirmed samples");
        Assert.IsLessThanOrEqualTo(-7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "the offset is a lower bound");
        Assert.IsFalse(live[staleSeq], "a record 20 minutes before its receipt");

        for (var index = 0; index < steady.Count; index++)
        {
            AssertAll(live, steady[index].Batch, index >= confirming, index >= confirming ? "a batch sent live from the confirming sample on" : "a batch before the confirming sample");
        }
    }

    /// <summary>
    /// With the agent's clock two hours behind and its samples a little off the 60-second grid, the second counted sample still confirms the first: the offset is measured, and records are live from the batch with the second counted sample on.
    /// </summary>
    /// <param name="firstMilliseconds">The spacing of the first, third, ... sample in milliseconds</param>
    /// <param name="secondMilliseconds">The spacing of the second, fourth, ... sample in milliseconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(59_900, 60_100)]
    [DataRow(59_999, 59_999)]
    public async Task SqliteStoreTimelineJitteredSamplesConfirmBehindClock(int firstMilliseconds, int secondMilliseconds)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var skew = TimeSpan.FromHours(-2);
        var batches = new List<RecordBatch>();
        var seq = 100UL;
        var at = _t0;

        for (var index = 0; index < 8; index++)
        {
            var batch = SampleBatch(seq, at, skew, 30, true);

            batches.Add(batch);
            seq += (ulong)batch.Records.Count;
            at = at.AddMilliseconds(index % 2 == 0 ? firstMilliseconds : secondMilliseconds);
        }

        AgentClock? afterFirst = null;

        // Act
        for (var index = 0; index < batches.Count; index++)
        {
            await WriteAsync(store, batches[index]);

            if (index == 0)
            {
                afterFirst = await store.ClockAsync(AgentId, TestContext.CancellationToken);
            }
        }

        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var live = await LiveAsync(directory);

        // Assert
        Assert.IsNull(afterFirst?.MeasuredAt, "the first counted sample alone measures nothing");
        Assert.IsGreaterThanOrEqualTo(-7204 * NanosecondsPerSecond, clock!.OffsetNs ?? long.MinValue, "the offset is measured");
        Assert.IsLessThanOrEqualTo(-7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "the offset is a lower bound");
        AssertAll(live, batches[0], false, "the batch with the first counted sample");

        for (var index = 1; index < batches.Count; index++)
        {
            AssertAll(live, batches[index], true, $"batch {index} from the second counted sample on");
        }
    }

    /// <summary>
    /// After the agent's clock is stepped back by 5 minutes, its samples a little under 60 seconds apart confirm each other, and current records are live again within 10 minutes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineJitteredSamplesConfirmBackwardStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var spacing = TimeSpan.FromMilliseconds(59_900);
        var before = new List<RecordBatch>();
        var after = new List<RecordBatch>();
        var seq = 100UL;
        var at = _t0;

        for (var index = 0; index < 5; index++)
        {
            var batch = SampleBatch(seq, at, TimeSpan.Zero, 30, true);

            before.Add(batch);
            seq += (ulong)batch.Records.Count;
            at += spacing;
        }

        var stepAt = at;

        for (var index = 0; index < 13; index++)
        {
            var batch = SampleBatch(seq, at, TimeSpan.FromMinutes(-5), 30, true);

            after.Add(batch);
            seq += (ulong)batch.Records.Count;
            at += spacing;
        }

        // Act
        foreach (var batch in before.Concat(after))
        {
            await WriteAsync(store, batch);
        }

        var live = await LiveAsync(directory);

        // Assert
        AssertAll(live, before[^1], true, "the last batch before the step");
        AssertAll(live, after[0], false, "the first batch after the step is 5 minutes off");

        foreach (var batch in after.Where(batch => batch.ReceivedAt - stepAt >= TimeSpan.FromMinutes(10)))
        {
            AssertAll(live, batch, true, $"the batch received at {batch.ReceivedAt:HH:mm:ss}, 10 minutes after the step");
        }

        Assert.IsNotEmpty(after.Where(batch => batch.ReceivedAt - stepAt >= TimeSpan.FromMinutes(10)).ToList(), "batches 10 minutes after the step were written");
    }

    /// <summary>
    /// With the agent's clock two hours behind, a second sample that is missing or not counted does not break the confirmation: the third sample, 120 seconds after the first, confirms the first and measures.
    /// </summary>
    /// <param name="missing"><see langword="true"/> when the second batch carries no self sample, <see langword="false"/> when its sample has a send lag over 60 seconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SqliteStoreTimelineSkippedSampleStillConfirms(bool missing)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var skew = TimeSpan.FromHours(-2);
        var batches = new List<RecordBatch>();
        var seq = 100UL;

        for (var index = 0; index < 5; index++)
        {
            var lag = index == 1 ? 90 : 30;
            var samples = index == 1 && missing ? 0 : 1;
            var batch = SampleBatch(seq, _t0.AddSeconds(60 * index), skew, lag, samples > 0);

            batches.Add(batch);
            seq += (ulong)batch.Records.Count;
        }

        AgentClock? afterSecond = null;

        // Act
        for (var index = 0; index < batches.Count; index++)
        {
            await WriteAsync(store, batches[index]);

            if (index == 1)
            {
                afterSecond = await store.ClockAsync(AgentId, TestContext.CancellationToken);
            }
        }

        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var live = await LiveAsync(directory);

        // Assert
        Assert.IsNull(afterSecond?.MeasuredAt, "no counted sample confirmed the first one yet");
        Assert.IsGreaterThanOrEqualTo(-7204 * NanosecondsPerSecond, clock!.OffsetNs ?? long.MinValue, "the third sample measures the offset");
        Assert.IsLessThanOrEqualTo(-7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "the offset is a lower bound");
        AssertAll(live, batches[0], false, "the batch with the first counted sample");
        AssertAll(live, batches[1], false, "the batch before the measurement");

        for (var index = 2; index < batches.Count; index++)
        {
            AssertAll(live, batches[index], true, $"batch {index} from the third sample on");
        }
    }

    /// <summary>
    /// With the agent's holes full, so that its live stream sends the outage's records first with consecutive numbers and a late self sample is counted, no record captured during the outage more than 90 seconds before its receipt is stored live, the offset stays unchanged through the replay, and current batches are live once their counted samples arrive.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineFullHolesOutageReplayNeverStoresOutageRecordLive()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var start = new DateTimeOffset(2026, 10, 1, 21, 50, 0, TimeSpan.Zero);
        var outageStart = start.AddMinutes(10);
        var backendBack = outageStart.AddHours(13);
        var replay = new AgentReplay(start, TimeSpan.Zero, 1000)
                     {
                         FullHoles = true
                     };

        await WriteAllAsync(store, replay.AdvanceTo(outageStart));

        var clockBefore = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        replay.SetBackend(false);
        replay.AdvanceTo(backendBack);
        replay.SetBackend(true);

        var batches = replay.AdvanceTo(backendBack.AddMinutes(3));
        var receivedOf = new Dictionary<ulong, DateTimeOffset>();
        var clockHeld = true;
        var held = 0;

        // Act
        foreach (var batch in batches)
        {
            await WriteAsync(store, batch.Batch);

            foreach (var record in batch.Batch.Records)
            {
                receivedOf[record.Seq] = batch.Batch.ReceivedAt;
            }

            var inOutage = batch.Batch.Records.All(record => replay.TrueCaptureOf(record.Seq) < backendBack.AddMinutes(-2));

            if (inOutage)
            {
                var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);

                clockHeld &= clock!.OffsetNs == clockBefore!.OffsetNs && clock.MeasuredAt == clockBefore.MeasuredAt;
                held++;
            }
        }

        var live = await LiveAsync(directory);
        var clockAfter = await store.ClockAsync(AgentId, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(clockBefore?.MeasuredAt, "the 10 minutes of live batches measured the offset");
        Assert.IsGreaterThan(0, held, "batches of the outage's records were written");
        Assert.IsTrue(clockHeld, "the offset and its time stay unchanged while the outage's records arrive");
        Assert.IsGreaterThan(1, batches.Count(batch => batch.Batch.Records.Any(record => replay.TrueCaptureOf(record.Seq) < backendBack.AddMinutes(-2))), "the outage's records come in several batches");

        foreach (var seq in live.Keys.Where(seq => replay.TrueCaptureOf(seq) >= outageStart && replay.TrueCaptureOf(seq) < backendBack && receivedOf[seq] - replay.TrueCaptureOf(seq) > TimeSpan.FromSeconds(90)))
        {
            Assert.IsFalse(live[seq], $"record {seq} captured at {replay.TrueCaptureOf(seq):HH:mm:ss} during the outage");
        }

        Assert.IsNotNull(clockAfter?.MeasuredAt, "the offset is still known at the end");
        Assert.IsGreaterThan(backendBack, clockAfter.MeasuredAt ?? DateTimeOffset.MinValue, "a current counted sample measured the offset after the outage's records");

        var last = batches[^1];

        Assert.IsTrue(last.Live, "the last batch is a live one");
        AssertAll(live, last.Batch, true, "current records once their counted samples arrived");
    }

    /// <summary>
    /// An agent without log traffic has its samples counted: its send lag stays within the bound and the offset is measured.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineQuietAgentSamplesAreCounted()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.FromHours(2), 100);

        await WriteAllAsync(store, replay.AdvanceTo(_t0.AddMinutes(5)));

        // Act
        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var newestLag = await TimelineSamples.CountAsync(DbPath(directory), "SELECT value FROM metrics WHERE name = 'agent_send_lag_seconds' ORDER BY record_id DESC LIMIT 1", TestContext.CancellationToken);
        var largestLag = await TimelineSamples.CountAsync(DbPath(directory), "SELECT max(value) FROM metrics WHERE name = 'agent_send_lag_seconds'", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(30L, newestLag, "the lag of a quiet agent is one system interval");
        Assert.IsLessThanOrEqualTo((long)TimelineLimits.MaxCountedSendLagSeconds, largestLag, "every sample reports a lag within the bound");
        Assert.IsNotNull(clock!.MeasuredAt, "the offset is measured");
        Assert.IsGreaterThanOrEqualTo(7196 * NanosecondsPerSecond, clock.OffsetNs ?? long.MinValue, "from the counted samples");
        Assert.IsLessThanOrEqualTo(7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "a lower bound of the offset");
    }

    /// <summary>
    /// An agent two hours ahead that was installed while the backend was off keeps its backfilled records backfilled while no sample counts, even when a backfill batch is the first after the largest bound expires.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineClockAheadBackfillAfterExpiryStaysBackfilled()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0.AddHours(-2).AddSeconds(-7), TimeSpan.FromHours(2), 1000, false, 4);

        replay.AdvanceTo(_t0);
        replay.SetBackend(true);

        var batches = replay.AdvanceTo(_t0.AddMinutes(14));
        var lost = batches.Where(batch => batch.Live).Skip(1).First();
        var received = new Dictionary<ulong, DateTimeOffset>();

        // Act
        foreach (var batch in batches.Where(batch => batch != lost))
        {
            await WriteAsync(store, batch.Batch);

            foreach (var record in batch.Batch.Records)
            {
                received[record.Seq] = batch.Batch.ReceivedAt;
            }
        }

        var live = await LiveAsync(directory);
        var gaps = await GapsAsync(store);
        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var lastLive = batches.Last(batch => batch.Live);

        // Assert
        Assert.AreSequenceEqual([$"{lost.Batch.Records.Min(record => record.Seq)}-{lost.Batch.Records.Max(record => record.Seq)} final sequence_missing"], TimelineSamples.SequenceGaps(gaps), "the batch that never arrives is a gap that became final after the wait");

        foreach (var seq in live.Keys)
        {
            var lateness = received[seq] - replay.TrueCaptureOf(seq);

            Assert.IsFalse(lateness > TimeSpan.FromSeconds(100) && live[seq], $"record {seq} was captured {lateness.TotalSeconds} seconds before its receipt");
        }

        AssertAll(live, lastLive.Batch, true, "the current records after the gap became final and samples count again");
        Assert.IsGreaterThanOrEqualTo(7196 * NanosecondsPerSecond, clock!.OffsetNs ?? long.MinValue, "the offset is measured at the end");
        Assert.IsLessThanOrEqualTo(7200 * NanosecondsPerSecond, clock.OffsetNs ?? long.MaxValue, "a lower bound of the offset");
    }

    /// <summary>
    /// After the agent's clock steps back by five minutes, current records are backfilled first and live again within ten minutes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineClockBackwardStepRecoversWithinTenMinutes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var replay = new AgentReplay(_t0, TimeSpan.Zero, 100);

        await WriteAllAsync(store, replay.AdvanceTo(_t0.AddMinutes(10)));

        replay.StepClock(TimeSpan.FromMinutes(-5));

        var batches = replay.AdvanceTo(_t0.AddMinutes(21));

        // Act
        await WriteAllAsync(store, batches);

        var live = await LiveAsync(directory);
        var afterStep = batches.Where(batch => batch.Batch.ReceivedAt > _t0.AddMinutes(10).AddSeconds(2)).ToList();
        var first = afterStep.Where(batch => batch.Batch.ReceivedAt < _t0.AddMinutes(11)).ToList();
        var last = afterStep.Where(batch => batch.Batch.ReceivedAt > _t0.AddMinutes(20)).ToList();

        // Assert
        Assert.IsNotEmpty(first, "batches within a minute after the step");
        Assert.IsNotEmpty(last, "batches ten minutes after the step");

        foreach (var batch in first)
        {
            AssertAll(live, batch.Batch, false, "records stamped five minutes behind a measurement that does not know the step");
        }

        foreach (var batch in last)
        {
            AssertAll(live, batch.Batch, true, "records ten minutes after the step");
        }
    }

    /// <summary>
    /// An agent's first numbers open no sequence gap below them, whatever order the batches come in, and the records are stored by lateness.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineNoSequenceGapBelowFirstNumber()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var older = TimelineSamples.Run(50, 3, _t0.AddSeconds(70));

        // Act
        await WriteAsync(store, TimelineSamples.Run(100, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, older);
        await WriteAsync(store, TimelineSamples.Run(103, 1, _t0.AddSeconds(80)));

        var gaps = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.IsEmpty(TimelineSamples.SequenceGaps(gaps), "no number below the first one is missing");
        AssertAll(live, older, true, "records below the first number are stored by lateness");
    }

    /// <summary>
    /// A record in the middle of an open gap splits it into the lower and the upper missing part, and the parts close as their numbers arrive.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineMiddleFillSplitsGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, TimelineSamples.Run(10, 1, _t0.AddSeconds(90)));

        // Act
        var splitting = await WriteAsync(store, TimelineSamples.OldRun(6, 1, _t0.AddSeconds(5), _t0.AddSeconds(120)));

        var split = await GapsAsync(store);

        await WriteAsync(store, TimelineSamples.OldRun(4, 2, _t0.AddSeconds(3), _t0.AddSeconds(121)));

        var lowerClosed = await GapsAsync(store);

        await WriteAsync(store, TimelineSamples.OldRun(7, 3, _t0.AddSeconds(8), _t0.AddSeconds(122)));

        var closed = await GapsAsync(store);

        // Assert
        Assert.AreEqual(0, splitting.Timeline?.Opened ?? 0, "a split-off row is no newly detected gap");
        Assert.AreEqual(0, splitting.Timeline?.Closed ?? 0, "the split closes no gap");
        Assert.AreSequenceEqual([$"4-5 {OpenSequenceGap}", $"7-9 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(split), "the lower part keeps the gap, the upper part is a new gap of the same cause and state");
        Assert.AreSequenceEqual([$"7-9 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(lowerClosed).Where(text => text.Contains("open", StringComparison.Ordinal)).ToList(), "the lower part is filled");
        Assert.IsEmpty(closed.Where(gap => gap.Kind == GapKind.Sequence && gap.State != GapState.Closed).ToList(), "a gap that lost all its missing numbers is closed");
        Assert.HasCount(2, closed.Where(gap => gap.Kind == GapKind.Sequence).ToList(), "two closed gaps remain readable");
    }

    /// <summary>
    /// Numbers that arrive at the upper end of an open gap shrink it from above and open no new gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineUpperFillShrinksGapFromAbove()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, TimelineSamples.Run(10, 1, _t0.AddSeconds(90)));

        // Act
        var result = await WriteAsync(store, TimelineSamples.OldRun(7, 3, _t0.AddSeconds(8), _t0.AddSeconds(120)));
        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreSequenceEqual([$"4-6 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(gaps), "the numbers below the filled ones are still missing");
        Assert.AreEqual(0, result.Timeline?.Opened ?? 0, "the fill opens no gap");
        Assert.AreEqual(0, result.Timeline?.Closed ?? 0, "the fill closes no gap");
    }

    /// <summary>
    /// A gap record that names numbers no open gap contains, because they are stored or lie above every gap, changes no gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentGapNamingNoMissingNumbersChangesNothing()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, TimelineSamples.Run(7, 2, _t0.AddSeconds(90)));

        var before = TimelineSamples.Fingerprint(await GapsAsync(store));
        var received = _t0.AddSeconds(120);

        // Act
        await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.AgentGap(9, received.AddSeconds(-1), GapCause.SpoolDropped, 8, 8)));

        var above = TimelineSamples.Fingerprint(await GapsAsync(store));

        await WriteAsync(store, TimelineSamples.Batch(received.AddSeconds(1), TimelineSamples.AgentGap(10, received, GapCause.SpoolDropped, 2, 3)));

        var below = TimelineSamples.Fingerprint(await GapsAsync(store));

        // Assert
        Assert.AreEqual(before, above, "the stored numbers above the open gap make nothing final");
        Assert.AreEqual(before, below, "the stored numbers below the open gap make nothing final");
        Assert.AreSequenceEqual([$"4-6 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(await GapsAsync(store)), "the gap stays open");
    }

    /// <summary>
    /// A gap record that names the lower part of an open gap makes only that part final and leaves the upper rest open.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentGapNamingLowerPartLeavesUpperRestOpen()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 10, _t0.AddSeconds(60)));

        var received = _t0.AddSeconds(120);

        // Act
        var result = await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.AgentGap(50, received.AddSeconds(-1), GapCause.SpoolDropped, 11, 20)));
        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreSequenceEqual(["11-20 final spool_dropped", $"21-49 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(gaps), "the named numbers are final, the rest above them stays missing");
        Assert.AreEqual(1, result.Timeline!.Open, "the rest is the one open sequence gap");
    }

    /// <summary>
    /// A sample time inside a time gap splits it, narrows it from above or from below, according to how much of the gap is left on each side, and opens no new gap.
    /// </summary>
    /// <param name="offsetSeconds">The capture time of the new sample, seconds after the start</param>
    /// <param name="expected">The time gaps afterwards, in seconds after the start</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(330, "30-330,330-630")]
    [DataRow(570, "30-570")]
    [DataRow(90, "90-630")]
    public async Task SqliteStoreTimelineSampleInsideTimeGapNarrowsOrSplitsIt(int offsetSeconds, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(31), TimelineSamples.Metric(1, _t0), TimelineSamples.Metric(2, _t0.AddSeconds(30))));
        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(631), TimelineSamples.Metric(3, _t0.AddSeconds(630))));

        // Act
        var result = await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(632), TimelineSamples.Metric(4, _t0.AddSeconds(offsetSeconds))));
        var gaps = TimelineSamples.TimeGaps(await GapsAsync(store));

        // Assert
        Assert.AreEqual(expected, string.Join(",", gaps.Select(gap => $"{(gap.From - _t0).TotalSeconds}-{(gap.To - _t0).TotalSeconds}")), "the time gaps left");
        Assert.IsTrue(gaps.All(gap => gap.State != GapState.Closed), "no time gap closes");
        Assert.AreEqual(0, result.Timeline?.Opened ?? 0, "a split-off part is no newly detected gap");
    }

    /// <summary>
    /// A batch of an agent that holds no agent record is stored and changes no gap and no clock.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineBatchWithoutAgentRecordsChangesNoTimeline()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var batch = TimelineSamples.BatchOf(AgentId, TimelineSamples.BootId, _t0.AddSeconds(30), [Samples.Log(RecordOrigin.Backend, 0, 0, "written by the backend")]);

        // Act
        var result = await WriteAsync(store, batch);
        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreEqual(1, result.Stored, "the record is stored");
        Assert.IsNull(result.Timeline, "nothing of note changed");
        Assert.IsEmpty(gaps, "no gap exists");
    }

    /// <summary>
    /// A final gap shrinks and closes like an open one when its numbers arrive, and the records are backfilled.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineFinalGapShrinksAndCloses()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var opened = _t0.AddSeconds(90);
        var finalized = opened.AddSeconds(300);
        var firstFill = TimelineSamples.OldRun(4, 2, _t0.AddSeconds(5), finalized.AddSeconds(10));
        var lastFill = TimelineSamples.OldRun(6, 4, _t0.AddSeconds(8), finalized.AddSeconds(20));

        await WriteAsync(store, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
        await WriteAsync(store, TimelineSamples.Run(10, 1, opened));
        await WriteAsync(store, TimelineSamples.Run(11, 1, finalized));

        var final = await GapsAsync(store);

        // Act
        await WriteAsync(store, firstFill);

        var shrunk = await GapsAsync(store);

        await WriteAsync(store, lastFill);

        var closed = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.AreSequenceEqual(["4-9 final sequence_missing"], TimelineSamples.SequenceGaps(final), "the wait made the gap final");
        Assert.AreSequenceEqual(["6-9 final sequence_missing"], TimelineSamples.SequenceGaps(shrunk), "a final gap shrinks like an open one");
        Assert.IsEmpty(closed.Where(gap => gap.Kind == GapKind.Sequence && gap.State != GapState.Closed).ToList(), "a final gap whose numbers all arrived is closed");
        AssertAll(live, firstFill, false, "records filled into a final gap are old and backfilled");
        AssertAll(live, lastFill, false, "the last records of the gap are backfilled");
    }

    /// <summary>
    /// Open and final gaps and their fields survive a restart of the backend.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineGapStatesSurviveReopen()
    {
        // Arrange
        using var directory = new TempDirectory();
        var finalizing = _t0.AddSeconds(90).AddSeconds(300);
        string before;

        await using (var first = await Open(directory))
        {
            await WriteAsync(first, TimelineSamples.Run(1, 3, _t0.AddSeconds(60)));
            await WriteAsync(first, TimelineSamples.Run(10, 1, _t0.AddSeconds(90)));
            await WriteAsync(first, TimelineSamples.Run(11, 1, finalizing));
            await WriteAsync(first, TimelineSamples.Run(20, 1, finalizing.AddSeconds(10)));

            before = TimelineSamples.Fingerprint(await GapsAsync(first));
        }

        // Act
        await using var store = await Open(directory);

        var after = await GapsAsync(store);
        var open = await GapsAsync(store, GapState.Open);
        var final = await GapsAsync(store, GapState.Final);

        // Assert
        Assert.AreEqual(before, TimelineSamples.Fingerprint(after), "every field of every gap is read back as it was");
        Assert.AreSequenceEqual([$"12-19 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(open), "the open gap");
        Assert.AreSequenceEqual(["4-9 final sequence_missing"], TimelineSamples.SequenceGaps(final), "the final gap");
    }

    /// <summary>
    /// A refused batch, by the validation and by a write that is rolled back, changes no gap, offset or record, and a valid batch afterwards finds the state as before.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineRefusedBatchChangesNothing()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(60), TimelineSamples.Metric(1, _t0.AddSeconds(57)), TimelineSamples.Metric(2, _t0.AddSeconds(58)), TimelineSamples.SelfSample(3, _t0.AddSeconds(59), 10)));
        await WriteAsync(store, TimelineSamples.Run(10, 1, _t0.AddSeconds(90)));
        await TimelineSamples.ExecuteAsync(DbPath(directory), PoisonTrigger, TestContext.CancellationToken);

        var before = await SnapshotAsync(store, directory);
        var late = _t0.AddSeconds(400);
        var invalid = new DataRecord
                      {
                          Origin = RecordOrigin.Agent,
                          Source = "system",
                          Seq = 0,
                          CapturedAt = late,
                          Data = new MetricPoint
                                 {
                                     Name = "cpu_user_percent",
                                     Unit = "percent"
                                 }
                      };

        // Act
        await Assert.ThrowsExactlyAsync<InvalidBatchException>(() => store.WriteBatchAsync(TimelineSamples.Batch(late, TimelineSamples.Metric(20, late), invalid), TestContext.CancellationToken), "refused by the validation");

        var afterInvalid = await SnapshotAsync(store, directory);

        await Assert.ThrowsExactlyAsync<StoreException>(() => store.WriteBatchAsync(TimelineSamples.Batch(late, TimelineSamples.Metric(20, late), TimelineSamples.Metric(99, late)), TestContext.CancellationToken), "refused after the gaps were prepared");

        var afterRollback = await SnapshotAsync(store, directory);

        await WriteAsync(store, TimelineSamples.Run(20, 1, _t0.AddSeconds(200)));

        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreEqual(before, afterInvalid, "a batch the validation refuses changes nothing");
        Assert.AreEqual(before, afterRollback, "a write that is rolled back changes nothing");
        Assert.AreSequenceEqual([$"4-9 {OpenSequenceGap}", $"11-19 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(gaps), "the next batch finds the state as before: the 300-second wait did not start");
    }

    /// <summary>
    /// Two batches of one agent written at once give the result of one of the two orders in which they can commit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineConcurrentBatchesMatchCommitOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(60);
        var firstThenSecond = $"4-9 {OpenSequenceGap}|1:1,2:1,3:1,10:0,11:0,12:0";
        var secondThenFirst = "|1:1,2:1,3:1,10:1,11:1,12:1";

        for (var index = 0; index < 25; index++)
        {
            var agent = $"agent-{index + 2}";
            var low = TimelineSamples.BatchOf(agent, TimelineSamples.BootId, received, TimelineSamples.Run(1, 3, received).Records);
            var high = TimelineSamples.BatchOf(agent, TimelineSamples.BootId, received, TimelineSamples.Run(10, 3, received).Records);

            // Act
            await Task.WhenAll(store.WriteBatchAsync(low, TestContext.CancellationToken), store.WriteBatchAsync(high, TestContext.CancellationToken));

            var gaps = TimelineSamples.SequenceGaps(await GapsAsync(store, agent: agent));
            var live = await LiveAsync(directory, agent);
            var actual = $"{string.Join(",", gaps)}|{string.Join(",", live.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{(pair.Value ? 1 : 0)}"))}";

            // Assert
            Assert.IsTrue(actual == firstThenSecond || actual == secondThenFirst, $"round {index}: {actual} is the result of neither commit order");
        }
    }

    /// <summary>
    /// A batch of metrics that jump in number and in time between every record adds at most two gap rows per record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineJumpInNumberAndTimeAddsAtMostTwoRowsPerRecord()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        const int count = 20;
        var records = Enumerable.Range(0, count).Select(index => TimelineSamples.Metric(1UL + (ulong)(index * 10), _t0.AddMinutes(index * 10))).ToArray();

        // Act
        var result = await WriteAsync(store, TimelineSamples.Batch(_t0.AddMinutes((count * 10) + 1), records));

        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreEqual(count, result.Stored, "every record is stored");
        Assert.IsLessThanOrEqualTo(2 * count, gaps.Count, "at most two gap rows per stored record");
        Assert.HasCount(count - 1, TimelineSamples.SequenceGaps(gaps), "one sequence gap per jump in number");
        Assert.HasCount(count - 1, TimelineSamples.TimeGaps(gaps), "one time gap per jump in time");
    }

    /// <summary>
    /// Numbers near 2^63 - 1 are stored, open gaps and backfill the records after them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineNumbersNearMaximumWork()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        const ulong top = long.MaxValue;

        // Act
        var first = await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(60), TimelineSamples.Metric(top - 10, _t0.AddSeconds(59))));
        var second = await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(70), TimelineSamples.Metric(top - 3, _t0.AddSeconds(69))));
        var third = await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(80), TimelineSamples.Metric(top, _t0.AddSeconds(79))));

        var gaps = await GapsAsync(store);
        var live = await LiveAsync(directory);

        // Assert
        Assert.AreEqual(1, first.Stored + second.Stored + third.Stored - 2, "every batch stores its record");
        Assert.AreSequenceEqual([$"{top - 9}-{top - 4} {OpenSequenceGap}", $"{top - 2}-{top - 1} {OpenSequenceGap}"], TimelineSamples.SequenceGaps(gaps), "the gaps between the numbers");
        Assert.IsTrue(live[top - 10], "the first record is live");
        Assert.IsFalse(live[top - 3], "an open gap lies below the second record");
        Assert.IsFalse(live[top], "open gaps lie below the last record");
    }

    /// <summary>
    /// A gap record naming numbers above the largest storable number is stored, and only the numbers below its own are made final.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineAgentGapRangeAboveLongMaximumWorks()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Run(1, 10, _t0.AddSeconds(60)));

        var received = _t0.AddSeconds(120);

        // Act
        var straddling = await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.AgentGap(50, received.AddSeconds(-1), GapCause.SpoolDropped, 40, ulong.MaxValue)));
        var afterStraddling = await GapsAsync(store);
        var above = await WriteAsync(store, TimelineSamples.Batch(received.AddSeconds(1), TimelineSamples.AgentGap(60, received, GapCause.SpoolDropped, (ulong)long.MaxValue + 1, ulong.MaxValue)));
        var afterAbove = await GapsAsync(store);

        // Assert
        Assert.AreEqual(1, straddling.Stored, "the record naming numbers up to the largest ulong is stored");
        Assert.AreEqual(1, above.Stored, "the record naming only numbers above the storable range is stored");
        Assert.AreSequenceEqual([$"11-39 {OpenSequenceGap}", "40-49 final spool_dropped"], TimelineSamples.SequenceGaps(afterStraddling), "clipped to the numbers below its own");
        Assert.AreSequenceEqual([$"11-39 {OpenSequenceGap}", "40-49 final spool_dropped", $"51-59 {OpenSequenceGap}"], TimelineSamples.SequenceGaps(afterAbove), "numbers above the storable range finalize nothing");
    }

    /// <summary>
    /// Capture times at both ends of the storable range are stored and backfilled, and the time gap between them is read back.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineCaptureTimesAtStorableEndsStoredBackfilled()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var batch = TimelineSamples.Batch(_t0.AddSeconds(60), TimelineSamples.Metric(1, StorableTime.Min), TimelineSamples.Metric(2, StorableTime.Max), TimelineSamples.LogLine(3, StorableTime.Max));

        // Act
        var result = await WriteAsync(store, batch);

        var live = await LiveAsync(directory);
        var gaps = await GapsAsync(store);

        // Assert
        Assert.AreEqual(3, result.Stored, "every record is stored");
        AssertAll(live, batch, false, "records captured at the ends of the storable range");
        Assert.HasCount(1, TimelineSamples.TimeGaps(gaps), "the time gap between the two samples");
        Assert.AreEqual(StorableTime.Min, TimelineSamples.TimeGaps(gaps)[0].From, "from the earliest storable time");
        Assert.AreEqual(StorableTime.Max, TimelineSamples.TimeGaps(gaps)[0].To, "to the latest storable time");
    }

    /// <summary>
    /// Imported records and records of the backend are read as backfilled, even when their time is close to their receipt.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineImportRecordsReadBackfilled()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var file = await store.BeginImportAsync(Samples.ImportStart(1), TestContext.CancellationToken);
        var imported = Samples.Batch(Samples.Metric(RecordOrigin.Import, 0, 30, "imported"));

        imported.Import = new ImportStep
                          {
                              FileId = file.Id,
                              Done = 0,
                              Complete = true
                          };

        // Act
        await WriteAsync(store, imported);
        await WriteAsync(store, Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 30, "backend")));
        await WriteAsync(store, TimelineSamples.Run(1, 1, Samples.Base.AddSeconds(60)));

        var metrics = await store.RecordsAsync(Samples.Query(RecordKind.Metric), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(3, metrics, "all three records");
        Assert.IsTrue(metrics.Single(metric => metric.Record.Origin == RecordOrigin.Agent).Live, "an agent record as close to its receipt is live");
        Assert.IsFalse(metrics.Single(metric => metric.Record.Origin == RecordOrigin.Import).Live, "an imported record is historical");
        Assert.IsFalse(metrics.Single(metric => metric.Record.Origin == RecordOrigin.Backend).Live, "a record of the backend is no live data of an agent");
    }

    /// <summary>
    /// Every record read through the store carries its classification, for metrics and for log lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineRecordsCarryClassification()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var received = _t0.AddSeconds(60);

        await WriteAsync(store, TimelineSamples.Batch(received, TimelineSamples.Metric(1, _t0.AddSeconds(58)), TimelineSamples.Metric(2, _t0.AddSeconds(59)), TimelineSamples.LogLine(3, _t0.AddSeconds(59))));
        await WriteAsync(store, TimelineSamples.Batch(received.AddSeconds(1), TimelineSamples.Metric(4, _t0.AddMinutes(-20)), TimelineSamples.LogLine(5, _t0.AddMinutes(-11))));
        await WriteAsync(store, Samples.Batch(Samples.Log(RecordOrigin.Backend, 0, 30, "backend line")));

        // Act
        var metrics = await store.RecordsAsync(MetricsAround(RecordKind.Metric), TestContext.CancellationToken);
        var logs = await store.RecordsAsync(MetricsAround(RecordKind.LogLine), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(metrics.Single(metric => metric.Record.Seq == 1).Live, "a fresh agent metric is live");
        Assert.IsTrue(metrics.Single(metric => metric.Record.Seq == 2).Live, "another fresh agent metric is live");
        Assert.IsFalse(metrics.Single(metric => metric.Record.Seq == 4).Live, "an agent metric 20 minutes old is backfilled");
        Assert.IsTrue(logs.Single(line => line.Record.Seq == 3).Live, "a fresh agent log line is live");
        Assert.IsFalse(logs.Single(line => line.Record.Seq == 5).Live, "an agent log line 11 minutes old is backfilled");
        Assert.IsFalse(logs.Single(line => line.Record.Origin == RecordOrigin.Backend).Live, "a record of the backend is not live");
    }

    /// <summary>
    /// The gaps of an agent and its measured offset are read with every field, in pages by ID and by state.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineGapsAndClockReadable()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);

        await WriteAsync(store, TimelineSamples.Batch(_t0.AddSeconds(60), TimelineSamples.Metric(1, _t0.AddSeconds(57)), TimelineSamples.Metric(2, _t0.AddSeconds(58)), TimelineSamples.SelfSample(3, _t0.AddSeconds(59), 10)));
        await WriteAsync(store, TimelineSamples.Run(10, 1, _t0.AddSeconds(4000)));

        // Act
        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var gaps = await GapsAsync(store);
        var firstPage = await store.GapsAsync(new GapQuery
                                              {
                                                  AgentId = AgentId,
                                                  Limit = 1
                                              },
                                              TestContext.CancellationToken);
        var secondPage = await store.GapsAsync(new GapQuery
                                               {
                                                   AgentId = AgentId,
                                                   AfterId = firstPage[0].Id,
                                                   Limit = 1
                                               },
                                               TestContext.CancellationToken);
        var lastPage = await store.GapsAsync(new GapQuery
                                             {
                                                 AgentId = AgentId,
                                                 AfterId = secondPage[0].Id,
                                                 Limit = 1
                                             },
                                             TestContext.CancellationToken);
        var open = await GapsAsync(store, GapState.Open);
        var final = await GapsAsync(store, GapState.Final);
        var other = await GapsAsync(store, agent: "agent-2");
        var otherClock = await store.ClockAsync("agent-2", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(AgentId, clock!.AgentId, "the agent of the clock");
        Assert.AreEqual(-NanosecondsPerSecond, clock.OffsetNs, "the offset of the counted sample");
        Assert.AreEqual(_t0.AddSeconds(60), clock.MeasuredAt, "measured when the counted sample was received");
        Assert.HasCount(2, gaps, "a sequence gap and a time gap");

        var sequence = gaps.Single(gap => gap.Kind == GapKind.Sequence);
        var time = gaps.Single(gap => gap.Kind == GapKind.Time);

        Assert.AreEqual(AgentId, sequence.AgentId, "the agent of the sequence gap");
        Assert.AreEqual(GapCause.SequenceMissing, sequence.Cause, "its cause");
        Assert.AreEqual(GapState.Open, sequence.State, "its state");
        Assert.AreEqual(4UL, sequence.FirstSeq, "its first number");
        Assert.AreEqual(9UL, sequence.LastSeq, "its last number");
        Assert.AreEqual(_t0.AddSeconds(59), sequence.From, "from the newest capture time before the jump");
        Assert.AreEqual(_t0.AddSeconds(3999), sequence.To, "to the capture time of the record after it");
        Assert.AreNotEqual(default, sequence.DetectedAt, "when it was detected");
        Assert.IsGreaterThanOrEqualTo(sequence.DetectedAt, sequence.ChangedAt, "it was last changed when or after it was detected");
        Assert.AreEqual(GapCause.NoData, time.Cause, "the cause of the time gap");
        Assert.AreEqual(GapState.Open, time.State, "an open sequence gap lies between its neighbours");
        Assert.AreEqual(_t0.AddSeconds(59), time.From, "the time gap starts at the last sample");
        Assert.AreEqual(_t0.AddSeconds(3999), time.To, "and ends at the next sample");
        Assert.AreSequenceEqual([gaps[0].Id], firstPage.Select(gap => gap.Id), "the first page by ID");
        Assert.AreSequenceEqual([gaps[1].Id], secondPage.Select(gap => gap.Id), "the second page by ID");
        Assert.IsEmpty(lastPage, "no further gap");
        Assert.HasCount(2, open, "both gaps are open");
        Assert.IsEmpty(final, "no gap is final");
        Assert.IsEmpty(other, "another agent has no gap");
        Assert.IsNull(otherClock, "another agent has no clock");
    }

    /// <summary>
    /// A gap query that breaks a rule is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTimelineGapQueryRefusesInvalidQueries()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await Open(directory);
        var queries = new[]
                      {
                          new GapQuery
                          {
                              AgentId = string.Empty,
                              Limit = 10
                          },
                          new GapQuery
                          {
                              AgentId = "bad agent!",
                              Limit = 10
                          },
                          new GapQuery
                          {
                              AgentId = AgentId,
                              State = "unknown",
                              Limit = 10
                          },
                          new GapQuery
                          {
                              AgentId = AgentId,
                              AfterId = -1,
                              Limit = 10
                          },
                          new GapQuery
                          {
                              AgentId = AgentId,
                              Limit = 0
                          },
                          new GapQuery
                          {
                              AgentId = AgentId,
                              Limit = 1001
                          }
                      };

        // Act
        var accepted = new List<IReadOnlyList<AgentGap>>
                       {
                           await store.GapsAsync(new GapQuery
                                                 {
                                                     AgentId = AgentId,
                                                     Limit = 1
                                                 },
                                                 TestContext.CancellationToken),
                           await store.GapsAsync(new GapQuery
                                                 {
                                                     AgentId = AgentId,
                                                     Limit = 1000,
                                                     State = GapState.Closed
                                                 },
                                                 TestContext.CancellationToken)
                       };

        // Assert
        Assert.HasCount(2, accepted, "the limits 1 and 1000 are accepted");

        foreach (var query in queries)
        {
            await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.GapsAsync(query, TestContext.CancellationToken), $"agent '{query.AgentId}' state '{query.State}' after {query.AfterId} limit {query.Limit}");
        }
    }

    #endregion // Methods

    #region Helpers

    /// <summary>
    /// Gets the database file of a test.
    /// </summary>
    /// <param name="directory">The directory of the database</param>
    /// <returns>The path</returns>
    private static string DbPath(TempDirectory directory)
    {
        return Path.Combine(directory.Path, StorageLimits.FileName);
    }

    /// <summary>
    /// Builds the batch of one sample period: a <c>system</c> metric and, optionally, a <c>self</c> sample, captured at the same time and received 3 seconds later.
    /// </summary>
    /// <param name="firstSeq">The number of the first record</param>
    /// <param name="trueTime">The backend host's time of the capture</param>
    /// <param name="skew">The agent's clock minus the backend host's clock</param>
    /// <param name="lagSeconds">The send lag of the sample</param>
    /// <param name="withSample"><see langword="true"/> to add the <c>self</c> sample</param>
    /// <returns>The batch</returns>
    private static RecordBatch SampleBatch(ulong firstSeq, DateTimeOffset trueTime, TimeSpan skew, double lagSeconds, bool withSample)
    {
        var stamp = trueTime + skew;
        var records = withSample ? new[] { TimelineSamples.Metric(firstSeq, stamp), TimelineSamples.SelfSample(firstSeq + 1, stamp, lagSeconds) } : new[] { TimelineSamples.Metric(firstSeq, stamp) };

        return TimelineSamples.Batch(trueTime.AddSeconds(3), records);
    }

    /// <summary>
    /// Tells whether a batch carries a counted <c>self</c> sample: a send lag of at most 60 seconds.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <returns><see langword="true"/> when it carries one</returns>
    private static bool HasCountedSample(ReplayBatch batch)
    {
        return batch.Batch.Records.Any(record => record.Data is MetricPoint { Name: "agent_send_lag_seconds" } point && point.Value <= TimelineLimits.MaxCountedSendLagSeconds);
    }

    /// <summary>
    /// Builds a query that finds the records of a kind written around the test's base time.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The query</returns>
    private static RecordQuery MetricsAround(string kind)
    {
        return new RecordQuery
               {
                   Kind = kind,
                   From = _t0.AddHours(-1),
                   To = _t0.AddHours(1),
                   Limit = 100
               };
    }

    /// <summary>
    /// Moves a batch to the boot after a reboot.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <returns>The batch with the new boot ID</returns>
    private static RecordBatch Rebooted(RecordBatch batch)
    {
        batch.BootId = TimelineSamples.OtherBootId;

        return batch;
    }

    /// <summary>
    /// Checks the classification of every record of a batch.
    /// </summary>
    /// <param name="live">Whether each stored number is live</param>
    /// <param name="batch">The batch</param>
    /// <param name="expected">Whether the records are expected to be live</param>
    /// <param name="message">What the records are</param>
    private static void AssertAll(Dictionary<ulong, bool> live, RecordBatch batch, bool expected, string message)
    {
        foreach (var record in batch.Records)
        {
            Assert.AreEqual(expected, live[record.Seq], $"{message}: record {record.Seq}");
        }
    }

    /// <summary>
    /// Opens the store of a test.
    /// </summary>
    /// <param name="directory">The directory of the database</param>
    /// <returns>A task that returns the store</returns>
    private Task<SqliteStore> Open(TempDirectory directory)
    {
        return SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes a batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="batch">The batch</param>
    /// <returns>A task that returns the result</returns>
    private Task<WriteResult> WriteAsync(SqliteStore store, RecordBatch batch)
    {
        return store.WriteBatchAsync(batch, TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes batches in order.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="batches">The batches</param>
    /// <returns>A task that completes when all are written</returns>
    private async Task WriteAllAsync(SqliteStore store, IEnumerable<ReplayBatch> batches)
    {
        foreach (var batch in batches)
        {
            await WriteAsync(store, batch.Batch);
        }
    }

    /// <summary>
    /// Reads the gaps of an agent.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="state">The state, or an empty text for every one</param>
    /// <param name="agent">The agent ID</param>
    /// <returns>A task that returns the gaps</returns>
    private Task<List<AgentGap>> GapsAsync(SqliteStore store, string state = "", string agent = AgentId)
    {
        return TimelineSamples.GapsOfAsync(store, agent, state, TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads the classification of the records of an agent.
    /// </summary>
    /// <param name="directory">The directory of the database</param>
    /// <param name="agent">The agent ID</param>
    /// <returns>A task that returns whether each number is live</returns>
    private Task<Dictionary<ulong, bool>> LiveAsync(TempDirectory directory, string agent = AgentId)
    {
        return TimelineSamples.LiveOfAsync(DbPath(directory), agent, TestContext.CancellationToken);
    }

    /// <summary>
    /// Describes what a refused batch must not change.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="directory">The directory of the database</param>
    /// <returns>A task that returns the description</returns>
    private async Task<string> SnapshotAsync(SqliteStore store, TempDirectory directory)
    {
        var gaps = TimelineSamples.Fingerprint(await GapsAsync(store));
        var clock = await store.ClockAsync(AgentId, TestContext.CancellationToken);
        var records = await TimelineSamples.CountAsync(DbPath(directory), "SELECT count(*) FROM records", TestContext.CancellationToken);
        var live = await TimelineSamples.CountAsync(DbPath(directory), "SELECT count(*) FROM records WHERE live = 1", TestContext.CancellationToken);

        return $"{gaps}#{clock?.OffsetNs}#{clock?.MeasuredAt?.UtcTicks}#{records}#{live}";
    }

    #endregion // Helpers
}