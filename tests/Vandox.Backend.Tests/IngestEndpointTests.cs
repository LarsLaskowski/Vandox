using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Ingest;
using Vandox.Core.Wire;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestEndpoint"/>: the answers, the checks in their order, what is stored and what is logged.
/// </summary>
[TestClass]
public class IngestEndpointTests
{
    #region Constants

    private const string Refusal = "batch";

    private const string TokenRefused = "ingest token refused";

    private const string AgentRefused = "ingest agent refused";

    private const string BatchRefused = "ingest batch refused";

    private const string NotAccepted = "ingest request not accepted";

    private const string StoreFailed = "ingest store failed";

    private const string RequestFailed = "ingest request failed";

    private const string Paused = "ingest paused: free space low";

    private const string Resumed = "ingest resumed";

    private const string FirstStored = "ingest first batch stored";

    private const string BatchStored = "ingest batch stored";

    private const string DisabledText = "ingest disabled";

    private const string Authorization = "Authorization";

    private const string ContentType = "Content-Type";

    private const string GzipType = "application/gzip";

    #endregion // Constants

    #region Fields

    private static readonly string[] _modes = ["live", "backfill", "live"];

    private static readonly string[] _recordCounts = ["3", "5", "2"];

    private static readonly string[] _storedCounts = ["2", "4", "1"];

    private static readonly string[] _duplicateCounts = ["1", "1", "1"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A valid batch is answered 204 without a body only after the write returned, and the writer got the batch with the receive time of the arrival.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointValidBatchAnswers204AfterCommit()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var call = IngestCall.Post(IngestSamples.Valid(1, 3));

        var arrival = rig.Clock.GetUtcNow();

        // Act
        var run = rig.RunAsync(call);

        await rig.Writer.WaitForCallsAsync(1, TestContext.CancellationToken, run);
        await IngestRig.SettleAsync();

        var finishedBeforeCommit = run.IsCompleted;

        rig.Writer.ReleaseAll();
        await Finish(run);

        // Assert
        Assert.IsFalse(finishedBeforeCommit, "the request is not answered while the write is open");
        Assert.AreEqual(204, call.Status, "status");
        Assert.AreEqual(0, call.AnswerBytes, "no body");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.HasCount(1, rig.Writer.Batches, "one write");
        Assert.HasCount(3, rig.Writer.Batches[0].Records, "records of the batch");
        Assert.AreEqual(IngestSamples.AgentId, rig.Writer.Batches[0].AgentId, "agent ID of the batch");
        Assert.AreEqual(IngestSamples.BootId, rig.Writer.Batches[0].BootId, "boot ID of the batch");
        Assert.AreEqual(arrival, rig.Writer.Batches[0].ReceivedAt, "receive time");
    }

    /// <summary>
    /// A real store holds every record of the golden batch with origin agent, the batch's agent ID, boot ID and clock offset, its own sequence number, and one
    /// receive time that is the time of the arrival, not of the commit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointStoresReceiveTimeAndContextForEveryRecord()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        var lines = GoldenLines(IngestSamples.Header(clockOffsetNs: -1500, mode: "backfill"));

        rig.Writer.Hold();

        using var call = IngestCall.Post(IngestSamples.Gzip(IngestSamples.Plain(lines)));

        var arrival = IngestRig.Nanoseconds(rig.Clock.GetUtcNow());

        // Act
        var run = rig.RunAsync(call);

        await rig.Writer.WaitForCallsAsync(1, TestContext.CancellationToken, run);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Writer.ReleaseAll();
        await Finish(run);

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, call.Status, "status");
        Assert.HasCount(lines.Length - 1, rows, "every record is stored");
        Assert.IsTrue(rows.All(row => row.Origin == "agent"), "origin of every record");
        Assert.IsTrue(rows.All(row => row.AgentId == IngestSamples.AgentId), "agent ID of every record");
        Assert.IsTrue(rows.All(row => row.BootId == IngestSamples.BootId), "boot ID of every record");
        Assert.IsTrue(rows.All(row => row.ClockOffsetNs == -1500), "clock offset of every record");
        Assert.IsTrue(rows.All(row => row.ReceivedAt == arrival), "one receive time, the time of the arrival");
        Assert.AreSequenceEqual(Enumerable.Range(1, lines.Length - 1).Select(seq => (long)seq).ToArray(), rows.Select(row => row.Seq).ToArray(), "sequence numbers");
    }

    /// <summary>
    /// A batch that was stored already is answered 204 again and changes no row, the receive time included.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointResentBatchStoresNothingNew()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        var first = IngestRig.Nanoseconds(rig.Clock.GetUtcNow());
        var body = IngestSamples.Valid(1, 5);

        using var original = await rig.PostAsync(body);

        rig.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        using var resent = await rig.PostAsync(body);

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, original.Status, "status of the first post");
        Assert.AreEqual(204, resent.Status, "status of the resent batch");
        Assert.HasCount(5, rows, "each record is stored once");
        Assert.IsTrue(rows.All(row => row.ReceivedAt == first), "the receive time of the first post stays");
    }

    /// <summary>
    /// A batch that overlaps the stored records stores only its new records and leaves the stored rows as they were.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointOverlappingBatchStoresOnlyNewRecords()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        var first = IngestRig.Nanoseconds(rig.Clock.GetUtcNow());

        using var original = await rig.PostAsync(IngestSamples.Valid(1, 10));

        rig.Clock.Advance(TimeSpan.FromHours(1));

        var second = IngestRig.Nanoseconds(rig.Clock.GetUtcNow());

        // Act
        using var overlapping = await rig.PostAsync(IngestSamples.Valid(8, 5));

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, overlapping.Status, "status");
        Assert.AreSequenceEqual(Enumerable.Range(1, 12).Select(seq => (long)seq).ToArray(), rows.Select(row => row.Seq).ToArray(), "each sequence number once");
        Assert.IsTrue(rows.Where(row => row.Seq <= 10).All(row => row.ReceivedAt == first), "the stored rows keep their receive time");
        Assert.IsTrue(rows.Where(row => row.Seq > 10).All(row => row.ReceivedAt == second), "the new rows carry the new receive time");
    }

    /// <summary>
    /// Two requests with overlapping records at the same time are both answered 204 and store each record once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointConcurrentOverlappingBatchesStoreEachRecordOnce()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        using var live = IngestCall.Post(IngestSamples.Valid(1, 6));
        using var backfill = IngestCall.Post(IngestSamples.Valid(4, 6, mode: "backfill"));

        // Act
        await Finish(Task.WhenAll(Task.Run(() => rig.RunAsync(live), TestContext.CancellationToken), Task.Run(() => rig.RunAsync(backfill), TestContext.CancellationToken)));

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, live.Status, "status of the first request");
        Assert.AreEqual(204, backfill.Status, "status of the second request");
        Assert.AreSequenceEqual(Enumerable.Range(1, 9).Select(seq => (long)seq).ToArray(), rows.Select(row => row.Seq).ToArray(), "each sequence number once");
    }

    /// <summary>
    /// A sequence number that is stored already with other content is counted as a duplicate and the stored record is not replaced.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointStoredRecordWithOtherContentIsKept()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        using var original = await rig.PostAsync(IngestSamples.Valid(1, 3));

        var other = IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), IngestSamples.Metric(2, value: 99), IngestSamples.Metric(4, value: 4)]));

        // Act
        using var hostile = await rig.PostAsync(other);

        var kept = await rig.ScalarAsync("SELECT m.value FROM metrics m JOIN records r ON r.id = m.record_id WHERE r.seq = 2", TestContext.CancellationToken);
        var added = await rig.ScalarAsync("SELECT m.value FROM metrics m JOIN records r ON r.id = m.record_id WHERE r.seq = 4", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, hostile.Status, "status");
        Assert.AreEqual(1.5, kept, "the stored value is not replaced");
        Assert.AreEqual(4.0, added, "the new record is stored");
    }

    /// <summary>
    /// Every request that fails the token check is answered 401 with the bearer challenge and no refusal header, and not a byte of the body is read.
    /// </summary>
    /// <param name="form">The form of the request</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no header")]
    [DataRow("wrong token")]
    [DataRow("basic scheme")]
    [DataRow("two headers")]
    [DataRow("token in the query only")]
    [DataRow("wrong token with an oversized length")]
    [DataRow("wrong token with a content encoding")]
    public async Task IngestEndpointRefusedTokenReadsNoBody(string form)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var call = IngestCall.Post(IngestSamples.Valid(), null);

        switch (form)
        {
            case "wrong token":
                {
                    call.With(Authorization, "Bearer wrong-token-0123456789-abcdefghijklmnop");
                }
                break;

            case "basic scheme":
                {
                    call.With(Authorization, $"Basic {IngestRig.Token}");
                }
                break;

            case "two headers":
                {
                    call.With(Authorization, $"Bearer {IngestRig.Token}", $"Bearer {IngestRig.Token}");
                }
                break;

            case "token in the query only":
                {
                    call.Context.Request.QueryString = new QueryString($"?token={IngestRig.Token}");
                }
                break;

            case "wrong token with an oversized length":
                {
                    call.With(Authorization, "Bearer wrong").WithLength(IngestLimits.MaxBodyBytes + 1L);
                }
                break;

            case "wrong token with a content encoding":
                {
                    call.With(Authorization, "Bearer wrong").With("Content-Encoding", "gzip");
                }
                break;

            default:
                break;
        }

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        Assert.AreEqual(401, call.Status, "status");
        Assert.AreEqual("Bearer", call.Header("WWW-Authenticate"), "challenge");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(0, call.Body.ReadCalls, "the body is not read");
        Assert.AreEqual(0, call.AnswerBytes, "no body in the answer");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
    }

    /// <summary>
    /// A refused token logs the peer and a fixed reason and neither the presented value nor the header.
    /// </summary>
    /// <param name="presented">The presented token, <c>null</c> for no header</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("SENTINEL-WRONG-TOKEN-0123456789-abcdef", "wrong")]
    [DataRow((string?)null, "missing")]
    public async Task IngestEndpointRefusedTokenLogsPeerAndReasonOnly(string? presented, string reason)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var call = IngestCall.Post(IngestSamples.Valid(), presented);

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        var lines = rig.Log.Named(TokenRefused);

        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual(LogLevel.Warning, lines[0].Level, "level");
        Assert.AreEqual(IngestCall.Peer, lines[0].Value("Peer"), "peer");
        Assert.AreEqual(reason, lines[0].Value("Reason"), "reason");
        Assert.AreEqual("0", lines[0].Value("Suppressed"), "nothing suppressed yet");
        Assert.DoesNotContain("SENTINEL", string.Join('\n', rig.Log.AllTexts()), "the presented value is not logged");
        Assert.DoesNotContain("Bearer", string.Join('\n', rig.Log.AllTexts()), "the header is not logged");
    }

    /// <summary>
    /// A batch of another agent is answered 403 without a refusal header, even when the rest of the body is garbage, and it stores nothing; the line is
    /// rate-limited.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointOtherAgentIdAnswers403()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        var body = IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header("agent-2"), "{not json"]));

        // Act
        using var first = await rig.PostAsync(body);
        using var second = await rig.PostAsync(IngestSamples.Valid(1, 2, "agent-2"));

        rig.Clock.Advance(TimeSpan.FromSeconds(61));

        using var third = await rig.PostAsync(IngestSamples.Valid(1, 2, "agent-2"));

        // Assert
        var lines = rig.Log.Named(AgentRefused);

        Assert.AreEqual(403, first.Status, "status with a garbage body");
        Assert.AreEqual(403, second.Status, "status with a valid body");
        Assert.AreEqual(403, third.Status, "status after a quiet minute");
        Assert.IsNull(first.Refused, "no refusal header");
        Assert.AreEqual(0, first.AnswerBytes, "no body in the answer");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
        Assert.HasCount(2, lines, "one line a minute");
        Assert.AreEqual(LogLevel.Warning, lines[0].Level, "level");
        Assert.AreEqual("agent-2", lines[0].Value("AgentId"), "presented agent ID");
        Assert.AreEqual(IngestCall.Peer, lines[0].Value("Peer"), "peer");
        Assert.AreEqual("0", lines[0].Value("Suppressed"), "nothing suppressed in the first line");
        Assert.AreEqual("1", lines[1].Value("Suppressed"), "the refusal in between is counted");
    }

    /// <summary>
    /// The agent ID of the batch is compared ordinally with the configured one.
    /// </summary>
    /// <param name="presented">The agent ID of the header</param>
    /// <param name="status">The expected status</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("agent-1", 204)]
    [DataRow("AGENT-1", 403)]
    [DataRow("Agent-1", 403)]
    [DataRow("agent-10", 403)]
    [DataRow("agent-", 403)]
    public async Task IngestEndpointAgentIdIsComparedOrdinally(string presented, int status)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        // Act
        using var call = await rig.PostAsync(IngestSamples.Valid(1, 2, presented));

        // Assert
        Assert.AreEqual(status, call.Status, "status");
    }

    /// <summary>
    /// Without the token or the agent ID every post is answered 503 before the request is looked at.
    /// </summary>
    /// <param name="presented">The token of the request, <c>null</c> for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("wrong")]
    [DataRow(IngestRig.Token)]
    public async Task IngestEndpointDisabledAnswers503(string? presented)
    {
        // Arrange
        await using var rig = IngestRig.Create(false);

        using var call = IngestCall.Post(IngestSamples.Valid(), presented);

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        Assert.AreEqual(503, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(0, call.Body.ReadCalls, "the body is not read");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
        Assert.HasCount(1, rig.Log.Named(DisabledText), "disabled is logged once, at the start");
    }

    /// <summary>
    /// A Content-Length over 2 MiB is refused 413 before the body is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointContentLengthOverLimitAnswers413BeforeReading()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var oversized = IngestCall.Post(IngestSamples.Valid());

        oversized.WithLength(IngestLimits.MaxBodyBytes + 1L);

        // Act
        await Finish(rig.RunAsync(oversized));

        // Assert
        var lines = rig.Log.Named(BatchRefused);

        Assert.AreEqual(413, oversized.Status, "status");
        Assert.AreEqual(Refusal, oversized.Refused, "refusal header");
        Assert.AreEqual(0, oversized.Body.ReadCalls, "the body is not read");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("413", lines[0].Value("Status"), "status in the log");
        Assert.AreEqual("body too large", lines[0].Value("Reason"), "reason");
    }

    /// <summary>
    /// A body without a length that grows past 2 MiB is refused 413 and read only up to the limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointChunkedBodyOverLimitAnswers413()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var call = IngestCall.Chunked(CountingBodyStream.Endless());

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        Assert.AreEqual(413, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.IsLessThanOrEqualTo(IngestLimits.MaxBodyBytes + 65536L, call.Body.BytesRead, "reading stops at the limit");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
    }

    /// <summary>
    /// A batch over a limit after the decompression is refused 413 and stores nothing.
    /// </summary>
    /// <param name="description">The body</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("decompressed over 2 MiB")]
    [DataRow("20001 records")]
    [DataRow("line over 1 MiB")]
    [DataRow("header line over 1 MiB")]
    [DataRow("1 KB body to a hostile line")]
    public async Task IngestEndpointOversizedBatchAnswers413(string description)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build(description));

        // Assert
        Assert.AreEqual(413, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.AreEqual(0, call.AnswerBytes, "no body in the answer");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
    }

    /// <summary>
    /// A body that is not one readable batch is refused 400 and stores nothing.
    /// </summary>
    /// <param name="description">The body</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("not gzip")]
    [DataRow("empty")]
    [DataRow("truncated")]
    [DataRow("bad CRC")]
    [DataRow("trailing bytes")]
    [DataRow("bad JSON")]
    [DataRow("key rule")]
    [DataRow("unreadable header line")]
    public async Task IngestEndpointUnreadableBodyAnswers400Refused(string description)
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build(description));

        // Assert
        var lines = rig.Log.Named(BatchRefused);

        Assert.AreEqual(400, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.AreEqual(0, call.AnswerBytes, "no body in the answer");
        Assert.IsEmpty(await rig.RowsAsync(TestContext.CancellationToken), "nothing is stored");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("400", lines[0].Value("Status"), "status in the log");
    }

    /// <summary>
    /// A batch that reads but breaks a rule is refused 422 and stores nothing.
    /// </summary>
    /// <param name="description">The body</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("field error")]
    [DataRow("unknown kind")]
    [DataRow("no records")]
    [DataRow("sequence")]
    [DataRow("invalid header")]
    public async Task IngestEndpointInvalidBatchAnswers422Refused(string description)
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build(description));

        // Assert
        var lines = rig.Log.Named(BatchRefused);

        Assert.AreEqual(422, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.AreEqual(0, call.AnswerBytes, "no body in the answer");
        Assert.IsEmpty(await rig.RowsAsync(TestContext.CancellationToken), "nothing is stored");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("422", lines[0].Value("Status"), "status in the log");
    }

    /// <summary>
    /// A record the decoder accepts but the store refuses, here a capture time outside the storable range, is refused 422 as refused by the store.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointRecordTheStoreRefusesAnswers422()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build("capture time outside the storable range"));

        // Assert
        var lines = rig.Log.Named(BatchRefused);

        Assert.AreEqual(422, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.IsEmpty(await rig.RowsAsync(TestContext.CancellationToken), "nothing is stored");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("refused by the store", lines[0].Value("Reason"), "reason");
    }

    /// <summary>
    /// An error after records that decoded well stores nothing: the answer is 400 or 422 and the store stays empty.
    /// </summary>
    /// <param name="description">The body</param>
    /// <param name="status">The expected status</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("bad JSON after records", 400)]
    [DataRow("bad CRC after records", 400)]
    [DataRow("field error after records", 422)]
    [DataRow("sequence after records", 422)]
    public async Task IngestEndpointErrorAfterGoodRecordsStoresNothing(string description, int status)
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build(description));

        // Assert
        Assert.AreEqual(status, call.Status, "status");
        Assert.AreEqual(Refusal, call.Refused, "refusal header");
        Assert.IsEmpty(await rig.RowsAsync(TestContext.CancellationToken), "nothing is committed before the decoder reached the end");
    }

    /// <summary>
    /// A newer minor of format 1 with unknown keys is stored and the unknown keys are dropped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointNewerMinorStoresWithUnknownKeysDropped()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        var lines = GoldenLines(IngestSamples.Header(minor: 99, extra: "\"future_header\":1,"));

        lines[1] = lines[1].Replace("\"data\":{", "\"data\":{\"future_data\":2,", StringComparison.Ordinal).Insert(1, "\"future_record\":3,");
        lines[2] = lines[2].Replace("\"data\":{", "\"data\":{\"future_data\":2,", StringComparison.Ordinal).Insert(1, "\"future_record\":3,");

        // Act
        using var call = await rig.PostAsync(IngestSamples.Gzip(IngestSamples.Plain(lines)));

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(204, call.Status, "status");
        Assert.HasCount(lines.Length - 1, rows, "every record is stored");
        Assert.DoesNotContain("future", string.Join('\n', rows.Select(row => row.Data ?? string.Empty)), "no unknown key is stored");
    }

    /// <summary>
    /// A refused batch logs the agent ID, the status, a fixed reason, the line and the field path with the map key reduced, and nothing of the batch.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointRefusedBatchLogsFixedReasonOnly()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        var body = IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), IngestSamples.Metric(1, "SENTINEL-SOURCE", labels: """{"SENTINEL MAP KEY":"SENTINEL-VALUE"}""")]));

        // Act
        using var call = await rig.PostAsync(body);

        // Assert
        var lines = rig.Log.Named(BatchRefused);

        Assert.AreEqual(422, call.Status, "status");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual(LogLevel.Warning, lines[0].Level, "level");
        Assert.AreEqual(IngestSamples.AgentId, lines[0].Value("AgentId"), "agent ID");
        Assert.AreEqual("422", lines[0].Value("Status"), "status in the log");
        Assert.AreEqual(IngestStatus.Reason(WireErrorKind.Invalid), lines[0].Value("Reason"), "the fixed reason of the error kind");
        Assert.AreEqual("2", lines[0].Value("Line"), "line");
        Assert.AreEqual("data.labels[]", lines[0].Value("Field"), "field path with the map key reduced");
        Assert.DoesNotContain("SENTINEL", string.Join('\n', rig.Log.AllTexts()), "nothing of the batch is logged");
        Assert.DoesNotContain("model:", string.Join('\n', rig.Log.AllTexts()), "no parser text is logged");
    }

    /// <summary>
    /// A header with an unsupported major is answered 415 without a refusal header, and it is logged with its status and reason.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointUnsupportedMajorAnswers415()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        // Act
        using var call = await rig.PostAsync(IngestBodies.Build("unsupported major"));

        // Assert
        var lines = rig.Log.Named(NotAccepted);

        Assert.AreEqual(415, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("415", lines[0].Value("Status"), "status in the log");
        Assert.AreEqual("format version", lines[0].Value("Reason"), "reason");
    }

    /// <summary>
    /// Only one Content-Type of exactly application/gzip, ignoring case, is accepted; every other form is answered 415 without reading the body.
    /// </summary>
    /// <param name="form">The Content-Type header, <c>&lt;missing&gt;</c> for none, <c>&lt;two&gt;</c> for two headers</param>
    /// <param name="accepted">Whether the form is accepted</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Application/GZIP", true)]
    [DataRow("application/gzip; charset=x", false)]
    [DataRow("application/x-gzip", false)]
    [DataRow("", false)]
    [DataRow("<missing>", false)]
    [DataRow("<two>", false)]
    [DataRow("application/gzip ", false)]
    [DataRow(" application/gzip", false)]
    public async Task IngestEndpointContentTypeForms(string form, bool accepted)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var call = IngestCall.Post(IngestSamples.Valid());

        switch (form)
        {
            case "<missing>":
                {
                    call.With(ContentType);
                }
                break;

            case "<two>":
                {
                    call.With(ContentType, GzipType, GzipType);
                }
                break;

            default:
                {
                    call.With(ContentType, form);
                }
                break;
        }

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        Assert.AreEqual(accepted ? 204 : 415, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");

        if (accepted)
        {
            Assert.AreEqual(1, rig.Writer.Calls, "the batch is written");
        }
        else
        {
            var lines = rig.Log.Named(NotAccepted);

            Assert.AreEqual(0, call.Body.ReadCalls, "the body is not read");
            Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
            Assert.HasCount(1, lines, "one line");
            Assert.AreEqual("415", lines[0].Value("Status"), "status in the log");
            Assert.AreEqual("content type", lines[0].Value("Reason"), "reason");
        }
    }

    /// <summary>
    /// Any Content-Encoding is answered 415 without reading the body.
    /// </summary>
    /// <param name="encoding">The value of the header</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("gzip")]
    [DataRow("identity")]
    [DataRow("br")]
    [DataRow("")]
    public async Task IngestEndpointContentEncodingAnswers415WithoutReading(string encoding)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        using var call = IngestCall.Post(IngestSamples.Valid());

        call.With("Content-Encoding", encoding);

        // Act
        await Finish(rig.RunAsync(call));

        // Assert
        var lines = rig.Log.Named(NotAccepted);

        Assert.AreEqual(415, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(0, call.Body.ReadCalls, "the body is not read");
        Assert.AreEqual(0, rig.Writer.Calls, "nothing is written");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual("415", lines[0].Value("Status"), "status in the log");
        Assert.AreEqual("content encoding", lines[0].Value("Reason"), "reason");
    }

    /// <summary>
    /// A failure of the store is answered 503 without a refusal header and logged with the agent ID and the type of the exception, never its text.
    /// </summary>
    /// <param name="failure">The type of the scripted exception</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("StoreException")]
    [DataRow("SqliteException")]
    [DataRow("IOException")]
    public async Task IngestEndpointStoreFailureAnswers503AndStoresNothing(string failure)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.FailNext(failure switch
                            {
                                "StoreException" => new StoreException("store: writing batch: SENTINEL-STORE-TEXT"),
                                "SqliteException" => new SqliteException("SENTINEL-SQLITE-TEXT", 13),
                                _ => new IOException("SENTINEL-IO-TEXT")
                            });

        // Act
        using var call = await rig.PostAsync(IngestSamples.Valid());

        // Assert
        var lines = rig.Log.Named(StoreFailed);

        Assert.AreEqual(503, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(0, rig.Writer.Completed, "nothing is stored");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual(LogLevel.Error, lines[0].Level, "level");
        Assert.AreEqual(IngestSamples.AgentId, lines[0].Value("AgentId"), "agent ID");
        Assert.AreEqual(failure, lines[0].Value("Exception"), "type of the exception");
        Assert.DoesNotContain("SENTINEL", string.Join('\n', rig.Log.AllTexts()), "the text of the exception is not logged");
    }

    /// <summary>
    /// A write that waits for a lock another connection holds gives up after about the busy timeout of 5 seconds with 503 and stores nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointStoreLockHeldByAnotherConnectionAnswers503()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        await using var blocker = new SqliteConnection($"Data Source={Path.Combine(rig.DirectoryPath, "vandox.db")};Pooling=False");

        await blocker.OpenAsync(TestContext.CancellationToken);

        await using var begin = blocker.CreateCommand();

        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(TestContext.CancellationToken);

        using var call = IngestCall.Post(IngestSamples.Valid());

        // Act
        try
        {
            await Task.Run(() => rig.RunAsync(call), TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        }
        finally
        {
            await using var rollback = blocker.CreateCommand();

            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        // Assert
        Assert.AreEqual(503, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.HasCount(1, rig.Log.Named(StoreFailed), "the failure is logged");
        Assert.IsEmpty(await rig.RowsAsync(TestContext.CancellationToken), "nothing is stored");
    }

    /// <summary>
    /// The same batch sent again after a failed write is stored and answered 204.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointResendAfterStoreFailureIsStored()
    {
        // Arrange
        await using var rig = await IngestRig.CreateWithStoreAsync(TestContext.CancellationToken);

        var body = IngestSamples.Valid(1, 4);

        rig.Writer.FailNext(new IOException("disk full"));

        // Act
        using var failed = await rig.PostAsync(body);
        using var resent = await rig.PostAsync(body);

        var rows = await rig.RowsAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(503, failed.Status, "status of the failed write");
        Assert.AreEqual(204, resent.Status, "status of the resent batch");
        Assert.HasCount(4, rows, "the resent batch is stored");
    }

    /// <summary>
    /// The first stored batch is logged once at information, every stored batch at debug with the agent ID, the mode and the counts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointLogsFirstBatchOnceAndEveryBatchAtDebug()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Outcome = batch => new WriteResult(batch.Records.Count - 1, 1);

        // Act
        using var first = await rig.PostAsync(IngestSamples.Valid(1, 3));
        using var second = await rig.PostAsync(IngestSamples.Valid(4, 5, mode: "backfill"));
        using var third = await rig.PostAsync(IngestSamples.Valid(9, 2));

        // Assert
        var firstLines = rig.Log.Named(FirstStored);
        var stored = rig.Log.Named(BatchStored);

        Assert.AreEqual(204, third.Status, "status of the last post");
        Assert.HasCount(1, firstLines, "the first batch is logged once");
        Assert.AreEqual(LogLevel.Information, firstLines[0].Level, "level of the first batch");
        Assert.AreEqual(IngestSamples.AgentId, firstLines[0].Value("AgentId"), "agent ID of the first batch");
        Assert.HasCount(3, stored, "every batch is logged");
        Assert.IsTrue(stored.All(line => line.Level == LogLevel.Debug), "level of every batch");
        Assert.AreSequenceEqual(_modes, stored.Select(line => line.Value("Mode")).ToArray(), "mode");
        Assert.AreSequenceEqual(_recordCounts, stored.Select(line => line.Value("Records")).ToArray(), "records");
        Assert.AreSequenceEqual(_storedCounts, stored.Select(line => line.Value("Stored")).ToArray(), "stored");
        Assert.AreSequenceEqual(_duplicateCounts, stored.Select(line => line.Value("Duplicates")).ToArray(), "duplicates");
        Assert.IsTrue(stored.All(line => line.Value("AgentId") == IngestSamples.AgentId), "agent ID of every batch");
    }

    /// <summary>
    /// No ingest log line of the scenarios holds the token, the header, body bytes, a map key or other record content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointLogsHoldNoTokenOrContent()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        string[] sentinels = ["SENTINEL-TOKEN", "SENTINEL-SOURCE", "SENTINEL MAP KEY", "SENTINEL-EXCEPTION", "SENTINEL-VALUE", "Bearer"];
        var labels = """{"SENTINEL MAP KEY":"SENTINEL-VALUE"}""";

        // Act
        using var wrongToken = IngestCall.Post(IngestSamples.Valid(), "SENTINEL-TOKEN-0123456789-abcdefghijklmnop");
        using var otherAgent = IngestCall.Post(IngestSamples.Valid(1, 2, "agent-2"));
        using var refused = IngestCall.Post(IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), IngestSamples.Metric(1, "SENTINEL-SOURCE", labels: labels)])));
        using var badJson = IngestCall.Post(IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), "{SENTINEL-SOURCE"])));
        using var encoded = IngestCall.Post(IngestSamples.Valid());
        using var stored = IngestCall.Post(IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), IngestSamples.Metric(1, "SENTINEL-SOURCE")])));
        using var failed = IngestCall.Post(IngestSamples.Valid(5, 1));

        encoded.With("Content-Encoding", "SENTINEL-VALUE");

        foreach (var call in new[] { wrongToken, otherAgent, refused, badJson, encoded, stored })
        {
            await Finish(rig.RunAsync(call));
            rig.Clock.Advance(TimeSpan.FromMinutes(2));
        }

        rig.Writer.FailNext(new IOException("SENTINEL-EXCEPTION"));
        await Finish(rig.RunAsync(failed));

        // Assert
        var text = string.Join('\n', rig.Log.AllTexts());

        Assert.IsGreaterThanOrEqualTo(6, rig.Log.Entries.Count, "the scenarios log their lines");

        foreach (var sentinel in sentinels)
        {
            Assert.DoesNotContain(sentinel, text, $"the log holds {sentinel}");
        }
    }

    /// <summary>
    /// A batch to be written while the free space is below the floor, or unreadable, is answered 503 and not written; a floor of 0 switches the check off.
    /// </summary>
    /// <param name="free">The free space the probe reports in bytes, <c>-1</c> for a volume that cannot be read</param>
    /// <param name="floor">The value of <c>ingest.min_free</c></param>
    /// <param name="status">The expected status</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(102400L, "1MiB", 503)]
    [DataRow(-1L, "1MiB", 503)]
    [DataRow(-1L, "0MiB", 204)]
    [DataRow(0L, "0MiB", 204)]
    [DataRow(1048576L, "1MiB", 204)]
    public async Task IngestEndpointLowFreeSpaceAnswers503AndWritesNothing(long free, string floor, int status)
    {
        // Arrange
        await using var rig = IngestRig.Create(minFree: floor);

        rig.FreeBytes = free < 0 ? null : free;

        // Act
        using var call = await rig.PostAsync(IngestSamples.Valid());

        // Assert
        Assert.AreEqual(status, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(status == 204 ? 1 : 0, rig.Writer.Calls, "writes");
    }

    /// <summary>
    /// The pause is logged once with the free space, and the resume once at the first batch written after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointLogsPauseOnceAndResume()
    {
        // Arrange
        await using var rig = IngestRig.Create(minFree: "1MiB");

        rig.FreeBytes = 4096;

        // Act
        using var first = await rig.PostAsync(IngestSamples.Valid(1, 2));
        using var second = await rig.PostAsync(IngestSamples.Valid(1, 2));

        var pausedLines = rig.Log.Named(Paused).Count;

        rig.FreeBytes = 5L << 20;

        using var resumed = await rig.PostAsync(IngestSamples.Valid(1, 2));
        using var next = await rig.PostAsync(IngestSamples.Valid(3, 2));

        rig.FreeBytes = null;

        using var paused = await rig.PostAsync(IngestSamples.Valid(5, 2));

        // Assert
        var pauses = rig.Log.Named(Paused);
        var resumes = rig.Log.Named(Resumed);

        Assert.AreEqual(503, first.Status, "status while paused");
        Assert.AreEqual(503, second.Status, "status while still paused");
        Assert.AreEqual(1, pausedLines, "one pause line for both refused batches");
        Assert.AreEqual(204, resumed.Status, "status after the space is back");
        Assert.AreEqual(204, next.Status, "status of the next batch");
        Assert.AreEqual(503, paused.Status, "status when the space cannot be read");
        Assert.HasCount(2, pauses, "a new pause is logged again");
        Assert.AreEqual(LogLevel.Warning, pauses[0].Level, "level of the pause");
        Assert.AreEqual("4096", pauses[0].Value("AvailableBytes"), "free space in the pause line");
        Assert.IsNull(pauses[1].Value("AvailableBytes"), "no free space when it cannot be read");
        Assert.HasCount(1, resumes, "the resume is logged once");
        Assert.AreEqual(LogLevel.Information, resumes[0].Level, "level of the resume");
    }

    /// <summary>
    /// An unexpected exception is answered 503 and logged with its type name only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointUnexpectedExceptionAnswers503WithTypeOnly()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.FailNext(new InvalidOperationException("SENTINEL-UNEXPECTED-TEXT"));

        // Act
        using var call = await rig.PostAsync(IngestSamples.Valid());

        // Assert
        var lines = rig.Log.Named(RequestFailed);

        Assert.AreEqual(503, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual(LogLevel.Error, lines[0].Level, "level");
        Assert.AreEqual(nameof(InvalidOperationException), lines[0].Value("Exception"), "type name");
        Assert.DoesNotContain("SENTINEL", string.Join('\n', rig.Log.AllTexts()), "the text of the exception is not logged");
    }

    /// <summary>
    /// Reads the lines of the golden batch and replaces its header.
    /// </summary>
    /// <param name="header">The new header line</param>
    /// <returns>The lines</returns>
    private static string[] GoldenLines(string header)
    {
        var lines = File.ReadAllLines(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"));

        lines[0] = header;

        return lines;
    }

    /// <summary>
    /// Waits for a request, and fails the test instead of hanging when it does not finish.
    /// </summary>
    /// <param name="run">The request</param>
    /// <returns>A task that completes with the request</returns>
    private Task Finish(Task run)
    {
        return run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
    }

    #endregion // Methods
}