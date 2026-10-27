using System.Globalization;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestEndpoint"/>: the classification of stored records and the log lines about gaps and the clock of an agent.
/// </summary>
[TestClass]
public class IngestEndpointTimelineTests
{
    #region Constants

    private const string GapsChanged = "ingest gaps changed";

    private const string ClockOff = "agent clock off";

    private const string ClockInStep = "agent clock in step";

    private const string StorableMinimum = "1677-09-21T00:12:43.1452242Z";

    private const string StorableMaximum = "2262-04-11T23:47:16.8547758Z";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Fresh records in a batch whose header says backfill are stored live: the classification follows the data, not the header.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineBackfillModeFreshRecordsStoredLive()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        var status = await PostAsync(rig, _now, "backfill", Metric(1, _now.AddSeconds(-2)), Metric(2, _now.AddSeconds(-1)));

        // Assert
        Assert.AreEqual(204, status, "status");
        Assert.AreEqual(2L, await rig.ScalarAsync("SELECT count(*) FROM records WHERE live = 1", TestContext.CancellationToken), "both fresh records are live whatever the header says");
    }

    /// <summary>
    /// Old records in a batch whose header says live are stored backfilled, a fresh one beside them live.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineLiveModeOldRecordsStoredBackfilled()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        var status = await PostAsync(rig, _now, "live", Metric(1, _now.AddHours(-1)), Metric(2, _now.AddSeconds(-1)));

        // Assert
        Assert.AreEqual(204, status, "status");
        Assert.AreEqual(0L, await rig.ScalarAsync("SELECT live FROM records WHERE seq = 1", TestContext.CancellationToken), "the record from an hour ago is backfilled");
        Assert.AreEqual(1L, await rig.ScalarAsync("SELECT live FROM records WHERE seq = 2", TestContext.CancellationToken), "the fresh record is live");
    }

    /// <summary>
    /// Capture times at both ends of the storable range are stored and answered 204, as backfilled records.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineCaptureTimeAtStorableEndAnswers204()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        var status = await PostAsync(rig, _now, "live", IngestSamples.Metric(1, capturedAt: StorableMinimum), IngestSamples.Metric(2, capturedAt: StorableMaximum), Metric(3, _now.AddSeconds(-1)));

        // Assert
        Assert.AreEqual(204, status, "status");
        Assert.AreEqual(3L, await rig.ScalarAsync("SELECT count(*) FROM records", TestContext.CancellationToken), "every record is stored");
        Assert.AreEqual(0L, await rig.ScalarAsync("SELECT count(*) FROM records WHERE seq IN (1, 2) AND live = 1", TestContext.CancellationToken), "the records at the ends of the range are backfilled");
        Assert.AreEqual(0L, await rig.ScalarAsync("SELECT count(*) FROM records WHERE live NOT IN (0, 1)", TestContext.CancellationToken), "the classification column is readable");
    }

    /// <summary>
    /// A batch that changes gaps is logged once with the numbers of newly detected, closed and final gaps and the open sequence gaps; a batch that changes none is not logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineGapsChangedLoggedOncePerBatch()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        await PostAsync(rig, _now, "live", Metric(1, _now.AddSeconds(-3)), Metric(2, _now.AddSeconds(-2)), Metric(3, _now.AddSeconds(-1)));

        var afterFirst = rig.Log.Named(GapsChanged).Count;
        var opening = _now.AddMinutes(5).AddSeconds(2);

        await PostAsync(rig, opening, "live", Metric(10, opening.AddSeconds(-2)), Metric(11, opening.AddSeconds(-1)));

        var afterJump = rig.Log.Named(GapsChanged).ToList();

        await PostAsync(rig, opening.AddSeconds(8), "backfill", Metric(4, _now.AddSeconds(-10)), Metric(5, _now.AddSeconds(-9)), Metric(6, _now.AddSeconds(-8)), Metric(7, _now.AddSeconds(-7)), Metric(8, _now.AddSeconds(-6)), Metric(9, _now.AddSeconds(-5)));

        var afterFill = rig.Log.Named(GapsChanged).ToList();

        await PostAsync(rig, opening.AddSeconds(12), "live", Metric(12, opening.AddSeconds(11)));

        var afterQuiet = rig.Log.Named(GapsChanged).ToList();

        // Assert
        Assert.AreEqual(0, afterFirst, "the first batch of an agent changes no gap");
        Assert.HasCount(1, afterJump, "the jump is logged once for the batch");
        Assert.AreEqual(LogLevel.Information, afterJump[0].Level, "level of the line");
        Assert.AreEqual(IngestSamples.AgentId, afterJump[0].Value("AgentId"), "agent of the line");
        Assert.AreEqual("2", afterJump[0].Value("Opened"), "the sequence gap and the time gap are newly detected");
        Assert.AreEqual("0", afterJump[0].Value("Closed"), "nothing closed");
        Assert.AreEqual("0", afterJump[0].Value("Final"), "nothing final: an open sequence gap lies between the neighbours of the time gap");
        Assert.AreEqual("1", afterJump[0].Value("Open"), "one open sequence gap");
        Assert.AreSequenceEqual(["AgentId", "Closed", "Final", "Open", "Opened"], afterJump[0].Values.Keys.Order(StringComparer.Ordinal).ToArray(), "the line carries the agent ID and counts only");
        Assert.HasCount(2, afterFill, "the fill is logged once more");
        Assert.AreEqual("0", afterFill[1].Value("Opened"), "the fill detects no new gap");
        Assert.AreEqual("1", afterFill[1].Value("Closed"), "the sequence gap is closed");
        Assert.AreEqual("0", afterFill[1].Value("Final"), "nothing final");
        Assert.AreEqual("0", afterFill[1].Value("Open"), "no open sequence gap is left");
        Assert.HasCount(2, afterQuiet, "a batch that changes no gap is not logged");
    }

    /// <summary>
    /// A batch that only shrinks an open gap, without closing or detecting any, writes no gaps line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineBatchThatOnlyShrinksGapLogsNoGapsLine()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        await PostAsync(rig, _now, "live", Metric(1, _now.AddSeconds(-3)), Metric(2, _now.AddSeconds(-2)), Metric(3, _now.AddSeconds(-1)));

        var opening = _now.AddMinutes(5).AddSeconds(2);

        await PostAsync(rig, opening, "live", Metric(10, opening.AddSeconds(-2)), Metric(11, opening.AddSeconds(-1)));

        var afterJump = rig.Log.Named(GapsChanged).Count;

        // Act
        var status = await PostAsync(rig, opening.AddSeconds(8), "backfill", Metric(4, _now.AddSeconds(-10)), Metric(5, _now.AddSeconds(-9)));

        var afterShrink = rig.Log.Named(GapsChanged).Count;

        // Assert
        Assert.AreEqual(204, status, "status");
        Assert.AreEqual(1, afterJump, "the jump is logged");
        Assert.AreEqual(afterJump, afterShrink, "the batch that only shrinks the gap writes no line");
        Assert.AreEqual(2L, await rig.ScalarAsync("SELECT count(*) FROM gaps WHERE state = 'open'", TestContext.CancellationToken), "the gaps stay open, the sequence gap shrunk");
    }

    /// <summary>
    /// The warning that the clock of an agent is off is logged once when the measured offset first exceeds 90 seconds, and the info that it is in step again once when it returns.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointTimelineClockOffLoggedOnceThenInStep()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        await PostAsync(rig, _now, "live", SelfSample(1, _now.AddSeconds(-1)));

        var beforeStep = rig.Log.Named(ClockOff).Count + rig.Log.Named(ClockInStep).Count;
        var second = _now.AddMinutes(1);

        await PostAsync(rig, second, "live", SelfSample(2, second.AddHours(2).AddSeconds(-1)));

        var off = rig.Log.Named(ClockOff).ToList();

        await PostAsync(rig, _now.AddMinutes(2), "live", SelfSample(3, _now.AddMinutes(2).AddHours(2).AddSeconds(-1)));

        var stillOff = rig.Log.Named(ClockOff).Count;
        var back = _now.AddMinutes(8);

        await PostAsync(rig, back, "live", SelfSample(4, back.AddSeconds(-1)));

        var inStepBefore = rig.Log.Named(ClockInStep).Count;

        await PostAsync(rig, back.AddMinutes(1), "live", SelfSample(5, back.AddMinutes(1).AddSeconds(-1)));

        var inStep = rig.Log.Named(ClockInStep).ToList();

        // Assert
        Assert.AreEqual(0, beforeStep, "a clock in step is not logged");
        Assert.HasCount(1, off, "the offset exceeding 90 seconds is logged");
        Assert.AreEqual(LogLevel.Warning, off[0].Level, "level of the warning");
        Assert.AreEqual(IngestSamples.AgentId, off[0].Value("AgentId"), "agent of the warning");
        Assert.AreEqual(7199d, double.Parse(off[0].Value("OffsetSeconds")!, CultureInfo.InvariantCulture), "the measured offset in seconds");
        Assert.AreSequenceEqual(["AgentId", "OffsetSeconds"], off[0].Values.Keys.Order(StringComparer.Ordinal).ToArray(), "the warning carries the agent ID and the offset only");
        Assert.AreEqual(1, stillOff, "an offset that stays off is logged once");
        Assert.AreEqual(0, inStepBefore, "the first sample in step is far below the offset in effect and measures nothing alone");
        Assert.HasCount(1, inStep, "the second sample in step, one minute later, confirms the first and the return is logged once");
        Assert.AreEqual(LogLevel.Information, inStep[0].Level, "level of the info");
        Assert.AreEqual(IngestSamples.AgentId, inStep[0].Value("AgentId"), "agent of the info");
        Assert.AreSequenceEqual(["AgentId"], inStep[0].Values.Keys.ToArray(), "the info carries the agent ID only");
        Assert.HasCount(1, rig.Log.Named(ClockOff), "no further warning");
        Assert.HasCount(1, rig.Log.Named(ClockInStep), "no further info");
    }

    /// <summary>
    /// Posts a batch received at a time.
    /// </summary>
    /// <param name="rig">The rig</param>
    /// <param name="receivedAt">The receive time</param>
    /// <param name="mode">The header's mode</param>
    /// <param name="records">The record lines</param>
    /// <returns>A task that returns the status</returns>
    private static async Task<int> PostAsync(IngestRig rig, DateTimeOffset receivedAt, string mode, params string[] records)
    {
        rig.Clock.SetUtcNow(receivedAt);

        using var call = IngestCall.Post(IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(mode: mode), .. records])));

        await rig.RunAsync(call);

        return call.Status;
    }

    /// <summary>
    /// Builds a metric line.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The line</returns>
    private static string Metric(long seq, DateTimeOffset capturedAt)
    {
        return IngestSamples.Metric(seq, capturedAt: capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds a self sample line that reports a send lag of 10 seconds.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The line</returns>
    private static string SelfSample(long seq, DateTimeOffset capturedAt)
    {
        var stamp = capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        return $$$"""{"kind":"metric","source":"agent","seq":{{{seq}}},"captured_at":"{{{stamp}}}","data":{"name":"agent_send_lag_seconds","value":10,"unit":"seconds"}}""";
    }

    #endregion // Methods
}