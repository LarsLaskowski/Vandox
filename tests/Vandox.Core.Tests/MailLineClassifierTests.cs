using System.Diagnostics;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MailLineClassifier"/>
/// </summary>
[TestClass]
public class MailLineClassifierTests
{
    #region Constants

    private const string Smtpd = "postfix/smtpd";
    private const string Proxymap = "postfix/proxymap";
    private const string Dovecot = "dovecot";
    private const string Gone = "Server has gone away";
    private const string ConnectFailed = "Connect failed to database (mail): " + Gone;
    private const string MysqlConnectFailed = "mysql(localhost): " + ConnectFailed;
    private const string Mysql = "MySQL server has gone away";
    private const string KeepsPriority = "keeps the priority it had";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the cases of a client phrase after an anchor: each of the eight phrases after each of the four anchors, once with text after the phrase and once as the whole client error.
    /// </summary>
    public static IEnumerable<object[]> ClientPhraseCases
    {
        get
        {
            var phrases = new[]
                          {
                              "Can't connect to local MySQL server",
                              "Can't connect to MySQL server",
                              "MySQL server has gone away",
                              "Lost connection to MySQL server",
                              "Can't connect to local server",
                              "Can't connect to server",
                              "Server has gone away",
                              "Lost connection to server"
                          };

            foreach (var phrase in phrases)
            {
                foreach (var client in new[] { phrase + " through socket '/var/run/mysqld/mysqld.sock' (2)", phrase })
                {
                    yield return [Proxymap, "warning: connect to mysql server localhost: " + client, 4, MailEvents.PostfixDatabaseUnavailable];
                    yield return [Proxymap, "warning: mysql:/etc/postfix/mysql-virtual.cf: query failed: " + client, 4, MailEvents.PostfixDatabaseUnavailable];
                    yield return [Dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): " + client, 3, MailEvents.DovecotDatabaseUnavailable];
                    yield return [Dovecot, "auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: " + client, 4, MailEvents.DovecotDatabaseUnavailable];
                }
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A program is a Postfix daemon when it begins with <c>postfix/</c> or <c>postfix-</c>, holds a slash and has a name after the last one.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="expected">The expected answer</param>
    [TestMethod]
    [DataRow("postfix/smtpd", true)]
    [DataRow("postfix/qmgr", true)]
    [DataRow("postfix/submission/smtpd", true)]
    [DataRow("postfix/smtps/smtpd", true)]
    [DataRow("postfix-out/smtp", true)]
    [DataRow("postfix/postfix-script", true)]
    [DataRow("postfix", false)]
    [DataRow("postfix/", false)]
    [DataRow("postfix/submission/", false)]
    [DataRow("postfix-out", false)]
    [DataRow("postfixadmin", false)]
    [DataRow("postfixadmin/x", false)]
    [DataRow("Postfix/smtpd", false)]
    [DataRow("mypostfix/smtpd", false)]
    [DataRow("dovecot", false)]
    [DataRow("", false)]
    public void MailLineClassifierIsPostfixKnowsThePostfixDaemons(string program, bool expected)
    {
        // Act
        var actual = MailLineClassifier.IsPostfix(program);

        // Assert
        Assert.AreEqual(expected, actual, $"IsPostfix(\"{program}\")");
    }

    /// <summary>
    /// A program is Dovecot when it is <c>dovecot</c> and nothing else.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="expected">The expected answer</param>
    [TestMethod]
    [DataRow("dovecot", true)]
    [DataRow("Dovecot", false)]
    [DataRow("dovecot-lda", false)]
    [DataRow("dovecot/imap", false)]
    [DataRow("imap-login", false)]
    [DataRow("dovecot ", false)]
    [DataRow("", false)]
    public void MailLineClassifierIsDovecotKnowsDovecotOnly(string program, bool expected)
    {
        // Act
        var actual = MailLineClassifier.IsDovecot(program);

        // Assert
        Assert.AreEqual(expected, actual, $"IsDovecot(\"{program}\")");
    }

    /// <summary>
    /// A line of another program keeps priority and event, also when its message carries a marker or a database message.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("psa-pc-remote", "Error: connect to database failed")]
    [DataRow("spamd", "fatal: something went wrong")]
    [DataRow("opendkim", "warning: key table not found")]
    [DataRow("kernel", "Panic: not syncing")]
    [DataRow("postfixadmin/x", "fatal: x")]
    [DataRow("postfix", "warning: x")]
    [DataRow("postfix/", "error: x")]
    [DataRow("Postfix/smtpd", "panic: x")]
    [DataRow("dovecot-lda", "Fatal: x")]
    [DataRow("dovecot/imap", "Panic: x")]
    [DataRow("imap-login", "Error: x")]
    [DataRow("spamd", "warning: connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)")]
    [DataRow("", "fatal: x")]
    public void MailLineClassifierClassifyLeavesOtherProgramsUnchanged(string program, string message)
    {
        // Act and Assert
        AssertClassified(program, message, -1, string.Empty);
    }

    /// <summary>
    /// A Postfix message gets the priority and the event of the marker at its start.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority, -1 to keep the priority the line had</param>
    /// <param name="eventName">The expected event</param>
    [TestMethod]
    [DataRow("warning: hostname mx.example.net does not resolve to address 192.0.2.9", 4, "")]
    [DataRow("error: open database /etc/postfix/x.db: No such file or directory", 3, "")]
    [DataRow("fatal: the Postfix mail system is not running", 2, MailEvents.PostfixFatal)]
    [DataRow("panic: myfree: corrupt or unallocated memory block", 2, MailEvents.PostfixPanic)]
    [DataRow("warning: ", 4, "")]
    [DataRow("fatal: ", 2, MailEvents.PostfixFatal)]
    [DataRow("panic: warning: x", 2, MailEvents.PostfixPanic)]
    [DataRow("connect from unknown[192.0.2.9]", -1, "")]
    [DataRow("Warning: x", -1, "")]
    [DataRow("FATAL: x", -1, "")]
    [DataRow("Fatal: x", -1, "")]
    [DataRow("fatal:x", -1, "")]
    [DataRow(" fatal: x", -1, "")]
    [DataRow("x fatal: y", -1, "")]
    [DataRow("info: x", -1, "")]
    [DataRow("warning:", -1, "")]
    [DataRow("", -1, "")]
    [DataRow("warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <fatal: x>", 4, "")]
    public void MailLineClassifierClassifyReadsThePostfixMarkers(string message, int priority, string eventName)
    {
        // Act and Assert
        AssertClassified(Smtpd, message, priority, eventName);
    }

    /// <summary>
    /// The markers apply to every Postfix daemon name the program rule accepts.
    /// </summary>
    /// <param name="program">The program</param>
    [TestMethod]
    [DataRow("postfix/qmgr")]
    [DataRow("postfix/submission/smtpd")]
    [DataRow("postfix/smtps/smtpd")]
    [DataRow("postfix-out/smtp")]
    [DataRow("postfix/postfix-script")]
    public void MailLineClassifierClassifyReadsTheMarkersOfEveryPostfixDaemon(string program)
    {
        // Act and Assert
        AssertClassified(program, "fatal: x", 2, MailEvents.PostfixFatal);
        AssertClassified(program, "warning: x", 4, string.Empty);
    }

    /// <summary>
    /// A Dovecot message gets the priority and the event of the marker at its start or after its first segment.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority, -1 to keep the priority the line had</param>
    /// <param name="eventName">The expected event</param>
    [TestMethod]
    [DataRow("auth: Debug: Loading modules from directory: /usr/lib/dovecot/modules/auth", 7, "")]
    [DataRow("master: Warning: Killed with signal 15 (by pid=1 uid=0 code=kill)", 4, "")]
    [DataRow("master: Error: service(imap): child 2301 killed with signal 9", 3, "")]
    [DataRow("auth: Fatal: master: service(auth): child 2290 returned error 89", 2, MailEvents.DovecotFatal)]
    [DataRow("imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42 (mail_index_open): assertion failed: (index != NULL)", 2, MailEvents.DovecotPanic)]
    [DataRow("Fatal: Dovecot is already running", 2, MailEvents.DovecotFatal)]
    [DataRow("Fatal: Panic: x", 2, MailEvents.DovecotFatal)]
    [DataRow("Panic: Fatal: x", 2, MailEvents.DovecotPanic)]
    [DataRow("Debug: x", 7, "")]
    [DataRow("Warning: ", 4, "")]
    [DataRow("imap-login: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7", -1, "")]
    [DataRow("imap-login: Disconnected (auth failed, 1 attempts in 2 secs): user=<x: Panic: y>, rip=192.0.2.9", -1, "")]
    [DataRow("imap-login: Disconnected: Panic: x", -1, "")]
    [DataRow("a b: Panic: x", -1, "")]
    [DataRow(": Panic: x", -1, "")]
    [DataRow("auth: panic: x", -1, "")]
    [DataRow("auth: PANIC: x", -1, "")]
    [DataRow("fatal: x", -1, "")]
    [DataRow("auth: Panic:x", -1, "")]
    [DataRow("master: Info: x", -1, "")]
    [DataRow("Info: x", -1, "")]
    [DataRow("auth: info: x", -1, "")]
    [DataRow("", -1, "")]
    public void MailLineClassifierClassifyReadsTheDovecotMarkers(string message, int priority, string eventName)
    {
        // Act and Assert
        AssertClassified(Dovecot, message, priority, eventName);
    }

    /// <summary>
    /// An event that is set stays on a line without an event of its own and is replaced by the event of a fatal or panic marker; a line is never given an empty event.
    /// </summary>
    [TestMethod]
    public void MailLineClassifierClassifyNeverClearsAnEvent()
    {
        // Arrange
        var plain = Line(Smtpd, "connect from unknown[192.0.2.9]", 6, "mariadb.start");
        var warning = Line(Smtpd, "warning: x", 6, "mariadb.start");
        var fatal = Line(Smtpd, "fatal: x", 6, "mariadb.start");
        var dovecot = Line(Dovecot, "auth: Error: x", 6, "mariadb.start");

        // Act
        MailLineClassifier.Classify(plain);
        MailLineClassifier.Classify(warning);
        MailLineClassifier.Classify(fatal);
        MailLineClassifier.Classify(dovecot);

        // Assert
        Assert.AreEqual("mariadb.start", plain.Event, "a line without a marker keeps its event");
        Assert.AreEqual("mariadb.start", warning.Event, "a warning without a database message keeps its event");
        Assert.AreEqual(MailEvents.PostfixFatal, fatal.Event, "a fatal line gets the event of the marker");
        Assert.AreEqual("mariadb.start", dovecot.Event, "an error without a database message keeps its event");
        Assert.AreEqual((byte)3, dovecot.Priority, "the marker sets the priority");
    }

    /// <summary>
    /// The classification changes priority and event only; every other field stays as it was, also for a line that was cut.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow(Smtpd, "fatal: the Postfix mail system is not running")]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: Lost connection to MySQL server during query")]
    [DataRow(Dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Server has gone away")]
    [DataRow(Dovecot, "imap-login: Login: user=<a>")]
    public void MailLineClassifierClassifyChangesOnlyPriorityAndEvent(string program, string message)
    {
        // Arrange
        var payload = new LogLine
                      {
                          Log = "mail.log",
                          Host = "mail-1",
                          Program = program,
                          Pid = 2290,
                          Priority = 6,
                          Message = message,
                          Truncated = true
                      };

        // Act
        MailLineClassifier.Classify(payload);

        // Assert
        Assert.AreEqual("mail.log", payload.Log, "log");
        Assert.AreEqual("mail-1", payload.Host, "host");
        Assert.AreEqual(program, payload.Program, "program");
        Assert.AreEqual(2290, payload.Pid, "pid");
        Assert.AreEqual(message, payload.Message, "message");
        Assert.IsTrue(payload.Truncated, "truncated");
    }

    /// <summary>
    /// Each client phrase after each of the four anchors gives the database event with the priority of the marker, whether the phrase ends the message or is followed by more text.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="eventName">The expected event</param>
    [TestMethod]
    [DynamicData(nameof(ClientPhraseCases))]
    public void MailLineClassifierClassifyFindsEveryClientPhraseAfterEveryAnchor(string program, string message, int priority, string eventName)
    {
        // Act and Assert
        AssertClassified(program, message, priority, eventName);
    }

    /// <summary>
    /// Further forms of a database message that give the database event.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority</param>
    /// <param name="eventName">The expected event</param>
    [TestMethod]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)", 4, MailEvents.PostfixDatabaseUnavailable)]
    [DataRow(Proxymap, "warning: mysql:/etc/postfix/mysql-virtual.cf: query failed: Lost connection to MySQL server during query", 4, MailEvents.PostfixDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry", 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away", 4, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Proxymap, "error: connect to mysql server localhost: Lost connection to MySQL server at 'reading initial communication packet'", 3, MailEvents.PostfixDatabaseUnavailable)]
    [DataRow(Proxymap, "warning: connect to mysql server unix:/var/run/mysqld/mysqld.sock: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)", 4, MailEvents.PostfixDatabaseUnavailable)]
    [DataRow(Proxymap, "warning: connect to mysql server inet:db.example.org:3306: Can't connect to MySQL server on 'db.example.org' (111)", 4, MailEvents.PostfixDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: mysql: Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry", 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: mysql(/run/mysqld/mysqld.sock): " + ConnectFailed, 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: sqlpool(mysql): " + MysqlConnectFailed, 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: sqlpool(mysql): mysql: " + MysqlConnectFailed, 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Warning: mysql(localhost): Query failed, retrying: " + Mysql, 4, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(0): Error: " + MysqlConnectFailed, 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(99999999999999999999): Error: " + MysqlConnectFailed, 3, MailEvents.DovecotDatabaseUnavailable)]
    [DataRow(Dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Lost connection to server", 3, MailEvents.DovecotDatabaseUnavailable)]
    public void MailLineClassifierClassifyGivesTheDatabaseEventForTheFurtherForms(string program, string message, int priority, string eventName)
    {
        // Act and Assert
        AssertClassified(program, message, priority, eventName);
    }

    /// <summary>
    /// A Postfix line that is no database message of the daemon gets the priority of its marker and no database event.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority, -1 to keep the priority the line had</param>
    [TestMethod]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: Access denied for user 'postfix'@'localhost'", 4)]
    [DataRow(Proxymap, "warning: mysql:/etc/postfix/x.cf: query failed: You have an error in your SQL syntax", 4)]
    [DataRow(Proxymap, "warning: connect to pgsql server localhost: could not connect", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost", 4)]
    [DataRow(Smtpd, "warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <MySQL server has gone away>", 4)]
    [DataRow(Smtpd, "warning: x connect to mysql server h: Can't connect to MySQL server on 'h' (111)", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: can't connect to MySQL server", 4)]
    [DataRow(Proxymap, "connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)", -1)]
    [DataRow(Smtpd, "warning: mysql:/etc/postfix/x.cf lookup error for \"a: query failed: MySQL server has gone away@x\"", 4)]
    [DataRow("postfix/trivial-rewrite", "warning: mysql:/etc/postfix/x.cf 'domain' pattern match failed for 'a: query failed: MySQL server has gone away@x'", 4)]
    [DataRow(Smtpd, "warning: mysql:/etc/postfix/x.cf lookup error for \"\"a: query failed: MySQL server has gone away\"@x\"", 4)]
    [DataRow(Proxymap, "warning: mysql:: query failed: MySQL server has gone away", 4)]
    [DataRow(Proxymap, "warning: mysql:/etc/postfix/x.cf: query failed:MySQL server has gone away", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server : MySQL server has gone away", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server a b: Can't connect to MySQL server on 'h' (111)", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: Can't connect to a MySQL server", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: Can\u2019t connect to MySQL server on 'h' (111)", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost:  MySQL server has gone away", 4)]
    [DataRow(Proxymap, "warning: connect to mysql server localhost: mysql server has gone away", 4)]
    [DataRow(Proxymap, "warning: mysql:/etc/postfix/x.cf: query failed: lost connection to MySQL server", 4)]
    [DataRow(Proxymap, "error: mysql:/etc/postfix/x.cf: query failed: Lost connection", 3)]
    public void MailLineClassifierClassifyGivesNoDatabaseEventForOtherPostfixLines(string program, string message, int priority)
    {
        // Act and Assert
        AssertClassified(program, message, priority, string.Empty);
    }

    /// <summary>
    /// A Dovecot line that is no database message of an <c>auth-worker</c> gets the priority of its marker and no database event.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="priority">The expected priority, -1 to keep the priority the line had</param>
    [TestMethod]
    [DataRow("auth-worker(1): Error: user x: Connect failed to database (d): Can't connect to MySQL server on 'h' (111)", 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost): Connect failed to database (mail): Access denied for user 'mail'", 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost): Connect failed to database (mail", 3)]
    [DataRow("auth-worker(1): Error: Query failed, aborting: SELECT password FROM users", 3)]
    [DataRow("auth-worker(1): " + MysqlConnectFailed, -1)]
    [DataRow("auth-worker(1): Debug: mysql(localhost): Query failed, retrying: " + Gone, 7)]
    [DataRow("lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a: Query failed, retrying: MySQL server has gone away@x>: failed to store into mailbox 'INBOX': Quota exceeded (mailbox for user is full)", 3)]
    [DataRow("lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a@x>: " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: sieve: msgid=<a@x>: " + ConnectFailed, 3)]
    [DataRow("auth: Error: sql(a,1.2.3.4): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: sql(a,1.2.3.4): " + ConnectFailed, 3)]
    [DataRow("imap(alice@example.org)<2301><AbCdEfGh>: Error: " + MysqlConnectFailed, 3)]
    [DataRow("Error: " + MysqlConnectFailed, 3)]
    [DataRow("auth-worker: Error: " + MysqlConnectFailed, 3)]
    [DataRow("auth-worker(): Error: " + MysqlConnectFailed, 3)]
    [DataRow("auth-worker(12a): Error: " + MysqlConnectFailed, 3)]
    [DataRow("xauth-worker(1): Error: " + MysqlConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: sqlpool(mysql): mysql: mysql: " + MysqlConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(local host): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(a)b): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: sqlpool(): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: pgsql(localhost): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: Mysql(localhost): " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql : " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost) " + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost):" + ConnectFailed, 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost): Connect failed to database (mail):" + Gone, 3)]
    [DataRow("auth-worker(1): Error: mysql(localhost): Connect failed to database (mail): server has gone away", 3)]
    [DataRow("auth-worker(1): Warning: mysql(localhost): Query failed, retrying:" + Mysql, 4)]
    [DataRow("auth-worker(1): Warning: mysql(localhost): Query failed: " + Mysql, 4)]
    [DataRow("auth-worker(1): Warning: mysql(localhost): query failed, retrying: " + Mysql, 4)]
    public void MailLineClassifierClassifyGivesNoDatabaseEventForOtherDovecotLines(string message, int priority)
    {
        // Act and Assert
        AssertClassified(Dovecot, message, priority, string.Empty);
    }

    /// <summary>
    /// A fatal or panic marker wins over the database message after it.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="eventName">The expected event</param>
    [TestMethod]
    [DataRow(Proxymap, "fatal: connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)", MailEvents.PostfixFatal)]
    [DataRow(Proxymap, "panic: mysql:/etc/postfix/x.cf: query failed: " + Mysql, MailEvents.PostfixPanic)]
    [DataRow(Dovecot, "auth-worker(1): Panic: sqlpool(mysql): Query failed, retrying: " + Mysql, MailEvents.DovecotPanic)]
    [DataRow(Dovecot, "auth-worker(1): Fatal: " + MysqlConnectFailed, MailEvents.DovecotFatal)]
    public void MailLineClassifierClassifyLetsFatalAndPanicWinOverTheDatabaseMessage(string program, string message, string eventName)
    {
        // Act and Assert
        AssertClassified(program, message, 2, eventName);
    }

    /// <summary>
    /// Odd, cut and empty messages cause no exception and no event.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow(Smtpd, "")]
    [DataRow(Smtpd, ":")]
    [DataRow(Smtpd, "warning: connect to mysql server ")]
    [DataRow(Smtpd, "warning: connect to mysql server :")]
    [DataRow(Smtpd, "warning: connect to mysql server a:")]
    [DataRow(Smtpd, "warning: mysql:")]
    [DataRow(Smtpd, "warning: mysql:a: query failed:")]
    [DataRow(Dovecot, "")]
    [DataRow(Dovecot, ": ")]
    [DataRow(Dovecot, "Warning")]
    [DataRow(Dovecot, "auth-worker(")]
    [DataRow(Dovecot, "auth-worker(1")]
    [DataRow(Dovecot, "auth-worker(1): ")]
    [DataRow(Dovecot, "auth-worker(1): Error: ")]
    [DataRow(Dovecot, "auth-worker(1): Error: mysql(")]
    [DataRow(Dovecot, "auth-worker(1): Error: mysql(localhost")]
    [DataRow(Dovecot, "auth-worker(1): Error: mysql(localhost)")]
    [DataRow(Dovecot, "auth-worker(1): Error: sqlpool(mysql): mysql: mysql: mysql")]
    public void MailLineClassifierClassifyToleratesOddMessages(string program, string message)
    {
        // Arrange
        var payload = Line(program, message, null, string.Empty);

        // Act
        MailLineClassifier.Classify(payload);

        // Assert
        Assert.AreEqual(string.Empty, payload.Event, "no event");
        Assert.AreEqual(message, payload.Message, "message");
    }

    /// <summary>
    /// A phrase at the end of a message of the largest size is still found, and a very long message with the phrase far behind the anchor works too.
    /// </summary>
    [TestMethod]
    public void MailLineClassifierClassifyFindsAPhraseAtTheEndOfALongMessage()
    {
        // Arrange
        var postfix = Line(Proxymap, $"warning: connect to mysql server localhost: Lost connection to MySQL server{new string('x', 16000)}", null, string.Empty);
        var dovecot = Line(Dovecot, $"auth-worker(1): Warning: sqlpool(mysql): Query failed, retrying: Server has gone away{new string('y', 16000)}", null, string.Empty);
        var farAway = Line(Dovecot, $"auth-worker(1): Warning: sqlpool(mysql): Query failed, retrying: {new string('z', 16000)}{Gone}", null, string.Empty);

        // Act
        MailLineClassifier.Classify(postfix);
        MailLineClassifier.Classify(dovecot);
        MailLineClassifier.Classify(farAway);

        // Assert
        Assert.AreEqual(MailEvents.PostfixDatabaseUnavailable, postfix.Event, "Postfix, phrase at the start of the client error");
        Assert.AreEqual(MailEvents.DovecotDatabaseUnavailable, dovecot.Event, "Dovecot, phrase at the start of the client error");
        Assert.AreEqual(string.Empty, farAway.Event, "the phrase does not start the client error");
    }

    /// <summary>
    /// The time of a classification grows with the length of the message and not faster: a message eight times as long takes less than 40 times as long.
    /// </summary>
    [TestMethod]
    public void MailLineClassifierClassifyIsLinearInTheLengthOfTheMessage()
    {
        // Arrange
        const int baseLength = 2048;
        const int factor = 8;
        const int iterations = 1000;
        var baseCases = HostileLines(baseLength);
        var longCases = HostileLines(baseLength * factor);

        Measure(baseCases, iterations);
        Measure(longCases, iterations);

        // Act
        var baseTime = Math.Min(Measure(baseCases, iterations), Math.Min(Measure(baseCases, iterations), Measure(baseCases, iterations)));
        var longTime = Math.Min(Measure(longCases, iterations), Math.Min(Measure(longCases, iterations), Measure(longCases, iterations)));

        // Assert
        var ratio = (double)longTime / Math.Max(1, baseTime);

        Assert.IsLessThan(40.0, ratio, $"the time of a message of {baseLength * factor} characters against one of {baseLength} (a linear scan gives about {factor}, a quadratic one about {factor * factor})");
    }

    /// <summary>
    /// Builds a line for the tests.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The priority</param>
    /// <param name="eventName">The event</param>
    /// <returns>The line</returns>
    private static LogLine Line(string program, string message, byte? priority, string eventName)
    {
        return new LogLine
               {
                   Log = "mail.log",
                   Host = "mail-1",
                   Program = program,
                   Pid = 2210,
                   Priority = priority,
                   Message = message,
                   Event = eventName
               };
    }

    /// <summary>
    /// Classifies a line with no priority, with priority 6 and with priority 3, and checks the result and that no other field changed.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="expectedPriority">The priority a marker sets, -1 for a line the classification leaves unchanged</param>
    /// <param name="expectedEvent">The expected event</param>
    private static void AssertClassified(string program, string message, int expectedPriority, string expectedEvent)
    {
        foreach (var before in new byte?[] { null, 6, 3 })
        {
            // Arrange
            var payload = Line(program, message, before, string.Empty);
            var copy = Line(program, message, before, string.Empty);
            var expected = expectedPriority < 0 ? before : (byte?)expectedPriority;
            var context = $"program \"{program}\", message \"{message}\", priority before {before?.ToString() ?? "none"}";

            // Act
            MailLineClassifier.Classify(payload);

            // Assert
            Assert.AreEqual(expected, payload.Priority, expectedPriority < 0 ? $"{KeepsPriority}: {context}" : $"the priority of the marker: {context}");
            Assert.AreEqual(expectedEvent, payload.Event, $"event: {context}");
            Assert.AreEqual(copy.Log, payload.Log, $"log: {context}");
            Assert.AreEqual(copy.Host, payload.Host, $"host: {context}");
            Assert.AreEqual(copy.Program, payload.Program, $"program: {context}");
            Assert.AreEqual(copy.Pid, payload.Pid, $"pid: {context}");
            Assert.AreEqual(copy.Message, payload.Message, $"message: {context}");
            Assert.AreEqual(copy.Truncated, payload.Truncated, $"truncated: {context}");
        }
    }

    /// <summary>
    /// Builds lines whose messages tempt a scan to start over at many places.
    /// </summary>
    /// <param name="length">The approximate length of each message</param>
    /// <returns>The lines</returns>
    private static LogLine[] HostileLines(int length)
    {
        return
        [
            Line(Proxymap, "warning: mysql:" + new string('a', length), null, string.Empty),
            Line(Proxymap, "warning: connect to mysql server " + string.Concat(Enumerable.Repeat("a:", length / 2)), null, string.Empty),
            Line(Dovecot, "auth-worker(1): Error: " + string.Concat(Enumerable.Repeat("mysql(a): ", length / 10)), null, string.Empty),
            Line(Dovecot, "Warning: " + string.Concat(Enumerable.Repeat("x: ", length / 3)), null, string.Empty),
            Line(Dovecot, string.Concat(Enumerable.Repeat("x:", length / 2)) + " Panic: x", null, string.Empty)
        ];
    }

    /// <summary>
    /// Classifies a set of lines repeatedly and returns the time it took.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="iterations">The number of rounds</param>
    /// <returns>The elapsed ticks of the stopwatch</returns>
    private static long Measure(LogLine[] lines, int iterations)
    {
        var start = Stopwatch.GetTimestamp();

        for (var round = 0; round < iterations; round++)
        {
            foreach (var line in lines)
            {
                MailLineClassifier.Classify(line);
            }
        }

        return Stopwatch.GetTimestamp() - start;
    }

    #endregion // Methods
}