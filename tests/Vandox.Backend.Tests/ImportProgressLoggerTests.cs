using Microsoft.Extensions.Logging;

using Vandox.Backend.Cli;
using Vandox.Import;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ImportProgressLogger"/>
/// </summary>
[TestClass]
public class ImportProgressLoggerTests
{
    #region Methods

    /// <summary>
    /// The outcome of a continued file is logged as <c>continued</c>.
    /// </summary>
    [TestMethod]
    public void ImportProgressLoggerLogsTheOutcomeOfAContinuedFileAsContinued()
    {
        // Arrange
        var logger = new ListLogger();
        var progress = new ImportProgress
                       {
                           Event = ProgressEvent.FileFinished,
                           Result = new FileResult
                                    {
                                        Path = "b/syslog",
                                        SourceType = "syslog",
                                        Outcome = ImportOutcome.Continued,
                                        Lines = 2,
                                        Records = 2,
                                        ContinuedFrom = "a/syslog",
                                        ContinuedAfter = 3
                                    }
                       };

        // Act
        ImportProgressLogger.Log(logger, progress);

        // Assert
        Assert.AreEqual("file finished b/syslog continued syslog 2 2 0", logger.Messages.Single(), "the file finished event names the outcome");
    }

    /// <summary>
    /// The outcome of a contained file is logged as <c>contained</c>.
    /// </summary>
    [TestMethod]
    public void ImportProgressLoggerLogsTheOutcomeOfAContainedFileAsContained()
    {
        // Arrange
        var logger = new ListLogger();
        var progress = new ImportProgress
                       {
                           Event = ProgressEvent.FileFinished,
                           Result = new FileResult
                                    {
                                        Path = "b/syslog",
                                        SourceType = "syslog",
                                        Outcome = ImportOutcome.Contained,
                                        ContainedIn = "a/syslog"
                                    }
                       };

        // Act
        ImportProgressLogger.Log(logger, progress);

        // Assert
        Assert.AreEqual("file finished b/syslog contained syslog 0 0 0", logger.Messages.Single(), "the file finished event names the outcome");
    }

    /// <summary>
    /// A finished file with raw lines logs a warning with its path, source type and the number of raw lines after the <c>file finished</c> message.
    /// </summary>
    [TestMethod]
    public void ImportProgressLoggerWarnsAboutTheRawLinesOfAFinishedFile()
    {
        // Arrange
        var logger = new ListLogger();
        var progress = new ImportProgress
                       {
                           Event = ProgressEvent.FileFinished,
                           Result = new FileResult
                                    {
                                        Path = "nginx/error.log",
                                        SourceType = "nginx-error",
                                        Outcome = ImportOutcome.Imported,
                                        Lines = 6,
                                        Records = 6,
                                        Raw = 3
                                    }
                       };

        // Act
        ImportProgressLogger.Log(logger, progress);

        // Assert
        Assert.AreSequenceEqual(["file finished nginx/error.log imported nginx-error 6 6 0", "raw lines kept nginx/error.log nginx-error 3"], logger.Messages, "the warning follows the file finished message");
        Assert.AreEqual(LogLevel.Warning, logger.Entries[1].Level, "level of the warning");
        Assert.AreEqual(29, logger.Entries[1].EventId.Id, "event ID of the warning");
    }

    #endregion // Methods
}