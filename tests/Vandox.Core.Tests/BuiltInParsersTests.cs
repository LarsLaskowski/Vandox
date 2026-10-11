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
    private const string MailHead = "Oct  9 03:12:01 mail-1 postfix/master[812]: daemon started -- version 3.6.4, configuration /etc/postfix\n";
    private const string MailRfc3339Head = "2026-10-09T03:12:01.123456+02:00 mail-1 postfix/master[812]: daemon started -- version 3.6.4, configuration /etc/postfix\n";
    private const string MailCutHead = "ail-1 postfix/smtpd[2210]: connect from unknown[192.0.2.9]\n";
    private const string MariaDbHead = "2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed\n";
    private const string DovecotHead = "Oct 09 03:12:12 master: Info: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)\nOct 09 03:12:13 imap-login: Info: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7, lip=192.0.2.1, mpid=2301, TLS, session=<AbCdEfGh>\n";
    private const string DovecotSyslogHead = "Oct  9 03:12:12 mail-1 dovecot: master: Info: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)\n";
    private const string PleskHead = "[2026-10-09 03:12:12.123] 4821:5f3a9c1e INFO [panel] Session opened\n";
    private const string NginxHead = "2026/10/09 03:12:12 [error] 1021#1021: *15 connect() failed\n";
    private const string ApacheHead = "[Fri Oct 09 03:12:12.123456 2026] [core:error] [pid 1] x\n";
    private const string Apache22Head = "[Fri Oct 09 03:12:12 2026] [error] [client 198.51.100.8] x\n";
    private const string LegacyHead = "===== Fri Oct  2 09:15:01 CEST 2026 =====\ntop - 09:15:01 up 10 days,  3:02,  1 user,  load average: 0.52, 0.48, 0.45\n";
    private const string MariaDbReady = "2026-03-02 10:20:01 0 [Note] /usr/sbin/mariadbd: ready for connections.\n";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The list holds the journal export parser, the MariaDB error log parser, the Dovecot log parser and the syslog parser, then the Plesk panel, sw-cp-server, nginx and Apache error log parsers and the legacy monitoring log parser, with or without a time zone.
    /// </summary>
    /// <param name="timeZone">The time zone, or <c>null</c></param>
    [TestMethod]
    [DataRow("UTC")]
    [DataRow(null)]
    [DataRow("Europe/Berlin")]
    public void BuiltInParsersCreateReturnsSystemParsersThenErrorLogParsers(string? timeZone)
    {
        // Act
        var parsers = BuiltInParsers.Create(timeZone, ErrorLogPaths.Default);

        // Assert
        Assert.HasCount(9, parsers, "nine parsers");
        Assert.IsInstanceOfType<JournalExportParser>(parsers[0], "first parser");
        Assert.IsInstanceOfType<MariaDbErrorLogParser>(parsers[1], "second parser");
        Assert.IsInstanceOfType<DovecotLogParser>(parsers[2], "third parser");
        Assert.IsInstanceOfType<SyslogParser>(parsers[3], "fourth parser");
        Assert.IsInstanceOfType<ErrorLogParser>(parsers[4], "fifth parser");
        Assert.IsInstanceOfType<ErrorLogParser>(parsers[5], "sixth parser");
        Assert.IsInstanceOfType<ErrorLogParser>(parsers[6], "seventh parser");
        Assert.IsInstanceOfType<ErrorLogParser>(parsers[7], "eighth parser");
        Assert.IsInstanceOfType<LegacyMonitorParser>(parsers[8], "ninth parser");
        Assert.AreSequenceEqual(["journal", "mariadb", "dovecot", "syslog", "plesk-panel", "sw-cp-server-error", "nginx-error", "apache-error", "legacy-monitor"], parsers.Select(parser => parser.Type).ToList(), "types in order");
    }

    /// <summary>
    /// A registry accepts the list.
    /// </summary>
    [TestMethod]
    public void BuiltInParsersCreateListIsAcceptedByTheRegistry()
    {
        // Arrange
        var parsers = BuiltInParsers.Create("UTC", ErrorLogPaths.Default);

        // Act
        var registry = new ParserRegistry(parsers);

        // Assert
        Assert.AreSequenceEqual(["journal", "mariadb", "dovecot", "syslog", "plesk-panel", "sw-cp-server-error", "nginx-error", "apache-error", "legacy-monitor"], registry.GetTypes(), "registered types in order");
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
        var exception = Assert.ThrowsExactly<ArgumentException>(() => BuiltInParsers.Create(timeZone, ErrorLogPaths.Default), "unknown zone");

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
    [DataRow("mail.log", MailHead, "syslog", Confidence.MatchName)]
    [DataRow("mail.log", MailRfc3339Head, "syslog", Confidence.MatchName)]
    [DataRow("mail.log.1", MailHead, "syslog", Confidence.MatchName)]
    [DataRow("mail.log.1", MailRfc3339Head, "syslog", Confidence.MatchName)]
    [DataRow("maillog", MailHead, "syslog", Confidence.MatchName)]
    [DataRow("maillog", MailRfc3339Head, "syslog", Confidence.MatchName)]
    [DataRow("mail.err", MailHead, "syslog", Confidence.MatchName)]
    [DataRow("mail.err", MailRfc3339Head, "syslog", Confidence.MatchName)]
    [DataRow("dovecot.log", DovecotHead, "dovecot", Confidence.MatchContent)]
    [DataRow("mail/dovecot-info.log.1", DovecotHead, "dovecot", Confidence.MatchContent)]
    [DataRow("syslog", DovecotHead, "syslog", Confidence.MatchName)]
    [DataRow("kern.log-20261010", DovecotHead, "syslog", Confidence.MatchName)]
    [DataRow("mail.log", DovecotSyslogHead, "syslog", Confidence.MatchName)]
    [DataRow("notes.txt", "\n" + DovecotHead, "", Confidence.NoMatch)]
    [DataRow("mail.log", MailCutHead, "", Confidence.NoMatch)]
    [DataRow("auth.log", MailCutHead, "", Confidence.NoMatch)]
    [DataRow("plesk/panel.log", "garbage\n", "plesk-panel", Confidence.MatchPath)]
    [DataRow("plesk/panel.log.2", "", "plesk-panel", Confidence.MatchPath)]
    [DataRow("copy/plesk/panel.log-20261010", NginxHead, "plesk-panel", Confidence.MatchPath)]
    [DataRow("logs/other.txt", PleskHead, "plesk-panel", Confidence.MatchContent)]
    [DataRow("logs/other.txt", NginxHead, "nginx-error", Confidence.MatchContent)]
    [DataRow("logs/other.txt", ApacheHead, "apache-error", Confidence.MatchContent)]
    [DataRow("logs/other.txt", Apache22Head, "apache-error", Confidence.MatchContent)]
    [DataRow("nginx/error.log", ApacheHead, "nginx-error", Confidence.MatchPath)]
    [DataRow("nginx/error.log", NginxHead, "nginx-error", Confidence.MatchPath)]
    [DataRow("var/log/nginx/error.log.1", "", "nginx-error", Confidence.MatchPath)]
    [DataRow("sw-cp-server/error_log", NginxHead, "sw-cp-server-error", Confidence.MatchPath)]
    [DataRow("sw-cp-server/error_log", "", "sw-cp-server-error", Confidence.MatchPath)]
    [DataRow("other/error_log", NginxHead, "nginx-error", Confidence.MatchContent)]
    [DataRow("other/error_log", ApacheHead, "apache-error", Confidence.MatchContent)]
    [DataRow("apache2/error.log", NginxHead, "apache-error", Confidence.MatchPath)]
    [DataRow("apache2/error.log-20261010", "", "apache-error", Confidence.MatchPath)]
    [DataRow("error.log", NginxHead, "nginx-error", Confidence.MatchContent)]
    [DataRow("error.log", ApacheHead, "apache-error", Confidence.MatchContent)]
    [DataRow("error.log", PleskHead, "plesk-panel", Confidence.MatchContent)]
    [DataRow("error.log", "garbage\n", "", Confidence.NoMatch)]
    [DataRow("syslog", NginxHead, "syslog", Confidence.MatchName)]
    [DataRow("kern.log", ApacheHead, "syslog", Confidence.MatchName)]
    [DataRow("syslog.1", PleskHead, "syslog", Confidence.MatchName)]
    [DataRow("kern.log-20261010", Apache22Head, "syslog", Confidence.MatchName)]
    [DataRow("monitor.log", LegacyHead, "legacy-monitor", Confidence.MatchContent)]
    [DataRow("logs/top.log", LegacyHead, "legacy-monitor", Confidence.MatchContent)]
    public void BuiltInParsersRegistryDetectsFilesByContentAndName(string name, string head, string expected, Confidence expectedConfidence)
    {
        // Arrange
        var registry = new ParserRegistry(BuiltInParsers.Create("UTC", ErrorLogPaths.Default));
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
        var registry = new ParserRegistry(BuiltInParsers.Create("UTC", ErrorLogPaths.Default));

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

    /// <summary>
    /// The Dovecot log parser does not claim a journal export, a syslog head, a mail log head or a MariaDB error log head.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow(JournalHead)]
    [DataRow(TraditionalHead)]
    [DataRow(Rfc3339Head)]
    [DataRow(MailHead)]
    [DataRow(DovecotSyslogHead)]
    [DataRow(MariaDbHead)]
    [DataRow(MariaDbReady)]
    public void BuiltInParsersDovecotParserIgnoresJournalSyslogAndMariaDbSamples(string head)
    {
        // Arrange
        var parser = new DovecotLogParser(null);

        // Act
        var confidence = parser.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "the Dovecot parser does not claim the head");
    }

    /// <summary>
    /// A file at two configured paths goes to the parser registered first, and a configured path of a parser decides before the content of the file.
    /// </summary>
    /// <param name="swCpServer">The configured path of the sw-cp-server error log</param>
    /// <param name="apache">The configured path of the Apache error log</param>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    /// <param name="expected">The expected parser type</param>
    [TestMethod]
    [DataRow("/var/log/nginx/error.log", ErrorLogPaths.DefaultApache, "nginx/error.log", NginxHead, "sw-cp-server-error")]
    [DataRow(ErrorLogPaths.DefaultSwCpServer, "/srv/x/error_log", "x/error_log", NginxHead, "apache-error")]
    [DataRow("/srv/x/error_log", "/srv/x/error_log", "x/error_log", ApacheHead, "sw-cp-server-error")]
    public void BuiltInParsersRegistryGivesAFileAtTwoConfiguredPathsToTheEarlierParser(string swCpServer, string apache, string name, string head, string expected)
    {
        // Arrange
        var paths = new ErrorLogPaths(ErrorLogPaths.DefaultPleskPanel, swCpServer, ErrorLogPaths.DefaultNginx, apache);
        var registry = new ParserRegistry(BuiltInParsers.Create("UTC", paths));

        // Act
        var (parser, confidence) = registry.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, parser?.Type, "the parser registered first among those whose path matches");
        Assert.AreEqual(Confidence.MatchPath, confidence, "path match");
    }

    /// <summary>
    /// The error log parsers do not claim a journal export, a syslog head, a mail log head, a Dovecot head or a MariaDB error log head.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow(JournalHead)]
    [DataRow(TraditionalHead)]
    [DataRow(Rfc3339Head)]
    [DataRow(MailHead)]
    [DataRow(MailRfc3339Head)]
    [DataRow(DovecotHead)]
    [DataRow(DovecotSyslogHead)]
    [DataRow(MariaDbHead)]
    [DataRow(MariaDbReady)]
    [DataRow("260302 10:10:10 [ERROR] mysqld got signal 6 ;\n")]
    public void BuiltInParsersErrorLogParsersIgnoreSystemLogHeads(string head)
    {
        // Arrange
        var parsers = BuiltInParsers.Create("UTC", ErrorLogPaths.Default).OfType<ErrorLogParser>().ToList();

        // Act
        var confidences = parsers.Select(parser => parser.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(head))).ToList();

        // Assert
        Assert.HasCount(4, parsers, "the four error log parsers");
        Assert.IsTrue(confidences.All(confidence => confidence == Confidence.NoMatch), "no error log parser claims the head");
    }

    /// <summary>
    /// None of the four system parsers claims the head of a Plesk panel log, an nginx error log or an Apache error log.
    /// </summary>
    /// <param name="head">A head</param>
    [TestMethod]
    [DataRow(PleskHead)]
    [DataRow(NginxHead)]
    [DataRow(ApacheHead)]
    [DataRow(Apache22Head)]
    public void BuiltInParsersSystemParsersIgnoreErrorLogHeads(string head)
    {
        // Arrange
        var parsers = BuiltInParsers.Create("UTC", ErrorLogPaths.Default).Take(4).ToList();

        // Act
        var confidences = parsers.Select(parser => parser.Detect(new LogFile("export.txt", null), Encoding.UTF8.GetBytes(head))).ToList();

        // Assert
        Assert.HasCount(4, parsers, "the four system parsers");
        Assert.IsTrue(confidences.All(confidence => confidence == Confidence.NoMatch), "no system parser claims the head");
    }

    /// <summary>
    /// None of the other eight parsers claims the head of a legacy monitoring log.
    /// </summary>
    [TestMethod]
    public void BuiltInParsersOtherParsersIgnoreLegacyMonitorHead()
    {
        // Arrange
        var parsers = BuiltInParsers.Create("UTC", ErrorLogPaths.Default);
        var legacy = parsers.OfType<LegacyMonitorParser>().Single();
        var others = parsers.Where(parser => parser is not LegacyMonitorParser).ToList();

        // Act
        var confidences = others.Select(parser => parser.Detect(new LogFile("monitor.log", null), Encoding.UTF8.GetBytes(LegacyHead))).ToList();

        // Assert
        Assert.AreEqual(LegacyMonitorParser.ParserType, legacy.Type, "the legacy parser");
        Assert.HasCount(8, others, "the eight other parsers");
        Assert.IsTrue(confidences.All(confidence => confidence == Confidence.NoMatch), "no other parser claims the head");
    }

    #endregion // Methods
}