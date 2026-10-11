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

    #endregion // Methods
}