using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MariaDbEventClassifier"/>
/// </summary>
[TestClass]
public class MariaDbEventClassifierTests
{
    #region Constants

    private const string StartNew = "Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 2345";
    private const string StartOld = "/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1 ...";
    private const string RecoveryStart = "InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234";
    private const string Started = "InnoDB: 10.6.12 started; log sequence number 1; transaction id 2";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The event is chosen by the level and the message of the header; an empty text is none.
    /// </summary>
    /// <param name="level">The level</param>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected event, or an empty text for none</param>
    [TestMethod]
    [DataRow("Note", "Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 2345", "mariadb.start")]
    [DataRow("Note", "Starting MariaDB 10.6.22-MariaDB-0ubuntu0.22.04.1 source revision 3d0a5b1c server_uid 7mT4hXc0QvG2kz9pW8sYbN1eJ+U= as process 2345", "mariadb.start")]
    [DataRow("Note", "/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234 ...", "mariadb.start")]
    [DataRow("Note", "/usr/sbin/mariadbd (server 10.6.11-MariaDB-0ubuntu0.22.04.1 as 10.6.11-custom) starting as process 1234 ...", "mariadb.start")]
    [DataRow("Note", "/usr/sbin/mysqld (server 10.6.7-MariaDB) starting as process 1 ...", "mariadb.start")]
    [DataRow("Note", "/usr/sbin/mariadbd: ready for connections.", "mariadb.ready")]
    [DataRow("Note", "/usr/sbin/mariadbd (initiated by: unknown): Normal shutdown", "mariadb.shutdown")]
    [DataRow("Note", "/usr/sbin/mariadbd (initiated by: root[root] @ localhost []): Normal shutdown", "mariadb.shutdown")]
    [DataRow("Note", "/usr/sbin/mariadbd: Shutdown complete", "mariadb.shutdown_complete")]
    [DataRow("ERROR", "mysqld got signal 6 ;", "mariadb.abort")]
    [DataRow("ERROR", "/usr/sbin/mariadbd got signal 11 ;", "mariadb.abort")]
    [DataRow("Note", "InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234", "mariadb.recovery_start")]
    [DataRow("Note", "InnoDB: Starting crash recovery.", "mariadb.recovery_start")]
    [DataRow("Note", "Starting table crash recovery...", "mariadb.recovery_start")]
    [DataRow("Note", "Crash table recovery finished.", "mariadb.recovery_end")]
    [DataRow("Note", "InnoDB: Starting shutdown...", "")]
    [DataRow("Note", "InnoDB: Shutdown completed; log sequence number 8394829; transaction id 5600", "")]
    [DataRow("Warning", "Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)", "")]
    [DataRow("ERROR", "/usr/sbin/mariadbd: ready for connections.", "")]
    [DataRow("Warning", "mysqld got signal 6 ;", "")]
    [DataRow("ERROR", "mysqld got signal ;", "")]
    [DataRow("ERROR", "mysqld got signal 6", "")]
    [DataRow("ERROR", "mysqld got signal 6 ; x", "")]
    [DataRow("ERROR", "mysqld got signal 1234 ;", "")]
    [DataRow("ERROR", " got signal 6 ;", "")]
    [DataRow("Note", "mysqld did an expected abort", "")]
    [DataRow("Note", "starting MariaDB 10.6.12 as process 1", "")]
    [DataRow("Note", "Starting MariaDB 10.6.12", "")]
    [DataRow("Note", "/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234", "")]
    [DataRow("Note", "/usr/sbin/mariadbd starting as process 1234 ...", "")]
    [DataRow("Note", " (server 10.6.7) starting as process 1 ...", "")]
    [DataRow("Note", "/usr/sbin/mariadbd (server 10.6.7) starting as process 1 ... x", "")]
    [DataRow("Note", "/usr/sbin/mysqld (mysqld 5.7.44) starting as process 1 ...", "")]
    [DataRow("Warning", "/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234 ...", "")]
    [DataRow("Note", "InnoDB: Starting final batch to recover 210 pages from redo log.", "")]
    [DataRow("Note", "InnoDB: 10.6.12 started; log sequence number 8401234; transaction id 5678", "")]
    public void MariaDbEventClassifierClassifyChoosesTheEventByLevelAndMessage(string level, string message, string expected)
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        var result = classifier.Classify(Header(level, message));

        // Assert
        Assert.AreEqual(expected, result, "event");
    }

    /// <summary>
    /// A header without a level has no event, whatever its text says.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("mysqld_safe", "Starting mariadbd daemon with databases from /var/lib/mysql")]
    [DataRow("mysqld_safe", "Starting MariaDB 10.6.12 source revision  as process 1")]
    [DataRow("", "InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836")]
    [DataRow("", "/usr/sbin/mariadbd: ready for connections.")]
    [DataRow("", "/usr/sbin/mariadbd (initiated by: unknown): Normal shutdown")]
    [DataRow("", "mysqld got signal 6 ;")]
    [DataRow("", "InnoDB: Starting crash recovery from checkpoint LSN=1,1")]
    public void MariaDbEventClassifierClassifyGivesNoEventWithoutALevel(string program, string message)
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();
        var line = new MariaDbLine
                   {
                       Program = program,
                       Message = message
                   };

        // Act
        var result = classifier.Classify(line);

        // Assert
        Assert.AreEqual(string.Empty, result, "event");
    }

    /// <summary>
    /// The text is matched case-sensitively.
    /// </summary>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("/usr/sbin/mariadbd: Ready for connections.")]
    [DataRow("/usr/sbin/mariadbd: ready for connections")]
    [DataRow("/usr/sbin/mariadbd: normal shutdown")]
    [DataRow("/usr/sbin/mariadbd: shutdown complete")]
    [DataRow("crash table recovery finished.")]
    [DataRow("innodb: starting crash recovery")]
    public void MariaDbEventClassifierClassifyMatchesCaseSensitively(string message)
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        var result = classifier.Classify(Header("Note", message));

        // Assert
        Assert.AreEqual(string.Empty, result, "event");
    }

    /// <summary>
    /// A recovery ends with the first <c>started</c> line, and a second one without a new recovery is none.
    /// </summary>
    [TestMethod]
    public void MariaDbEventClassifierClassifyClosesARecoveryWithTheStartedLine()
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        var start = classifier.Classify(Header("Note", RecoveryStart));
        var end = classifier.Classify(Header("Note", Started));
        var again = classifier.Classify(Header("Note", Started));

        // Assert
        Assert.AreEqual(MariaDbEvents.RecoveryStart, start, "recovery start");
        Assert.AreEqual(MariaDbEvents.RecoveryEnd, end, "the started line closes the recovery");
        Assert.AreEqual(string.Empty, again, "no recovery is open any more");
    }

    /// <summary>
    /// A start line closes an open recovery, for both wordings of the start line.
    /// </summary>
    /// <param name="startLine">The start line</param>
    [TestMethod]
    [DataRow(StartNew)]
    [DataRow(StartOld)]
    public void MariaDbEventClassifierClassifyClosesARecoveryWithAStartLine(string startLine)
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        var recovery = classifier.Classify(Header("Note", RecoveryStart));
        var start = classifier.Classify(Header("Note", startLine));
        var started = classifier.Classify(Header("Note", Started));

        // Assert
        Assert.AreEqual(MariaDbEvents.RecoveryStart, recovery, "recovery start");
        Assert.AreEqual(MariaDbEvents.Start, start, "start");
        Assert.AreEqual(string.Empty, started, "the start closed the recovery, so the started line is none");
    }

    /// <summary>
    /// The table recovery opens and closes a recovery like the redo recovery.
    /// </summary>
    [TestMethod]
    public void MariaDbEventClassifierClassifyTracksRecoveriesInSequence()
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        var events = new[]
                     {
                         classifier.Classify(Header("Note", RecoveryStart)),
                         classifier.Classify(Header("Note", Started)),
                         classifier.Classify(Header("Note", "Starting table crash recovery...")),
                         classifier.Classify(Header("Note", "Crash table recovery finished.")),
                         classifier.Classify(Header("Note", Started))
                     };

        // Assert
        Assert.AreSequenceEqual([MariaDbEvents.RecoveryStart, MariaDbEvents.RecoveryEnd, MariaDbEvents.RecoveryStart, MariaDbEvents.RecoveryEnd, string.Empty], events, "events in order");
    }

    /// <summary>
    /// Lines of other events do not close an open recovery.
    /// </summary>
    [TestMethod]
    public void MariaDbEventClassifierClassifyKeepsARecoveryOpenOverOtherEvents()
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        // Act
        classifier.Classify(Header("Note", RecoveryStart));

        var ready = classifier.Classify(Header("Note", "/usr/sbin/mariadbd: ready for connections."));
        var plain = classifier.Classify(Header("Note", "InnoDB: 128 rollback segments are active."));
        var end = classifier.Classify(Header("Note", Started));

        // Assert
        Assert.AreEqual(MariaDbEvents.Ready, ready, "ready");
        Assert.AreEqual(string.Empty, plain, "plain line");
        Assert.AreEqual(MariaDbEvents.RecoveryEnd, end, "the recovery was still open");
    }

    /// <summary>
    /// A <c>started</c> line at another level than <c>Note</c> is none while a recovery is open.
    /// </summary>
    [TestMethod]
    public void MariaDbEventClassifierClassifyIgnoresAStartedLineOfAnotherLevel()
    {
        // Arrange
        var classifier = new MariaDbEventClassifier();

        classifier.Classify(Header("Note", RecoveryStart));

        // Act
        var result = classifier.Classify(Header("Warning", Started));

        // Assert
        Assert.AreEqual(string.Empty, result, "event");
    }

    /// <summary>
    /// A fresh classifier has no recovery open, so the first <c>started</c> line is none.
    /// </summary>
    [TestMethod]
    public void MariaDbEventClassifierClassifyStartsWithoutAnOpenRecovery()
    {
        // Arrange
        var first = new MariaDbEventClassifier();
        var second = new MariaDbEventClassifier();

        first.Classify(Header("Note", RecoveryStart));

        // Act
        var result = second.Classify(Header("Note", Started));

        // Assert
        Assert.AreEqual(string.Empty, result, "the state is per classifier");
    }

    /// <summary>
    /// Builds a header of the server form.
    /// </summary>
    /// <param name="level">The level</param>
    /// <param name="message">The message</param>
    /// <returns>The header</returns>
    private static MariaDbLine Header(string level, string message)
    {
        return new MariaDbLine
               {
                   Level = level,
                   Message = message
               };
    }

    #endregion // Methods
}