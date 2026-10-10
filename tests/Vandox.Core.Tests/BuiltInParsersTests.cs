using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BuiltInParsers"/>
/// </summary>
[TestClass]
public class BuiltInParsersTests
{
    #region Constants

    private const string JournalHead = "__CURSOR=s=0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n";
    private const string TraditionalHead = "Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n";
    private const string Rfc3339Head = "2026-03-01T12:30:15.123456+01:00 web-1 sshd[1234]: Accepted publickey for root\n";
    private const string MariaDbHead = "2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed\n";
    private const string MariaDbReady = "2026-03-02 10:20:01 0 [Note] /usr/sbin/mariadbd: ready for connections.\n";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The list holds the journal export parser, the MariaDB error log parser and then the syslog parser, with or without a time zone.
    /// </summary>
    /// <param name="timeZone">The time zone, or <c>null</c></param>
    [TestMethod]
    [DataRow("UTC")]
    [DataRow(null)]
    [DataRow("Europe/Berlin")]
    public void BuiltInParsersCreateReturnsJournalThenMariaDbThenSyslog(string? timeZone)
    {
        // Act
        var parsers = BuiltInParsers.Create(timeZone);

        // Assert
        Assert.HasCount(3, parsers, "three parsers");
        Assert.IsInstanceOfType<JournalExportParser>(parsers[0], "first parser");
        Assert.IsInstanceOfType<MariaDbErrorLogParser>(parsers[1], "second parser");
        Assert.IsInstanceOfType<SyslogParser>(parsers[2], "third parser");
        Assert.AreEqual("journal", parsers[0].Type, "journal type");
        Assert.AreEqual("mariadb", parsers[1].Type, "mariadb type");
        Assert.AreEqual("syslog", parsers[2].Type, "syslog type");
    }

    /// <summary>
    /// A registry accepts the list.
    /// </summary>
    [TestMethod]
    public void BuiltInParsersCreateListIsAcceptedByTheRegistry()
    {
        // Arrange
        var parsers = BuiltInParsers.Create("UTC");

        // Act
        var registry = new ParserRegistry(parsers);

        // Assert
        Assert.AreSequenceEqual(["journal", "mariadb", "syslog"], registry.GetTypes(), "registered types in order");
    }

    /// <summary>
    /// An unknown time zone is refused with an exception that does not repeat the name.
    /// </summary>
    /// <param name="timeZone">The time zone</param>
    [TestMethod]
    [DataRow("Europe/Nowhere")]
    [DataRow("secret-zone-name-0042")]
    [DataRow("")]
    [DataRow("europe/berlin")]
    public void BuiltInParsersCreateRefusesUnknownZoneWithoutItsText(string timeZone)
    {
        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => BuiltInParsers.Create(timeZone), "unknown zone");

        // Assert
        if (timeZone.Length > 0)
        {
            Assert.DoesNotContain(timeZone, exception.Message, "the message does not show the zone");
        }
    }

    /// <summary>
    /// Each kind of file head is claimed by the right parser, or by none, with the expected confidence.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head</param>
    /// <param name="expected">The expected parser type, or an empty text for none</param>
    /// <param name="expectedConfidence">The expected confidence</param>
    [TestMethod]
    [DataRow("export.txt", JournalHead, "journal", Confidence.MatchContent)]
    [DataRow("syslog", JournalHead, "journal", Confidence.MatchContent)]
    [DataRow("logs/other", TraditionalHead, "syslog", Confidence.MatchName)]
    [DataRow("logs/other", Rfc3339Head, "syslog", Confidence.MatchName)]
    [DataRow("backup/var/log/syslog.1", "", "syslog", Confidence.MatchName)]
    [DataRow("kern.log", "", "syslog", Confidence.MatchName)]
    [DataRow("notes.txt", "LPKSHHRH\u0001\u0000\u0000\u0000binary journal file", "", Confidence.NoMatch)]
    [DataRow("notes.txt", "This is just some prose about a server.\n", "", Confidence.NoMatch)]
    [DataRow("notes.txt", "", "", Confidence.NoMatch)]
    [DataRow("mariadb.err", MariaDbHead, "mariadb", Confidence.MatchContent)]
    [DataRow("mysql/error.log", MariaDbHead, "mariadb", Confidence.MatchContent)]
    [DataRow("notes.txt", "2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836\n", "mariadb", Confidence.MatchContent)]
    [DataRow("notes.txt", "260302 10:10:10 [ERROR] mysqld got signal 6 ;\n", "mariadb", Confidence.MatchContent)]
    [DataRow("notes.txt", "260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql\n", "mariadb", Confidence.MatchContent)]
    [DataRow("logs/other", TraditionalHead + MariaDbReady, "syslog", Confidence.MatchName)]
    [DataRow("logs/other", Rfc3339Head + MariaDbReady, "syslog", Confidence.MatchName)]
    [DataRow("syslog.1", MariaDbHead + MariaDbReady, "syslog", Confidence.MatchName)]
    [DataRow("kern.log", MariaDbHead + MariaDbReady, "syslog", Confidence.MatchName)]
    [DataRow("notes.txt", "InnoDB: Failing assertion: page_is_leaf(block->page.frame)\n" + MariaDbHead, "", Confidence.NoMatch)]
    [DataRow("notes.txt", "This is just some prose about a server.\n" + MariaDbHead, "", Confidence.NoMatch)]
    public void BuiltInParsersRegistryDetectsFilesByContentAndName(string name, string head, string expected, Confidence expectedConfidence)
    {
        // Arrange
        var registry = new ParserRegistry(BuiltInParsers.Create("UTC"));
        var bytes = Encoding.UTF8.GetBytes(head);

        // Act
        var (parser, confidence) = registry.Detect(new LogFile(name, null), bytes);

        // Assert
        Assert.AreEqual(expected, parser?.Type ?? string.Empty, "the parser that claims the file");
        Assert.AreEqual(expectedConfidence, confidence, "the confidence of the claim");
    }

    /// <summary>
    /// A journal export is recognized by content and a syslog file only weakly, so a more specific parser can take it over.
    /// </summary>
    [TestMethod]
    public void BuiltInParsersRegistryRatesJournalByContentAndSyslogByName()
    {
        // Arrange
        var registry = new ParserRegistry(BuiltInParsers.Create("UTC"));

        // Act
        var journal = registry.Detect(new LogFile("anything", null), Encoding.UTF8.GetBytes(JournalHead));
        var syslog = registry.Detect(new LogFile("anything", null), Encoding.UTF8.GetBytes(TraditionalHead));

        // Assert
        Assert.AreEqual(Confidence.MatchContent, journal.Confidence, "journal export by content");
        Assert.AreEqual(Confidence.MatchName, syslog.Confidence, "syslog file by name or shape");
    }

    /// <summary>
    /// Neither parser claims the other one's sample.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow(TraditionalHead)]
    [DataRow(Rfc3339Head)]
    public void BuiltInParsersJournalParserIgnoresSyslogSamples(string head)
    {
        // Arrange
        var journal = new JournalExportParser();

        // Act
        var confidence = journal.Detect(new LogFile("syslog", null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "the journal parser does not claim a syslog sample");
    }

    /// <summary>
    /// The syslog parser does not claim a journal export under a neutral name.
    /// </summary>
    [TestMethod]
    public void BuiltInParsersSyslogParserIgnoresJournalSample()
    {
        // Arrange
        var syslog = new SyslogParser(null);

        // Act
        var confidence = syslog.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(JournalHead));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "the syslog parser does not claim a journal export");
    }

    /// <summary>
    /// Neither the journal parser nor the syslog parser claims an entry header of the MariaDB error log under a neutral name.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow("2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed\n")]
    [DataRow("2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836\n")]
    [DataRow("260302 10:10:10 [ERROR] mysqld got signal 6 ;\n")]
    [DataRow("260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql\n")]
    public void BuiltInParsersJournalAndSyslogParsersIgnoreMariaDbHeads(string head)
    {
        // Arrange
        var file = new LogFile("export.txt", null);
        var bytes = Encoding.UTF8.GetBytes(head);

        // Act
        var journal = new JournalExportParser().Detect(file, bytes);
        var syslog = new SyslogParser(null).Detect(file, bytes);

        // Assert
        Assert.AreEqual(Confidence.NoMatch, journal, "the journal parser does not claim the head");
        Assert.AreEqual(Confidence.NoMatch, syslog, "the syslog parser does not claim the head");
    }

    /// <summary>
    /// The MariaDB error log parser does not claim a journal export or a syslog head.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow(JournalHead)]
    [DataRow(TraditionalHead)]
    [DataRow(Rfc3339Head)]
    public void BuiltInParsersMariaDbParserIgnoresJournalAndSyslogSamples(string head)
    {
        // Arrange
        var parser = new MariaDbErrorLogParser(null);

        // Act
        var confidence = parser.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "the MariaDB parser does not claim the head");
    }

    #endregion // Methods
}