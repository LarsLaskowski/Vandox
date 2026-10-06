using Vandox.Core.LogParsing;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="Importer"/> that cover batches, resuming, parser failures and store failures
/// </summary>
[TestClass]
public class ImporterStoreTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Records are written in batches of the configured size, and the last batch completes the file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterWritesBatchesAndCompletesFile()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);

        // Assert
        Assert.AreEqual(5L, run.Summary.Records, "records");
        Assert.AreEqual("2,2,1", string.Join(',', harness.Store.Batches.Select(batch => batch.Records.Count)), "batch sizes");
        Assert.IsTrue(harness.Store.Batches[^1].Import!.Complete, "the last batch completes the file");
        Assert.IsFalse(harness.Store.Batches[0].Import!.Complete, "the first batch does not");
        Assert.AreEqual("0,2,4", string.Join(',', harness.Store.Batches.Select(batch => batch.Import!.Done)), "stored counts");
        Assert.AreEqual(harness.Clock.GetUtcNow(), harness.Store.Batches[0].ReceivedAt, "receive time comes from the clock");
    }

    /// <summary>
    /// A batch is also flushed when the input bytes of the batch reach the limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFlushesBatchWhenInputBytesReachLimit()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", TestInputs.Log(6, $"LOG {new string('x', 20000)}"));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchBytes = 10000);

        // Assert
        Assert.AreEqual(6L, run.Summary.Records, "records");
        Assert.IsGreaterThan(1, harness.Store.Batches.Count, "more than one batch");
    }

    /// <summary>
    /// An interrupted import resumes where it stopped and stores every record once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterResumesInterruptedImport()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();

        harness.Write("a.log", TestInputs.Log(7));
        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 2)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        // Act
        var first = await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);

        harness.Store.AfterBatch = null;

        var second = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);

        // Assert
        Assert.IsTrue(first.Summary.Interrupted, "the first run is interrupted");
        Assert.IsInstanceOfType<OperationCanceledException>(first.Error, "the first run ends with the cancellation");
        Assert.AreEqual(4L, first.Summary.Files[0].Records, "two batches were stored");
        Assert.AreEqual(4L, second.Summary.Files[0].ResumedAfter, "the second run resumes after the stored records");
        Assert.AreEqual(3L, second.Summary.Files[0].Records, "the second run stores the rest");
        Assert.AreEqual(7, harness.Store.Records.Count, "every record is stored once");
        Assert.AreEqual("LOG line 5", ((LogLine)harness.Store.Records[4].Data!).Message, "the resumed run continues with the fifth line");
    }

    /// <summary>
    /// Lines the parser skips and records the store would refuse are counted and described, at most ten per file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterCountsSkippedLinesAndRefusedRecords()
    {
        // Arrange
        using var harness = new ImportHarness();
        var content = $"LOG ok\n{string.Concat(Enumerable.Repeat("bad line\n", 12))}refuse this\nLOG ok again\n";

        harness.Write("a.log", content);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files[0];

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported");
        Assert.AreEqual(2L, file.Records, "valid records");
        Assert.AreEqual(13L, file.Skipped, "twelve bad lines and one refused record");
        Assert.AreEqual(ImportLimits.MaxProblems, file.Problems.Count, "problems are capped");
        Assert.AreEqual(new ImportProblem(2, "bad line"), file.Problems[0], "first problem");
        Assert.AreEqual(15L, file.Lines, "lines");
        Assert.AreEqual(13L, run.Summary.Skipped, "skipped in the summary");
    }

    /// <summary>
    /// A parser that fails fails its file after what it parsed before the failure was stored; the run goes on.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsFileWhenParserFails()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", "LOG one\nLOG two\nboom\nLOG never\n");
        harness.Write("b.log", TestInputs.Log(2, "LOG b"));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var failed = run.Summary.Files.Single(file => file.Outcome == ImportOutcome.Failed);

        // Assert
        Assert.IsNull(run.Error, "the run goes on");
        Assert.AreEqual("the parser gave up", failed.Reason, "reason");
        Assert.AreEqual(2L, failed.Records, "the records before the failure are stored");
        Assert.AreEqual(2, run.Summary.Count(ImportOutcome.Imported) + 1, "the other file is imported");
    }

    /// <summary>
    /// A file that changes between the two passes fails, and nothing of it is completed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsFileThatChangedBetweenPasses()
    {
        // Arrange
        using var harness = new ImportHarness();
        var path = harness.Write("a.log", TestInputs.Log(3));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options => options.Progress = progress =>
                                                                       {
                                                                           if (progress.Event == ProgressEvent.Scanned)
                                                                           {
                                                                               File.WriteAllText(path, TestInputs.Log(3, "LOG changed"));
                                                                           }
                                                                       });
        var file = run.Summary.Files[0];

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, file.Outcome, "outcome");
        Assert.AreEqual("the content changed while it was imported", file.Reason, "reason");
        Assert.IsFalse(harness.Store.Batches.Any(batch => batch.Import!.Complete), "the file is never completed");
    }

    /// <summary>
    /// A file that grew between the passes is read only up to the hashed size and then fails the check.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsFileThatShrankBetweenPasses()
    {
        // Arrange
        using var harness = new ImportHarness();
        var path = harness.Write("a.log", TestInputs.Log(10));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options => options.Progress = progress =>
                                                                       {
                                                                           if (progress.Event == ProgressEvent.Scanned)
                                                                           {
                                                                               File.WriteAllText(path, TestInputs.Log(2));
                                                                           }
                                                                       });

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, run.Summary.Files[0].Outcome, "outcome");
    }

    /// <summary>
    /// A content that was imported before as another source type is not imported again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsContentImportedAsOtherSourceType()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", TestInputs.Log(2));
        harness.Store.ReportedSourceType = "other";

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("the content was imported before as source type \"other\"", run.Summary.Files[0].Reason, "reason");
        Assert.AreEqual(0, harness.Store.Records.Count, "nothing stored");
    }

    /// <summary>
    /// A conflict of the import state fails the file only; any other store error ends the run.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterHandlesStoreErrors()
    {
        // Arrange
        using var conflict = new ImportHarness();
        using var broken = new ImportHarness();
        using var begin = new ImportHarness();

        conflict.Write("a.log", TestInputs.Log(2));
        conflict.Write("b.log", TestInputs.Log(2, "LOG b"));
        conflict.Store.WriteFailure = new ImportConflictException("store: import state changed");
        broken.Write("a.log", TestInputs.Log(2));
        broken.Write("b.log", TestInputs.Log(2, "LOG b"));
        broken.Store.WriteFailure = new StoreException("store: writing batch: disk full");
        begin.Write("a.log", TestInputs.Log(2));
        begin.Store.BeginFailure = new StoreException("store: beginning import: locked");

        // Act
        var conflicted = await conflict.RunAsync(TestContext.CancellationToken);
        var failed = await broken.RunAsync(TestContext.CancellationToken);
        var refused = await begin.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNull(conflicted.Error, "a conflict does not end the run");
        Assert.AreEqual(2, conflicted.Summary.Count(ImportOutcome.Failed), "both files fail");
        Assert.AreEqual("the import state changed (another import of the same content is running?)", conflicted.Summary.Files[0].Reason, "conflict reason");
        Assert.IsInstanceOfType<ImportException>(failed.Error, "a store error ends the run");
        Assert.AreEqual("importer: writing to the database failed", failed.Error?.Message, "message without store text");
        Assert.AreEqual("database error", failed.Summary.Files[0].Reason, "reason");
        Assert.AreEqual(1, failed.Summary.Files.Count, "the second file was not started");
        Assert.IsInstanceOfType<ImportException>(refused.Error, "a failed begin ends the run");
    }

    /// <summary>
    /// Cancelling before the run starts interrupts it at once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterInterruptedBeforeStart()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var cancelled = new CancellationTokenSource();

        harness.Write("a.log", TestInputs.Log(2));
        await cancelled.CancelAsync();

        // Act
        var run = await harness.RunAsync(cancelled.Token);

        // Assert
        Assert.IsTrue(run.Summary.Interrupted, "interrupted");
        Assert.AreEqual(0, harness.Store.Records.Count, "nothing stored");
    }

    #endregion // Methods
}