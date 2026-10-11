using Vandox.Backend.Cli;
using Vandox.Import;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ImportSummaryWriter"/>
/// </summary>
[TestClass]
public class ImportSummaryWriterTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The count row of continued files follows the imported row, and a continued file is listed with its earlier name and line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterListsContinuedFilesAndCountsThem()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Plain("a/syslog", ImportOutcome.Imported));
        summary.Files.Add(Continued("b/syslog", "a/syslog", 3, 0));
        summary.Files.Add(Continued("c/syslog", "b/syslog", 5, 0));
        summary.Files.Add(Plain("note.txt", ImportOutcome.Unrecognized));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  imported:          1\n  continued:         2\n  already imported:", text, "the count row follows the imported row");
        Assert.Contains("\nFiles continued:\n  \"b/syslog\": continues \"a/syslog\" after line 3\n  \"c/syslog\": continues \"b/syslog\" after line 5\n", text, "the section lists every continued file");
        Assert.IsLessThan(text.IndexOf("Files not recognized", StringComparison.Ordinal), text.IndexOf("Files continued", StringComparison.Ordinal), "the section comes before the files not recognized");
    }

    /// <summary>
    /// A continued file with lines skipped before its first record names them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterNamesTheLinesSkippedBeforeTheFirstRecord()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Continued("b/mariadb.err", "a/mariadb.err", 3, 2));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  \"b/mariadb.err\": continues \"a/mariadb.err\" after line 3, skipped 2 lines before its first record\n", text, "the line names the skipped lines");
    }

    /// <summary>
    /// Without a continued file the count row says 0 and there is no section.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterPrintsNoSectionWithoutContinuedFiles()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Plain("a/syslog", ImportOutcome.Imported));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  imported:          1\n  continued:         0\n", text, "the count row says 0");
        Assert.DoesNotContain("Files continued", text, "no section");
    }

    /// <summary>
    /// A path or name with a control character is quoted and escaped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterQuotesThePathAndTheEarlierName()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Continued("b\nsyslog", "a\u001b[31msyslog", 3, 0));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  \"b\\nsyslog\": continues \"a\\u001b[31msyslog\" after line 3\n", text, "both are escaped");
        Assert.DoesNotContain("\u001b", text, "no escape character reaches the output");
    }

    /// <summary>
    /// The count row of contained files follows the already imported row, and the section lists every contained file in input order with its container, after the continued files and before the files not recognized.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterListsContainedFilesAndCountsThem()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Plain("a/syslog", ImportOutcome.Imported));
        summary.Files.Add(Contained("b/syslog", "a/syslog"));
        summary.Files.Add(Continued("c/syslog", "b/syslog", 5, 0));
        summary.Files.Add(Contained("d/syslog.gz", "a/syslog"));
        summary.Files.Add(Plain("note.txt", ImportOutcome.Unrecognized));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  already imported:  0\n  contained:         2\n  not recognized:", text, "the count row follows the already imported row");
        Assert.Contains("\nFiles contained in an imported file:\n  \"b/syslog\": start of \"a/syslog\"\n  \"d/syslog.gz\": start of \"a/syslog\"\n", text, "the section lists every contained file in input order");
        Assert.IsLessThan(text.IndexOf("Files contained in an imported file", StringComparison.Ordinal), text.IndexOf("Files continued", StringComparison.Ordinal), "the section follows the continued files");
        Assert.IsLessThan(text.IndexOf("Files not recognized", StringComparison.Ordinal), text.IndexOf("Files contained in an imported file", StringComparison.Ordinal), "the section comes before the files not recognized");
    }

    /// <summary>
    /// Without a contained file the count row says 0 and there is no section.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterPrintsNoSectionWithoutContainedFiles()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Plain("a/syslog", ImportOutcome.Imported));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  already imported:  0\n  contained:         0\n", text, "the count row says 0");
        Assert.DoesNotContain("Files contained in an imported file", text, "no section");
    }

    /// <summary>
    /// The path and the container of a contained file are quoted and escaped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportSummaryWriterQuotesThePathAndTheContainerOfAContainedFile()
    {
        // Arrange
        var summary = new ImportSummary
                      {
                          Root = "/logs"
                      };

        summary.Files.Add(Contained("b\nsyslog", "a\u001b[31msyslog"));

        // Act
        var text = await WriteAsync(summary);

        // Assert
        Assert.Contains("  \"b\\nsyslog\": start of \"a\\u001b[31msyslog\"\n", text, "both are escaped");
        Assert.DoesNotContain("\u001b", text, "no escape character reaches the output");
    }

    /// <summary>
    /// Creates the result of a file that is not continued.
    /// </summary>
    /// <param name="path">The display path</param>
    /// <param name="outcome">The outcome</param>
    /// <returns>The result</returns>
    private static FileResult Plain(string path, ImportOutcome outcome)
    {
        return new FileResult
               {
                   Path = path,
                   Outcome = outcome,
                   Reason = outcome == ImportOutcome.Unrecognized ? "no parser recognized the file" : string.Empty
               };
    }

    /// <summary>
    /// Creates the result of a contained file.
    /// </summary>
    /// <param name="path">The display path</param>
    /// <param name="container">The display name of the first import of the content it is the start of</param>
    /// <returns>The result</returns>
    private static FileResult Contained(string path, string container)
    {
        return new FileResult
               {
                   Path = path,
                   Outcome = ImportOutcome.Contained,
                   ContainedIn = container
               };
    }

    /// <summary>
    /// Creates the result of a continued file.
    /// </summary>
    /// <param name="path">The display path</param>
    /// <param name="from">The display name of the earlier content</param>
    /// <param name="after">The lines before the continuation point</param>
    /// <param name="skippedAtStart">The lines skipped before the first record</param>
    /// <returns>The result</returns>
    private static FileResult Continued(string path, string from, long after, long skippedAtStart)
    {
        return new FileResult
               {
                   Path = path,
                   Outcome = ImportOutcome.Continued,
                   ContinuedFrom = from,
                   ContinuedAfter = after,
                   SkippedAtStart = skippedAtStart
               };
    }

    /// <summary>
    /// Writes a summary to text.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <returns>A task that returns the text</returns>
    private static async Task<string> WriteAsync(ImportSummary summary)
    {
        await using var writer = new StringWriter();

        await ImportSummaryWriter.WriteAsync(writer, summary);

        return writer.ToString();
    }

    #endregion // Methods
}