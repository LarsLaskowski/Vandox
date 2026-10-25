using Microsoft.Extensions.Logging;

using Vandox.Backend.Ingest;
using Vandox.Core.Wire;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the slots, the time budget and the shutdown behavior of <see cref="IngestEndpoint"/> without a host.
/// </summary>
[TestClass]
public class IngestEndpointConcurrencyTests
{
    #region Constants

    private const string TimedOut = "ingest request timed out";

    private const string StoreFailed = "ingest store failed";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// At most two requests are in the writer at once; the others wait and do not read their body.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointAtMostTwoRequestsHoldASlot()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        var calls = Enumerable.Range(0, 5).Select(index => IngestCall.Post(IngestSamples.Valid((index * 10) + 1, 2))).ToList();

        try
        {
            // Act
            var runs = calls.Select(call => Task.Run(() => rig.RunAsync(call), TestContext.CancellationToken)).ToList();

            await rig.Writer.WaitForCallsAsync(IngestLimits.Slots, TestContext.CancellationToken, [.. runs]);
            await IngestRig.SettleAsync();

            var enteredAtFirst = rig.Writer.Calls;
            var readingAtFirst = calls.Count(call => call.Body.ReadCalls > 0);

            for (var released = 1; released <= 3; released++)
            {
                rig.Writer.ReleaseOne();
                await rig.Writer.WaitForCallsAsync(IngestLimits.Slots + released, TestContext.CancellationToken);
            }

            rig.Writer.ReleaseAll();
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

            // Assert
            Assert.AreEqual(IngestLimits.Slots, enteredAtFirst, "two writes start and the others wait");
            Assert.AreEqual(IngestLimits.Slots, readingAtFirst, "only the requests with a slot read their body");
            Assert.AreEqual(IngestLimits.Slots, rig.Writer.MaxInFlight, "never more than two writes at once");
            Assert.IsTrue(calls.All(call => call.Status == 204), "every request is answered 204 in the end");
        }
        finally
        {
            calls.ForEach(call => call.Dispose());
        }
    }

    /// <summary>
    /// A slot is held until the commit: a third request starts reading its body only after a write ended.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointSlotIsHeldUntilTheCommit()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var first = IngestCall.Post(IngestSamples.Valid(1, 2));
        using var second = IngestCall.Post(IngestSamples.Valid(11, 2));
        using var third = IngestCall.Post(IngestSamples.Valid(21, 2));

        var runs = new List<Task>
                   {
                       Task.Run(() => rig.RunAsync(first), TestContext.CancellationToken),
                       Task.Run(() => rig.RunAsync(second), TestContext.CancellationToken)
                   };

        await rig.Writer.WaitForCallsAsync(2, TestContext.CancellationToken, [.. runs]);

        // Act
        runs.Add(Task.Run(() => rig.RunAsync(third), TestContext.CancellationToken));
        await IngestRig.SettleAsync();

        var readBeforeCommit = third.Body.ReadCalls;

        rig.Writer.ReleaseOne();
        await Task.WhenAny(third.Body.FirstRead, runs[2]).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        rig.Writer.ReleaseAll();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, readBeforeCommit, "the third request does not read while both slots are held");
        Assert.AreEqual(204, third.Status, "the third request is answered once it has a slot");
        Assert.AreEqual(2, rig.Writer.MaxInFlight, "never more than two writes at once");
    }

    /// <summary>
    /// A request that is aborted after its write started, and one that passed the 30-second bound, still commit: only the shutdown deadline cancels a write.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointAbortedRequestStillCommits()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var call = IngestCall.Post(IngestSamples.Valid(1, 3));

        var run = Task.Run(() => rig.RunAsync(call), TestContext.CancellationToken);

        await rig.Writer.WaitForCallsAsync(1, TestContext.CancellationToken, run);

        // Act
        await call.AbortAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await IngestRig.SettleAsync();

        var cancelledWhileHeld = rig.Writer.Tokens[0].IsCancellationRequested;

        rig.Writer.ReleaseAll();

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The answer to a closed connection may be an abort; the commit is what counts.
        }

        // Assert
        Assert.IsFalse(cancelledWhileHeld, "neither the abort nor the 30-second bound cancels the write");
        Assert.AreEqual(1, rig.Writer.Completed, "the write committed");
    }

    /// <summary>
    /// The body buffers belong to the two slots: many requests one after the other allocate about two buffers, not one each.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointBodyBuffersBelongToTheSlots()
    {
        // Arrange
        const int requests = 12;

        await using var rig = IngestRig.Create();

        var body = IngestSamples.Valid(1, 5);

        await DecodeAsync(body);

        var baseline = GC.GetTotalAllocatedBytes(true);

        for (var index = 0; index < requests; index++)
        {
            await DecodeAsync(body);
        }

        baseline = GC.GetTotalAllocatedBytes(true) - baseline;

        using var warmup = await rig.PostAsync(body);

        var before = GC.GetTotalAllocatedBytes(true);

        // Act
        for (var index = 0; index < requests; index++)
        {
            using var call = await rig.PostAsync(body);
        }

        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        // Assert
        var allowance = (IngestLimits.Slots * (long)IngestLimits.MaxBodyBytes) + (1 << 20);

        Assert.AreEqual(204, warmup.Status, "the warm-up request is answered 204");
        Assert.IsLessThan(baseline + allowance, allocated, $"allocated {allocated} bytes for {requests} requests, the decoder alone {baseline}");
    }

    /// <summary>
    /// A request that waits for a slot, or whose body trickles in, for more than 30 seconds gets no 2xx and writes nothing; the writes that started are not
    /// affected.
    /// </summary>
    /// <param name="situation">The situation</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("slot wait")]
    [DataRow("trickling body")]
    public async Task IngestEndpointRequestOverBudgetGetsNo2xx(string situation)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var holder = IngestCall.Post(IngestSamples.Valid(1, 2));
        using var other = IngestCall.Post(IngestSamples.Valid(11, 2));
        using var late = situation == "slot wait" ? IngestCall.Post(IngestSamples.Valid(21, 2)) : IngestCall.Chunked(new CountingBodyStream(IngestSamples.Valid(21, 2)[..10], true));

        var holders = new List<Task>();

        if (situation == "slot wait")
        {
            holders.Add(Task.Run(() => rig.RunAsync(holder), TestContext.CancellationToken));
            holders.Add(Task.Run(() => rig.RunAsync(other), TestContext.CancellationToken));
            await rig.Writer.WaitForCallsAsync(2, TestContext.CancellationToken, [.. holders]);
        }

        var run = Task.Run(() => rig.RunAsync(late), TestContext.CancellationToken);

        if (situation == "trickling body")
        {
            await Task.WhenAny(late.Body.FirstRead, run).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            Assert.IsTrue(late.Body.FirstRead.IsCompleted, "the request started to read its body");
        }
        else
        {
            // The late request takes its receive time and creates its budget timer before the clock moves.
            await IngestRig.SettleAsync();
            Assert.IsFalse(run.IsCompleted, "the late request waits for a slot");
        }

        // Act
        await AdvanceUntilDoneAsync(rig, run);
        rig.Writer.ReleaseAll();
        await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        var lines = rig.Log.Named(TimedOut);

        Assert.IsTrue(late.Status is 503 or 408, $"status {late.Status} is a refusal to wait, not a 2xx");
        Assert.AreEqual(situation == "slot wait" ? 2 : 0, rig.Writer.Calls, "the late request never reaches the writer");
        Assert.HasCount(1, lines, "one line");
        Assert.AreEqual(LogLevel.Warning, lines[0].Level, "level");
        Assert.AreEqual(IngestCall.Peer, lines[0].Value("Peer"), "peer");

        if (situation == "slot wait")
        {
            Assert.AreEqual(204, holder.Status, "the first write that started is not cancelled by the bound");
            Assert.AreEqual(204, other.Status, "the second write that started is not cancelled by the bound");
        }
    }

    /// <summary>
    /// A request that waits for a slot when its connection is aborted is not answered, logs nothing and never reaches the writer; the stop signal answers it
    /// 503 without the timeout line.
    /// </summary>
    /// <param name="event">What ends the wait: <c>abort</c> or <c>stop</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("abort")]
    [DataRow("stop")]
    public async Task IngestEndpointWaitingRequestEndsWithAbortOrStop(string @event)
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var holder = IngestCall.Post(IngestSamples.Valid(1, 2));
        using var other = IngestCall.Post(IngestSamples.Valid(11, 2));
        using var waiting = IngestCall.Post(IngestSamples.Valid(21, 2));

        var holders = new List<Task>
                      {
                          Task.Run(() => rig.RunAsync(holder), TestContext.CancellationToken),
                          Task.Run(() => rig.RunAsync(other), TestContext.CancellationToken)
                      };

        await rig.Writer.WaitForCallsAsync(2, TestContext.CancellationToken, [.. holders]);

        var run = Task.Run(() => rig.RunAsync(waiting), TestContext.CancellationToken);

        await IngestRig.SettleAsync();

        // Act
        if (@event == "abort")
        {
            await waiting.AbortAsync();
        }
        else
        {
            rig.Lifetime.Stop();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        rig.Writer.ReleaseAll();
        await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(@event == "abort" ? 200 : 503, waiting.Status, "no answer is set for a gone connection, 503 at the stop signal");
        Assert.IsNull(waiting.Refused, "no refusal header");
        Assert.AreEqual(0, waiting.Body.ReadCalls, "the waiting request never read its body");
        Assert.AreEqual(2, rig.Writer.Calls, "the waiting request never reaches the writer");
        Assert.IsEmpty(rig.Log.Named(TimedOut), "the wait is not logged as a timeout");
    }

    /// <summary>
    /// The drain closes the gate and waits for the writes in flight; a request that comes later gets 503 and does not write.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointDrainWaitsForWritesInFlightAndRefusesLaterOnes()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var inFlight = IngestCall.Post(IngestSamples.Valid(1, 2));

        var run = Task.Run(() => rig.RunAsync(inFlight), TestContext.CancellationToken);

        await rig.Writer.WaitForCallsAsync(1, TestContext.CancellationToken, run);

        // Act
        var drain = rig.Endpoint.DrainAsync();

        await IngestRig.SettleAsync();

        var drainedWhileWriting = drain.IsCompleted;

        rig.Writer.ReleaseAll();
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        await drain.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        using var later = await rig.PostAsync(IngestSamples.Valid(5, 2));

        // Assert
        Assert.IsFalse(drainedWhileWriting, "the drain waits for the write");
        Assert.AreEqual(204, inFlight.Status, "the write that started is committed");
        Assert.AreEqual(503, later.Status, "a request after the drain gets 503");
        Assert.IsNull(later.Refused, "no refusal header");
        Assert.AreEqual(1, rig.Writer.Calls, "the later request does not reach the writer");
    }

    /// <summary>
    /// A write is cancelled 10 seconds after the stop signal, not before, and the request is answered 503 with the store failure logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointCancelsAWriteTenSecondsAfterTheStopSignal()
    {
        // Arrange
        await using var rig = IngestRig.Create();

        rig.Writer.Hold();

        using var call = IngestCall.Post(IngestSamples.Valid(1, 2));

        var run = Task.Run(() => rig.RunAsync(call), TestContext.CancellationToken);

        await rig.Writer.WaitForCallsAsync(1, TestContext.CancellationToken, run);

        var token = rig.Writer.Tokens[0];

        // Act
        rig.Lifetime.Stop();
        await IngestRig.SettleAsync();

        var cancelledAtStop = token.IsCancellationRequested;

        rig.Clock.Advance(TimeSpan.FromSeconds(9));

        var cancelledAtNine = token.IsCancellationRequested;

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        await rig.Endpoint.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        var lines = rig.Log.Named(StoreFailed);

        Assert.IsFalse(cancelledAtStop, "the write is not cancelled at the stop signal");
        Assert.IsFalse(cancelledAtNine, "the write is not cancelled after 9 seconds");
        Assert.IsTrue(token.IsCancellationRequested, "the write is cancelled after 10 seconds");
        Assert.AreEqual(503, call.Status, "status");
        Assert.IsNull(call.Refused, "no refusal header");
        Assert.AreEqual(1, rig.Writer.Ended, "the write ended");
        Assert.AreEqual(0, rig.Writer.Completed, "the cancelled write returned no result");
        Assert.HasCount(1, lines, "the failure is logged");
        Assert.Contains("OperationCanceledException", lines[0].Value("Exception") ?? string.Empty, "type of the exception");
    }

    /// <summary>
    /// A request that finished decoding when the stop signal fired gets no 2xx and the writer sees no call: the gate closes at the stop signal, not only at the
    /// drain.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestEndpointRefusesAWriteThatStartsAfterTheStopSignal()
    {
        // Arrange
        await using var rig = IngestRig.Create(minFree: "1MiB");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var release = new ManualResetEventSlim(false);
        using var call = IngestCall.Post(IngestSamples.Valid(1, 2));

        rig.OnProbe = () =>
                      {
                          entered.TrySetResult();
                          release.Wait(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

                          return 5L << 20;
                      };

        var run = Task.Run(() => rig.RunAsync(call), TestContext.CancellationToken);

        try
        {
            await Task.WhenAny(entered.Task, run).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            Assert.IsTrue(entered.Task.IsCompleted, "the request reached the free space check");

            // Act
            rig.Lifetime.Stop();
        }
        finally
        {
            release.Set();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(call.Status is >= 200 and < 300, $"status {call.Status} is not a 2xx");
        Assert.AreEqual(0, rig.Writer.Calls, "the writer sees no call");
    }

    /// <summary>
    /// Moves the fake clock past the 30-second budget until the request is done. The request may take its receive time and create its budget timer only after
    /// the first step, so the clock moves again until the timer fires; no fixed delay decides the outcome.
    /// </summary>
    /// <param name="rig">The rig whose clock moves</param>
    /// <param name="run">The request</param>
    /// <returns>A task that completes when the request is done</returns>
    private async Task AdvanceUntilDoneAsync(IngestRig rig, Task run)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        bound.CancelAfter(TimeSpan.FromSeconds(60));

        Task finished;

        do
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(31));
            finished = await Task.WhenAny(run, Task.Delay(10, bound.Token));
            bound.Token.ThrowIfCancellationRequested();
        }
        while (finished != run);

        await run;
    }

    /// <summary>
    /// Decodes a batch to its end, as the baseline of the allocation measurement.
    /// </summary>
    /// <param name="body">The compressed batch</param>
    /// <returns>A task that completes when the batch is decoded</returns>
    private async Task DecodeAsync(byte[] body)
    {
        using var decoder = await BatchDecoder.OpenAsync(new MemoryStream(body),
                                                         new WireLimits
                                                         {
                                                             MaxLineBytes = IngestLimits.MaxLineBytes,
                                                             MaxBatchBytes = IngestLimits.MaxBatchBytes,
                                                             MaxRecords = IngestLimits.MaxRecords
                                                         },
                                                         TestContext.CancellationToken);

        while (await decoder.NextAsync(TestContext.CancellationToken) is not null)
        {
            // The records are read to measure the decoder.
        }
    }

    #endregion // Methods
}