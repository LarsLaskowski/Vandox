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

    #endregion // Methods
}