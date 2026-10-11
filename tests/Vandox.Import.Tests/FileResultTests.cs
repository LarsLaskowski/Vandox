namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="FileResult"/>
/// </summary>
[TestClass]
public class FileResultTests
{
    #region Methods

    /// <summary>
    /// A snapshot copies the fields of a continued file.
    /// </summary>
    [TestMethod]
    public void FileResultSnapshotCopiesTheContinuationFields()
    {
        // Arrange
        var result = new FileResult
                     {
                         Path = "b/syslog",
                         Outcome = ImportOutcome.Continued,
                         ContinuedFrom = "a/syslog",
                         ContinuedAfter = 3,
                         SkippedAtStart = 2,
                         Lines = 7,
                         Records = 5
                     };

        // Act
        var copy = result.Snapshot();

        result.ContinuedFrom = "changed";
        result.ContinuedAfter = 99;
        result.SkippedAtStart = 99;

        // Assert
        Assert.AreEqual(ImportOutcome.Continued, copy.Outcome, "outcome");
        Assert.AreEqual("a/syslog", copy.ContinuedFrom, "continued from");
        Assert.AreEqual(3L, copy.ContinuedAfter, "continued after");
        Assert.AreEqual(2L, copy.SkippedAtStart, "skipped at start");
        Assert.AreEqual(7L, copy.Lines, "lines");
        Assert.AreEqual(5L, copy.Records, "records");
    }

    /// <summary>
    /// A snapshot copies the container of a contained file.
    /// </summary>
    [TestMethod]
    public void FileResultSnapshotCopiesTheContainer()
    {
        // Arrange
        var result = new FileResult
                     {
                         Path = "b/syslog",
                         Outcome = ImportOutcome.Contained,
                         ContainedIn = "a/syslog"
                     };

        // Act
        var copy = result.Snapshot();

        result.ContainedIn = "changed";

        // Assert
        Assert.AreEqual(ImportOutcome.Contained, copy.Outcome, "outcome");
        Assert.AreEqual("a/syslog", copy.ContainedIn, "contained in");
        Assert.AreEqual(string.Empty, new FileResult().ContainedIn, "a new result is not contained");
    }

    /// <summary>
    /// A snapshot copies the number of raw lines and their reasons.
    /// </summary>
    [TestMethod]
    public void FileResultSnapshotCopiesTheRawLines()
    {
        // Arrange
        var result = new FileResult
                     {
                         Path = "nginx/error.log",
                         Raw = 3
                     };

        result.RawProblems.Add(new ImportProblem(4, "not an nginx error log line"));
        result.RawProblems.Add(new ImportProblem(9, "invalid date"));

        // Act
        var copy = result.Snapshot();

        result.Raw = 99;
        result.RawProblems.Clear();

        // Assert
        Assert.AreEqual(3L, copy.Raw, "raw lines");
        Assert.HasCount(2, copy.RawProblems, "both reasons are copied");
        Assert.AreEqual(new ImportProblem(4, "not an nginx error log line"), copy.RawProblems[0], "first reason");
        Assert.AreEqual(new ImportProblem(9, "invalid date"), copy.RawProblems[1], "second reason");
        Assert.AreEqual(0L, new FileResult().Raw, "a new result has no raw lines");
    }

    #endregion // Methods
}