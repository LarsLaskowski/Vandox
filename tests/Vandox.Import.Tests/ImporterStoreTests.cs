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
        Assert.HasCount(7, harness.Store.Records, "every record is stored once");
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
        Assert.HasCount(ImportLimits.MaxProblems, file.Problems, "problems are capped");
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
        Assert.DoesNotContain(batch => batch.Import!.Complete, harness.Store.Batches, "the file is never completed");
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
        Assert.IsEmpty(harness.Store.Records, "nothing stored");
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
        Assert.HasCount(1, failed.Summary.Files, "the second file was not started");
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
        Assert.IsEmpty(harness.Store.Records, "nothing stored");
    }

    /// <summary>
    /// A file whose content is a completely imported content plus appended lines stores only the appended lines, and a third run reports it as already imported.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterContinuesGrownFileAfterCompleteEarlierContent()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));
        var baseSize = TestInputs.Bytes(TestInputs.Log(3)).Length;

        // Act
        var first = await harness.RunAsync(TestContext.CancellationToken);

        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(5));

        var second = await harness.RunAsync(TestContext.CancellationToken);
        var file = second.Summary.Files.Single();
        var third = await harness.RunAsync(TestContext.CancellationToken);
        var prefix = harness.Store.Prefixes[1].Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, first.Summary.Files[0].Outcome, "run 1 imports the earlier content");
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "run 2 continues it");
        Assert.AreEqual(2L, file.Records, "run 2 stores the records of the new lines");
        Assert.AreEqual("a/syslog", file.ContinuedFrom, "the display name of the earlier content");
        Assert.AreEqual(3L, file.ContinuedAfter, "the lines of the earlier content");
        Assert.AreEqual(TestInputs.Messages(1, 5), string.Join('|', harness.Store.Messages), "every line is stored once, in order");
        Assert.AreEqual(ImportOutcome.AlreadyImported, third.Summary.Files.Single().Outcome, "run 3 imports nothing");
        Assert.HasCount(5, harness.Store.Records, "run 3 stores nothing");
        Assert.AreEqual((long)baseSize, prefix.Size, "the prefix is the earlier content");
        Assert.AreEqual((long)baseSize, prefix.Offset, "the earlier content ends with a line feed, so the new part starts at its end");
        Assert.HasCount(32, harness.Store.Heads[1], "the head key is passed");
    }

    /// <summary>
    /// A file that grew again is continued after the longest complete content it starts with.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterContinuesAfterTheLongestBaseOfAChain()
    {
        // Arrange
        using var harness = new ImportHarness();
        var one = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(one);

        var two = harness.Write("b/syslog", TestInputs.Log(5));
        var second = await harness.RunAsync(TestContext.CancellationToken);

        File.Delete(two);
        harness.Write("c/syslog", TestInputs.Log(7));

        // Act
        var third = await harness.RunAsync(TestContext.CancellationToken);
        var file = third.Summary.Files.Single();

        // Assert
        Assert.AreEqual("a/syslog", second.Summary.Files.Single().ContinuedFrom, "run 2 continues the first content");
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "run 3 continues");
        Assert.AreEqual("b/syslog", file.ContinuedFrom, "the longest base, not the first one");
        Assert.AreEqual(5L, file.ContinuedAfter, "the lines of the longest base");
        Assert.AreEqual(2L, file.Records, "only the lines after it are stored");
        Assert.HasCount(2, harness.Store.Prefixes[2], "both bases are offered to the store");
        Assert.AreEqual(TestInputs.Messages(1, 7), string.Join('|', harness.Store.Messages), "every line is stored once");
    }

    /// <summary>
    /// A new part starts at the beginning of the line in which the earlier content ends, so that line has two records afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterContinuesAtTheBeginningOfTheLineThatTheEarlierContentCut()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", "LOG l1\nLOG l2\nLOG l3-part");

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", "LOG l1\nLOG l2\nLOG l3-partrest\nLOG l4\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "continued");
        Assert.AreEqual(2L, file.ContinuedAfter, "two complete lines before the point");
        Assert.AreEqual(14L, harness.Store.Prefixes[1].Single().Offset, "the point is the beginning of the cut line");
        Assert.AreEqual("LOG l1|LOG l2|LOG l3-part|LOG l3-partrest|LOG l4", string.Join('|', harness.Store.Messages), "the cut line has two records, the other lines one");
    }

    /// <summary>
    /// An earlier content without any line feed is never continued.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAsAWholeWhenTheEarlierContentHasNoLineFeed()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", "LOG only-part");

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", "LOG only-part more\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(string.Empty, file.ContinuedFrom, "continues nothing");
        Assert.IsEmpty(harness.Store.Prefixes[1], "no prefix is passed to the store");
        Assert.AreEqual("LOG only-part|LOG only-part more", string.Join('|', harness.Store.Messages), "all lines are stored");
    }

    /// <summary>
    /// A file whose first bytes differ from the earlier content in one byte after the first line is imported as a whole.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAsAWholeWhenThePrefixDiffersInOneByte()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(5).Replace("line 2", "lime 2", StringComparison.Ordinal));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(5L, file.Records, "all lines are stored");
        Assert.IsEmpty(harness.Store.Prefixes[1], "no prefix is passed to the store");
    }

    /// <summary>
    /// A content that was imported with another source type is not a base.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAsAWholeWhenTheEarlierContentHasAnotherSourceType()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.Parsers = new ParserRegistry([new LineParser("other")]));
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual("other", file.SourceType, "with the other parser");
        Assert.AreEqual(5L, file.Records, "all lines are stored");
    }

    /// <summary>
    /// An earlier content whose import is not complete is not a base when the longer file is imported alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAsAWholeWhenTheEarlierContentIsIncomplete()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var earlier = harness.Write("a/syslog", TestInputs.Log(7));

        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 1)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
        harness.Store.AfterBatch = null;
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(9));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(9L, file.Records, "all lines are stored");
        Assert.IsEmpty(harness.Store.Prefixes[^1], "no prefix is passed to the store");
    }

    /// <summary>
    /// An earlier content that the same run resumes is completed first, and the longer file is continued from it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterContinuesFromAnEarlierContentThatTheSameRunResumes()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();

        harness.Write("a/syslog", TestInputs.Log(7));
        harness.Write("b/syslog", TestInputs.Log(9));
        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 1)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
        harness.Store.AfterBatch = null;

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);
        var earlier = run.Summary.Files.Single(file => file.Path == "a/syslog");
        var later = run.Summary.Files.Single(file => file.Path == "b/syslog");

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, earlier.Outcome, "the earlier content is completed");
        Assert.AreEqual(2L, earlier.ResumedAfter, "after the batch of the first run");
        Assert.AreEqual(ImportOutcome.Continued, later.Outcome, "the longer file is continued");
        Assert.AreEqual("a/syslog", later.ContinuedFrom, "from the resumed content");
        Assert.AreEqual(7L, later.ContinuedAfter, "after its lines");
        Assert.AreEqual(TestInputs.Messages(1, 9), string.Join('|', harness.Store.Messages), "every line is stored once");
    }

    /// <summary>
    /// The counts and the line numbers of a continued file are those of the new part and of the whole file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterCountsLinesFromTheContinuationPointAndNumbersSkippedLinesInTheWholeFile()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", $"{TestInputs.Log(3)}LOG four\nbad five\nLOG six\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "continued");
        Assert.AreEqual(3L, file.Lines, "only the lines of the new part are read");
        Assert.AreEqual(3L, run.Summary.Lines, "the summary sums them");
        Assert.AreEqual(2L, file.Records, "two records");
        Assert.AreEqual(1L, file.Skipped, "one skipped line");
        Assert.AreEqual(new ImportProblem(5, "bad line"), file.Problems.Single(), "the bad line is line 5 of the whole file");
    }

    /// <summary>
    /// The progress of a continued file counts the lines from the continuation point.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterReportsProgressOfAContinuedFileFromTheContinuationPoint()
    {
        // Arrange
        using var harness = new ImportHarness();
        var head = string.Concat(Enumerable.Repeat("LOG\n", (int)ImportLimits.ProgressLines - 10));
        var earlier = harness.Write("a/syslog", head);

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", $"{head}{string.Concat(Enumerable.Repeat("LOG\n", 20))}");
        harness.Events.Clear();

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, run.Summary.Files.Single().Outcome, "continued");
        Assert.AreEqual(20L, run.Summary.Files.Single().Lines, "twenty lines in the new part");
        Assert.DoesNotContain(item => item.Event == ProgressEvent.FileProgress, harness.Events, "the lines before the point do not count toward a progress mark");
    }

    /// <summary>
    /// A file whose prefix part changed between the two passes fails like any other changed file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsAContinuedFileWhosePrefixChangedBetweenPasses()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);

        var path = harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options => options.Progress = progress =>
                                                                       {
                                                                           if (progress.Event == ProgressEvent.Scanned)
                                                                           {
                                                                               File.WriteAllText(path, TestInputs.Log(5).Replace("line 2", "lime 2", StringComparison.Ordinal));
                                                                           }
                                                                       });
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, file.Outcome, "outcome");
        Assert.AreEqual("the content changed while it was imported", file.Reason, "reason");
        Assert.HasCount(1, harness.Store.Prefixes[^1], "the file was a continuation when it was hashed");
        Assert.IsFalse(harness.Store.States.Last().Complete, "the state of the changed file is not complete");
    }

    /// <summary>
    /// A file that is shorter than the continuation point in pass 2 fails like any other changed file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsAContinuedFileThatIsShorterThanTheContinuationPointInPassTwo()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);

        var path = harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options => options.Progress = progress =>
                                                                       {
                                                                           if (progress.Event == ProgressEvent.Scanned)
                                                                           {
                                                                               File.WriteAllText(path, TestInputs.Log(1));
                                                                           }
                                                                       });
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, file.Outcome, "outcome");
        Assert.AreEqual("the content changed while it was imported", file.Reason, "reason");
        Assert.HasCount(1, harness.Store.Prefixes[^1], "the file was a continuation when it was hashed");
        Assert.IsFalse(harness.Store.States.Last().Complete, "the state of the changed file is not complete");
    }

    /// <summary>
    /// An interrupted continuation resumes with its stored continuation point, also when a larger complete content appeared in between.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterResumesAContinuationAtItsStoredPoint()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(9));
        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 2)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        var first = await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
        var larger = TestInputs.Bytes(TestInputs.Log(7));

        harness.Store.AfterBatch = null;
        harness.Store.Seed(new ImportFile
                           {
                               Sha256 = FakeImportStore.Hash(larger),
                               Size = larger.Length,
                               Name = "seed/syslog",
                               FileName = "syslog",
                               SourceType = "test",
                               Records = 7,
                               Complete = true
                           },
                           ContentHead.Key(larger));

        // Act
        var second = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);
        var file = second.Summary.Files.Single();

        // Assert
        Assert.IsTrue(first.Summary.Interrupted, "the first continuation is interrupted");
        Assert.Contains(prefix => prefix.Size == larger.Length, harness.Store.Prefixes[^1], "the larger content is offered as a prefix");
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "continued");
        Assert.AreEqual("a/syslog", file.ContinuedFrom, "from the stored base, not from the larger one");
        Assert.AreEqual(3L, file.ContinuedAfter, "after the stored point");
        Assert.AreEqual(2L, file.ResumedAfter, "after the records of the first run");
        Assert.AreEqual(4L, file.Records, "the rest of the new part is stored");
        Assert.AreEqual(TestInputs.Messages(1, 9), string.Join('|', harness.Store.Messages), "every line is stored once");
    }

    /// <summary>
    /// The parser of a continued file gets the name and modification time of the continued file, not those of the earlier content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterGivesTheParserOfAContinuedFileItsOwnNameAndModificationTime()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));
        var later = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(earlier, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);

        var path = harness.Write("b/syslog", TestInputs.Log(5));

        File.SetLastWriteTimeUtc(path, later);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, run.Summary.Files.Single().Outcome, "continued");
        Assert.AreEqual("b/syslog", harness.Parser.Seen[^1].Name, "the name of the continued file");
        Assert.AreEqual(new DateTimeOffset(later), harness.Parser.Seen[^1].ModTime, "the modification time of the continued file");
    }

    /// <summary>
    /// A failed lookup of the import state ends the run with a fixed message and keeps the files found so far.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterEndsTheRunWhenReadingTheImportStateFails()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.txt", "just a note\n");
        harness.Write("b.log", TestInputs.Log(2));
        harness.Store.FindFailure = new StoreException("store: finding import bases: locked");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsInstanceOfType<ImportException>(run.Error, "the run ends with an import exception");
        Assert.StartsWith("importer: reading the import state failed", run.Error.Message, "message");
        Assert.DoesNotContain("locked", run.Error.Message, "the message holds no store text");
        Assert.AreEqual("a.txt", run.Summary.Files.Single().Path, "the files found so far");
        Assert.IsEmpty(harness.Store.Records, "nothing is stored");
        Assert.IsFalse(run.Summary.Interrupted, "not interrupted");
    }

    /// <summary>
    /// A cancellation during the lookup of the import state ends the run as interrupted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterEndsTheRunAsInterruptedWhenCancelledDuringTheLookup()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();

        harness.Write("a.log", TestInputs.Log(2));
        harness.Store.BeforeFind = interrupt.Cancel;

        // Act
        var run = await harness.RunAsync(interrupt.Token);

        // Assert
        Assert.IsTrue(run.Summary.Interrupted, "interrupted");
        Assert.IsInstanceOfType<OperationCanceledException>(run.Error, "the run ends with the cancellation");
        Assert.IsEmpty(harness.Store.Records, "nothing is stored");
    }

    /// <summary>
    /// The lines a parser skips before its first record are counted for a continued file only, up to the first record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterCountsTheLinesSkippedBeforeTheFirstRecordOfAContinuedFile()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", $"{TestInputs.Log(3)}one\ntwo\nLOG six\nbad seven\nLOG eight\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.Parsers = new ParserRegistry([new LineParser("test", true, 2)]));
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "continued");
        Assert.AreEqual(2L, file.SkippedAtStart, "two lines before the first record");
        Assert.AreEqual(3L, file.Skipped, "the skipped line after the first record is skipped but not counted at the start");
        Assert.AreEqual("4,5,7", string.Join(',', file.Problems.Select(problem => problem.Line)), "line numbers in the whole file");
    }

    /// <summary>
    /// Skipped lines with line 0 are not counted, a refused record ends the count, and skips after the first record are not counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterEndsTheCountOfLinesSkippedAtTheStartAtTheFirstRecordCall()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", $"{TestInputs.Log(3)}bad four\nrefuse five\nbad six\nLOG seven\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, file.Outcome, "continued");
        Assert.AreEqual(1L, file.SkippedAtStart, "only the line before the refused record");
        Assert.AreEqual(3L, file.Skipped, "all skipped lines and the refused record");
    }

    /// <summary>
    /// A resumed continuation gives the same count of lines skipped at the start, and a file that is not continued gives 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterGivesTheSameCountOfLinesSkippedAtTheStartWhenAContinuationIsResumed()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", $"{TestInputs.Log(3)}bad four\nLOG five\nbad six\nLOG seven\nLOG eight\n");
        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 2)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        var first = await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 1);

        harness.Store.AfterBatch = null;

        // Act
        var second = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 1);
        var file = second.Summary.Files.Single();

        // Assert
        Assert.IsTrue(first.Summary.Interrupted, "the first run is interrupted");
        Assert.AreEqual(1L, file.ResumedAfter, "one record was stored");
        Assert.AreEqual(1L, file.SkippedAtStart, "the line before the first record, and the dropped record ends the count");
        Assert.AreEqual(2L, file.Skipped, "the parse repeats both skipped lines");
    }

    /// <summary>
    /// A file that is not continued counts no lines skipped at the start, also when its parser skipped lines before its first record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterCountsNoLinesSkippedAtTheStartForAFileThatIsNotContinued()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.Parsers = new ParserRegistry([new LineParser("test", true, 2)]));
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported");
        Assert.AreEqual(2L, file.Skipped, "the parser skipped two lines");
        Assert.AreEqual(0L, file.SkippedAtStart, "not counted for a file that is not continued");
    }

    /// <summary>
    /// A file whose parser is not line-framed is never looked up and never continued, but imported as a whole.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAsAWholeWhenTheParserIsNotLineFramed()
    {
        // Arrange
        using var harness = new ImportHarness(false);
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(0, harness.Store.FindCalls, "the store is not asked for bases");
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(5L, file.Records, "every line is stored");
        Assert.IsTrue(harness.Store.Prefixes.All(prefixes => prefixes.Count == 0), "no prefix is passed to the store");
        Assert.IsTrue(harness.Store.Heads.All(head => head.Length == 32), "the head key is passed");
        Assert.HasCount(8, harness.Store.Records, "the lines of both contents are stored");
    }

    /// <summary>
    /// A stored continuation point is refused for a parser that is not line-framed, and the run goes on with the next file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsAFileWhoseStateContinuesTheContentForAParserThatIsNotLineFramed()
    {
        // Arrange
        using var harness = new ImportHarness(false);
        var content = TestInputs.Bytes(TestInputs.Log(5));

        harness.Write("a.log", content);
        harness.Write("b.log", TestInputs.Log(2, "LOG b"));
        harness.Store.Seed(new ImportFile
                           {
                               Sha256 = FakeImportStore.Hash(content),
                               Size = content.Length,
                               Name = "seed/syslog",
                               FileName = "a.log",
                               SourceType = "test",
                               ContinuesId = 99,
                               Offset = 11
                           },
                           ContentHead.Key(content));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var refused = run.Summary.Files.Single(file => file.Path == "a.log");
        var next = run.Summary.Files.Single(file => file.Path == "b.log");

        // Assert
        Assert.IsNull(run.Error, "the run goes on");
        Assert.AreEqual(ImportOutcome.Failed, refused.Outcome, "the file fails");
        Assert.AreEqual("the import state continues the content, which its parser does not allow", refused.Reason, "reason");
        Assert.IsEmpty(harness.Parser.Seen.Where(file => file.Name == "a.log"), "nothing of the file is parsed");
        Assert.AreEqual(ImportOutcome.Imported, next.Outcome, "the next file is imported");
        Assert.AreEqual("LOG b 1|LOG b 2", string.Join('|', harness.Store.Messages), "only the next file is stored");
    }

    /// <summary>
    /// An older copy of a complete content, imported in a later run, is recognized: nothing is stored or written for it, and the result names the first import. The copy may be gzip-compressed.
    /// </summary>
    /// <param name="gzip">Whether the older copy is gzip-compressed</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImporterRecognizesOlderCopyImportedAfterTheNewerOne(bool gzip)
    {
        // Arrange
        using var harness = new ImportHarness();
        var newer = harness.Write("a/syslog", TestInputs.Log(8));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(newer);

        if (gzip)
        {
            harness.Write("b/syslog.gz", TestInputs.Gzip(TestInputs.Log(5)));
        }
        else
        {
            harness.Write("b/syslog", TestInputs.Log(5));
        }

        var batches = harness.Store.Batches.Count;
        var calls = harness.Store.LineHashCalls;
        var states = harness.Store.States.Count();

        harness.Events.Clear();

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();
        var finished = harness.Events.Single(item => item.Event == ProgressEvent.FileFinished);

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual(ImportOutcome.Contained, file.Outcome, "the older copy is contained");
        Assert.AreEqual("a/syslog", file.ContainedIn, "the display name of the first import");
        Assert.AreEqual(0L, file.Records, "no records");
        Assert.AreEqual(1, run.Summary.Count(ImportOutcome.Contained), "counted in the summary");
        Assert.HasCount(batches, harness.Store.Batches, "no batch is written");
        Assert.AreEqual(calls, harness.Store.LineHashCalls, "no line hashes are written");
        Assert.HasCount(states, harness.Store.States, "no state is created");
        Assert.AreEqual(TestInputs.Messages(1, 8), string.Join('|', harness.Store.Messages), "every line is stored once");
        Assert.AreEqual("a/syslog", finished.Result!.ContainedIn, "the progress event carries the container");
    }

    /// <summary>
    /// An older copy that comes later in the same input is recognized, and each line is stored once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterRecognizesOlderCopyLaterInTheSameRun()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a/syslog", TestInputs.Log(8));
        harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var newer = run.Summary.Files.Single(file => file.Path == "a/syslog");
        var older = run.Summary.Files.Single(file => file.Path == "b/syslog");

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, newer.Outcome, "the newer copy is imported");
        Assert.AreEqual(ImportOutcome.Contained, older.Outcome, "the older copy is contained");
        Assert.AreEqual("a/syslog", older.ContainedIn, "in the newer copy");
        Assert.AreEqual(TestInputs.Messages(1, 8), string.Join('|', harness.Store.Messages), "every line is stored once");
        Assert.ContainsSingle(harness.Store.States, "the older copy has no state");
    }

    /// <summary>
    /// An older copy is recognized whether or not the parser is line-framed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterRecognizesOlderCopyForAParserThatIsNotLineFramed()
    {
        // Arrange
        using var harness = new ImportHarness(false);
        var newer = harness.Write("a/syslog", TestInputs.Log(8));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(newer);
        harness.Write("b/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Contained, file.Outcome, "contained");
        Assert.AreEqual("a/syslog", file.ContainedIn, "in the first import");
        Assert.HasCount(8, harness.Store.Records, "nothing is stored again");
    }

    /// <summary>
    /// An older copy that ends inside a line is imported as a whole.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAnOlderCopyThatEndsInsideALineAsAWhole()
    {
        // Arrange
        using var harness = new ImportHarness();
        var newer = harness.Write("a/syslog", TestInputs.Log(8));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(newer);
        harness.Write("b/syslog", $"{TestInputs.Log(5)}LOG line 6 par");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(string.Empty, file.ContainedIn, "not contained");
        Assert.AreEqual(6L, file.Records, "every line is stored");
    }

    /// <summary>
    /// An older copy that differs from the newer one in one byte before its end is imported as a whole.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsAnOlderCopyThatDiffersInOneByteAsAWhole()
    {
        // Arrange
        using var harness = new ImportHarness();
        var newer = harness.Write("a/syslog", TestInputs.Log(8));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(newer);
        harness.Write("b/syslog", TestInputs.Log(5).Replace("line 3", "lime 3", StringComparison.Ordinal));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(5L, file.Records, "every line is stored");
    }

    /// <summary>
    /// An older copy is imported as a whole when the larger content is not complete: interrupted in an earlier run, or failed in the same run.
    /// </summary>
    /// <param name="how">How the larger content is not complete</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("interrupted")]
    [DataRow("failed in the same run")]
    public async Task ImporterImportsAnOlderCopyAsAWholeWhenTheLargerContentIsNotComplete(string how)
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();

        if (how == "interrupted")
        {
            var newer = harness.Write("a/syslog", TestInputs.Log(7));

            harness.Store.AfterBatch = count =>
                                       {
                                           if (count == 1)
                                           {
                                               interrupt.Cancel();
                                           }
                                       };
            await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
            harness.Store.AfterBatch = null;
            File.Delete(newer);
            harness.Write("b/syslog", TestInputs.Log(4));
        }
        else
        {
            harness.Write("a/syslog", "LOG line 1\nLOG line 2\nboom\nLOG line 4\n");
            harness.Write("b/syslog", "LOG line 1\nLOG line 2\n");
        }

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single(item => item.Path == "b/syslog");

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "imported as a whole");
        Assert.AreEqual(string.Empty, file.ContainedIn, "not contained");
        Assert.AreEqual(how == "interrupted" ? 4L : 2L, file.Records, "every line of the copy is stored");
    }

    /// <summary>
    /// A content with the hash of a complete stored content is already imported even if it is the start of a larger complete content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterKeepsAlreadyImportedForTheSameContentThatIsTheStartOfALargerOne()
    {
        // Arrange
        using var harness = new ImportHarness();
        var older = harness.Write("a/syslog", TestInputs.Log(5));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(older);
        harness.Write("b/syslog", TestInputs.Log(8));
        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(Path.Combine(harness.Directory.Path, "b/syslog"));
        harness.Write("c/syslog", TestInputs.Log(5));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.AlreadyImported, file.Outcome, "already imported");
        Assert.AreEqual(string.Empty, file.ContainedIn, "not contained");
    }

    /// <summary>
    /// A content with the hash of an incomplete stored content is resumed even if it is the start of a larger complete content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterResumesTheSameContentThatIsTheStartOfALargerOne()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var older = harness.Write("a/syslog", TestInputs.Log(7));

        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 1)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };
        await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
        harness.Store.AfterBatch = null;
        File.Delete(older);
        harness.Write("b/syslog", TestInputs.Log(9));
        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(Path.Combine(harness.Directory.Path, "b/syslog"));
        harness.Write("c/syslog", TestInputs.Log(7));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, file.Outcome, "resumed and completed");
        Assert.AreEqual(string.Empty, file.ContainedIn, "not contained");
        Assert.AreEqual(2L, file.ResumedAfter, "after the records of the first run");
    }

    /// <summary>
    /// A content that continues a smaller base and is also the start of a larger complete content is contained.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterPrefersContainmentOverContinuation()
    {
        // Arrange
        using var harness = new ImportHarness();
        var small = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(small);

        var large = harness.Write("b/syslog", TestInputs.Log(10));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(large);
        harness.Write("c/syslog", TestInputs.Log(6));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.IsNotEmpty(harness.Store.Prefixes[^1], "the smaller content was offered as a base");
        Assert.AreEqual(ImportOutcome.Contained, file.Outcome, "contained, not continued");
        Assert.AreEqual("b/syslog", file.ContainedIn, "in the larger content");
        Assert.AreEqual(string.Empty, file.ContinuedFrom, "continues nothing");
        Assert.AreEqual(TestInputs.Messages(1, 10), string.Join('|', harness.Store.Messages), "every line is stored once");
    }

    /// <summary>
    /// Importing a contained older copy again gives the same result with no state and no record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterReportsAContainedOlderCopyAgainInALaterRun()
    {
        // Arrange
        using var harness = new ImportHarness();
        var newer = harness.Write("a/syslog", TestInputs.Log(8));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(newer);
        harness.Write("b/syslog", TestInputs.Log(5));
        await harness.RunAsync(TestContext.CancellationToken);

        var batches = harness.Store.Batches.Count;

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Contained, file.Outcome, "contained again");
        Assert.AreEqual("a/syslog", file.ContainedIn, "in the same import");
        Assert.HasCount(batches, harness.Store.Batches, "no batch");
        Assert.ContainsSingle(harness.Store.States, "no state");
    }

    /// <summary>
    /// A store result that carries only the container ends the file as contained, before the completeness, the source type or the file ID is looked at, and nothing carrying a file ID is sent.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterEndsAFileAsContainedWhenTheStoreReturnsOnlyTheContainer()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a/syslog", TestInputs.Log(3));
        harness.Store.ScriptedBeginResult = new ImportFile
                                            {
                                                ContainedIn = "other/name"
                                            };

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual(ImportOutcome.Contained, file.Outcome, "contained, not already imported and not failed");
        Assert.AreEqual("other/name", file.ContainedIn, "the container of the store");
        Assert.AreEqual(string.Empty, file.Reason, "no reason");
        Assert.IsEmpty(harness.Store.Batches, "no batch");
        Assert.AreEqual(0, harness.Store.LineHashCalls, "no line hashes");
        Assert.IsEmpty(harness.Store.FileIdCalls, "no call carries a file ID");
        Assert.IsEmpty(harness.Parser.Seen, "nothing is parsed");
    }

    /// <summary>
    /// A complete state with an empty container is already imported.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterEndsAFileAsAlreadyImportedWhenTheStoreReturnsACompleteState()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a/syslog", TestInputs.Log(3));
        harness.Store.ScriptedBeginResult = new ImportFile
                                            {
                                                Id = 5,
                                                SourceType = "test",
                                                Complete = true
                                            };

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.AlreadyImported, file.Outcome, "already imported");
        Assert.AreEqual(string.Empty, file.ContainedIn, "not contained");
    }

    /// <summary>
    /// Every imported content stores the first 16 bytes of the SHA-256 up to every line end, whatever the parser, the packing and the continuation point; the rows cover the whole content, and a content without a line feed has none.
    /// </summary>
    /// <param name="lineFramed">Whether the parser is line-framed</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImporterStoresLineHashesOfEveryImportedContent(bool lineFramed)
    {
        // Arrange
        using var harness = new ImportHarness(lineFramed);
        var gzipped = TestInputs.Log(3, "LOG gz");

        harness.Write("a.log", TestInputs.Log(5, "LOG a"));
        harness.Write("b.log.gz", TestInputs.Gzip(gzipped));
        harness.Write("c.log", "LOG only-part");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var plain = harness.Store.States.Single(state => state.Name == "a.log");
        var packed = harness.Store.States.Single(state => state.Name == "b.log.gz");
        var cut = harness.Store.States.Single(state => state.Name == "c.log");

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual(ExpectedLineHashes.Describe(Expected(TestInputs.Bytes(TestInputs.Log(5, "LOG a")), plain)), ExpectedLineHashes.Describe(harness.Store.LineHashes.Where(row => row.FileId == plain.Id)), "rows of the plain file");
        Assert.AreEqual(ExpectedLineHashes.Describe(Expected(TestInputs.Bytes(gzipped), packed)), ExpectedLineHashes.Describe(harness.Store.LineHashes.Where(row => row.FileId == packed.Id)), "rows of the decompressed content");
        Assert.IsEmpty(harness.Store.LineHashes.Where(row => row.FileId == cut.Id), "a content without a line feed has no row");
    }

    /// <summary>
    /// A continued file stores the line hashes of its whole content, the part before the continuation point included.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterStoresLineHashesOfTheWholeContentOfAContinuedFile()
    {
        // Arrange
        using var harness = new ImportHarness();
        var earlier = harness.Write("a/syslog", TestInputs.Log(3));

        await harness.RunAsync(TestContext.CancellationToken);
        File.Delete(earlier);
        harness.Write("b/syslog", TestInputs.Log(9));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var continued = harness.Store.States.Single(state => state.Name == "b/syslog");

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, run.Summary.Files.Single().Outcome, "continued");
        Assert.AreEqual(ExpectedLineHashes.Describe(Expected(TestInputs.Bytes(TestInputs.Log(9)), continued)), ExpectedLineHashes.Describe(harness.Store.LineHashes.Where(row => row.FileId == continued.Id)), "rows of the whole content");
    }

    /// <summary>
    /// Every step the importer writes carries the attempt of its state, an interrupted import that is resumed ends with the line hashes of its completing attempt only, and the rows cover the whole content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterSendsTheAttemptOfTheStateAndKeepsTheRowsOfTheCompletingAttempt()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var content = TestInputs.Bytes(TestInputs.Log(9000));

        harness.Write("a.log", content);
        harness.Store.AfterBatch = count =>
                                   {
                                       if (count == 3000)
                                       {
                                           interrupt.Cancel();
                                       }
                                   };

        // Act
        var first = await harness.RunAsync(interrupt.Token, null, options => options.BatchRecords = 2);
        var firstBatches = harness.Store.Batches.Count;
        var firstRows = harness.Store.LineHashWrites.Count;

        harness.Store.AfterBatch = null;

        var second = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 2);
        var state = harness.Store.States.Single();

        // Assert
        Assert.IsTrue(first.Summary.Interrupted, "the first run is interrupted");
        Assert.IsGreaterThan(0, firstRows, "the first run wrote a full row");
        Assert.IsTrue(harness.Store.Batches.Take(firstBatches).All(batch => batch.Import!.Attempt == 1), "the steps of the first run carry attempt 1");
        Assert.IsTrue(harness.Store.Batches.Skip(firstBatches).All(batch => batch.Import!.Attempt == 2), "the steps of the second run carry attempt 2");
        Assert.IsTrue(harness.Store.LineHashWrites.Take(firstRows).All(row => row.Attempt == 1), "the first run wrote rows of attempt 1");
        Assert.AreEqual(ImportOutcome.Imported, second.Summary.Files.Single().Outcome, "the second run completes the file");
        Assert.AreEqual(2L, state.Attempt, "the state is at attempt 2");
        Assert.AreEqual(ExpectedLineHashes.Describe(Expected(content, state, 2)), ExpectedLineHashes.Describe(harness.Store.LineHashes), "only the rows of the completing attempt are held, for the whole content");
        Assert.IsTrue(harness.Store.Batches[^1].Import!.Complete, "the last batch completes the file");
    }

    /// <summary>
    /// A failed write of line hashes fails the file, in every phase of pass 2: a conflict with the reason of the state, any other store error with "database error" and the end of the run, a cancellation with "interrupted". The completing batch is never written, and nothing escapes.
    /// </summary>
    /// <param name="phase">Where the write happens</param>
    /// <param name="kind">What goes wrong</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("skip", "conflict")]
    [DataRow("skip", "store")]
    [DataRow("skip", "cancel")]
    [DataRow("parse", "conflict")]
    [DataRow("parse", "store")]
    [DataRow("parse", "cancel")]
    [DataRow("verification", "conflict")]
    [DataRow("verification", "store")]
    [DataRow("verification", "cancel")]
    [DataRow("flush", "conflict")]
    [DataRow("flush", "store")]
    [DataRow("flush", "cancel")]
    public async Task ImporterFailsAFileWhenAWriteOfLineHashesFails(string phase, string kind)
    {
        // Arrange
        using var harness = new ImportHarness();
        using var interrupt = new CancellationTokenSource();
        var parser = new ProbeParser("test", phase == "verification" ? 1 : 0);
        var registry = new ParserRegistry([parser]);
        var target = phase == "skip" ? "b/syslog" : "a/syslog";
        var evidence = string.Empty;

        if (phase == "skip")
        {
            var earlier = harness.Write("a/syslog", TestInputs.Log(12000));

            await harness.RunAsync(TestContext.CancellationToken, null, options => options.Parsers = registry);
            File.Delete(earlier);
            harness.Write(target, TestInputs.Log(12100));
        }
        else
        {
            harness.Write(target, TestInputs.Log(phase == "flush" ? 10 : 9000));
        }

        var armed = harness.Store.LineHashCalls + 1;
        var batches = harness.Store.Batches.Count;

        harness.Store.FailFromLineHashCall = armed;
        harness.Store.OnLineHashes = count =>
                                     {
                                         if (count == armed)
                                         {
                                             evidence = $"started={parser.Started} completed={parser.Completed}";

                                             if (kind == "cancel")
                                             {
                                                 interrupt.Cancel();
                                             }
                                         }
                                     };
        harness.Store.LineHashFailure = Failure(kind);

        // Act
        var run = await harness.RunAsync(interrupt.Token, null, options => options.Parsers = registry);
        var file = run.Summary.Files.Single(item => item.Path == target);

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, file.Outcome, $"{phase}, {kind}: the file fails");
        Assert.DoesNotContain(batch => batch.Import!.Complete, harness.Store.Batches.Skip(batches), $"{phase}, {kind}: the file is never completed");
        Assert.IsFalse(harness.Store.States.Last().Complete, $"{phase}, {kind}: the state is not complete");
        Assert.AreEqual(phase == "parse" ? "started=1 completed=0" : "started=1 completed=1", evidence, $"{phase}, {kind}: the write happened in this phase");

        switch (kind)
        {
            case "conflict":
                {
                    Assert.AreEqual("the import state changed (another import of the same content is running?)", file.Reason, "conflict reason");
                    Assert.IsNull(run.Error, "a conflict does not end the run");
                }
                break;

            case "store":
                {
                    Assert.AreEqual("database error", file.Reason, "store error reason");
                    Assert.IsInstanceOfType<ImportException>(run.Error, "a store error ends the run");
                    Assert.AreEqual("importer: writing to the database failed", run.Error?.Message, "message without the text of the store");
                }
                break;

            default:
                {
                    Assert.AreEqual("interrupted", file.Reason, "cancellation reason");
                    Assert.IsInstanceOfType<OperationCanceledException>(run.Error, "a cancellation ends the run");
                    Assert.IsTrue(run.Summary.Interrupted, "the run is interrupted");
                }
                break;
        }
    }

    /// <summary>
    /// After a failed batch, a parser that catches the failure and keeps reading causes no write of line hashes: the error of the store is sticky.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterWritesNoLineHashesAfterAFailedBatchWhenTheParserKeepsReading()
    {
        // Arrange
        using var harness = new ImportHarness();
        var parser = new SwallowingParser("test");

        harness.Write("a.log", TestInputs.Log(9000));
        harness.Store.WriteFailure = new StoreException("store: writing batch: disk full");
        harness.Store.FailFromBatch = 1;

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options =>
                                         {
                                             options.Parsers = new ParserRegistry([parser]);
                                             options.BatchRecords = 1000;
                                         });

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, run.Summary.Files.Single().Outcome, "the file fails");
        Assert.IsGreaterThan(0, parser.Caught, "the parser caught the failed batch and kept reading");
        Assert.IsGreaterThan(4096L, parser.LinesRead, "the parser read past the first row of line hashes");
        Assert.AreEqual(0, harness.Store.LineHashCalls, "no line hashes follow the failed batch");
    }

    /// <summary>
    /// The last partial row of line hashes is written after the verification read and before the completing batch, and exactly one batch completes the file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterWritesTheLastRowOfLineHashesBeforeTheCompletingBatch()
    {
        // Arrange
        using var harness = new ImportHarness();
        var events = new List<string>();

        harness.Write("a.log", TestInputs.Log(9000));
        harness.Store.OnLineHashes = _ => events.Add("rows");
        harness.Store.AfterBatch = _ => events.Add(harness.Store.Batches[^1].Import!.Complete ? "complete" : "batch");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 1000);

        // Assert
        Assert.AreEqual(ImportOutcome.Imported, run.Summary.Files.Single().Outcome, "imported");
        Assert.ContainsSingle(item => item == "complete", events, "one completing batch");
        Assert.AreEqual("complete", events[^1], "the completing batch is last");
        Assert.AreEqual("rows", events[^2], "the last row is written right before it");
        Assert.AreEqual(3, events.Count(item => item == "rows"), "two full rows and the last partial one");
    }

    /// <summary>
    /// A file that changed between the passes gets no completing batch and no partial row of line hashes.
    /// </summary>
    /// <param name="lines">The number of lines of the file</param>
    /// <param name="rows">The number of full rows written while the file was parsed</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(5, 0)]
    [DataRow(9000, 2)]
    public async Task ImporterWritesNoCompletingBatchAndNoLastRowForAChangedFile(int lines, int rows)
    {
        // Arrange
        using var harness = new ImportHarness();
        var path = harness.Write("a.log", TestInputs.Log(lines));
        var changed = TestInputs.Log(lines).Replace($"line {lines}\n", $"lime {lines}\n", StringComparison.Ordinal);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken,
                                         null,
                                         options => options.Progress = progress =>
                                                                       {
                                                                           if (progress.Event == ProgressEvent.Scanned)
                                                                           {
                                                                               File.WriteAllText(path, changed);
                                                                           }
                                                                       });
        var file = run.Summary.Files.Single();

        // Assert
        Assert.AreEqual(ImportOutcome.Failed, file.Outcome, "failed");
        Assert.AreEqual("the content changed while it was imported", file.Reason, "reason");
        Assert.DoesNotContain(batch => batch.Import!.Complete, harness.Store.Batches, "no completing batch");
        Assert.AreEqual(rows, harness.Store.LineHashCalls, "only full rows were written, the last partial row was not");
    }

    /// <summary>
    /// A file that fails in pass 2 on every run sends every row of line hashes and every step with the attempt the store returned for that run, and afterwards the store holds only the rows of the last attempt.
    /// </summary>
    /// <param name="how">How the file fails</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("content changed")]
    [DataRow("parser error")]
    public async Task ImporterKeepsOnlyTheLineHashesOfTheLatestAttemptOfAFileThatFailsEveryRun(string how)
    {
        // Arrange
        using var harness = new ImportHarness();
        var content = how == "parser error" ? $"{TestInputs.Log(6000)}boom\n" : TestInputs.Log(9000);
        var changed = content.Replace("line 9000\n", "lime 9000\n", StringComparison.Ordinal);
        var path = Path.Combine(harness.Directory.Path, "a.log");
        var batchesBefore = 0;
        var rowsBefore = 0;

        // Act and Assert
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            harness.Write("a.log", content);

            var run = await harness.RunAsync(TestContext.CancellationToken,
                                             null,
                                             options => options.Progress = progress =>
                                                                           {
                                                                               if (how == "content changed" && progress.Event == ProgressEvent.Scanned)
                                                                               {
                                                                                   File.WriteAllText(path, changed);
                                                                               }
                                                                           });

            Assert.AreEqual(ImportOutcome.Failed, run.Summary.Files.Single().Outcome, $"run {attempt} fails");
            Assert.IsGreaterThan(rowsBefore, harness.Store.LineHashWrites.Count, $"run {attempt} writes rows");
            Assert.IsTrue(harness.Store.LineHashWrites.Skip(rowsBefore).All(row => row.Attempt == attempt), $"run {attempt}: every row carries the attempt the store returned");
            Assert.IsTrue(harness.Store.Batches.Skip(batchesBefore).All(batch => batch.Import!.Attempt == attempt), $"run {attempt}: every step carries the attempt the store returned");
            batchesBefore = harness.Store.Batches.Count;
            rowsBefore = harness.Store.LineHashWrites.Count;
        }

        Assert.AreEqual(3L, harness.Store.States.Single().Attempt, "the store counted three attempts");
        Assert.IsNotEmpty(harness.Store.Batches, "a step was written in the first run");
        Assert.IsTrue(harness.Store.LineHashes.All(row => row.Attempt == 3), "only the rows of attempt 3 are held");
    }

    /// <summary>
    /// Creates the failure of the store the case describes.
    /// </summary>
    /// <param name="kind">The kind: <c>conflict</c>, <c>store</c> or anything else for none</param>
    /// <returns>The exception, or <c>null</c> when the case cancels instead</returns>
    private static Exception? Failure(string kind)
    {
        if (kind == "conflict")
        {
            return new ImportConflictException("store: import state changed: file 1 is complete, unknown or was begun again");
        }

        return kind == "store" ? new StoreException("store: adding line hashes: disk full") : null;
    }

    /// <summary>
    /// Returns the rows of line hashes a content must have, for the state the store holds for it.
    /// </summary>
    /// <param name="content">The content</param>
    /// <param name="state">The state</param>
    /// <param name="attempt">The attempt of the rows</param>
    /// <returns>The rows with file ID and attempt</returns>
    private static List<ImportLineHashes> Expected(byte[] content, ImportFile state, long attempt = 1)
    {
        var rows = ExpectedLineHashes.Rows(content);

        foreach (var row in rows)
        {
            row.FileId = state.Id;
            row.Attempt = attempt;
        }

        return rows;
    }

    #endregion // Methods
}