using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BuiltInSignatures"/>
/// </summary>
[TestClass]
public class BuiltInSignaturesTests
{
    #region Constants

    private const string KillBare = "Out of memory: Killed process 4242 (mariadbd) total-vm:8400000kB, anon-rss:7400000kB, file-rss:0kB, shmem-rss:0kB, UID:110 pgtables:14600kB oom_score_adj:0";
    private const string KillPrefixed = "[123456.789105] " + KillBare;
    private const string OomFields = "n:victim_pid=4242\nt:victim_command=mariadbd\nn:anon_rss_bytes=7577600000\nn:oom_score_adj=0";
    private const string DiskFullMessage = "mariadbd: Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 \"No space left on device\")";
    private const string DovecotConnect = "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Too many connections - waiting for 1 seconds before retry";
    private const string Kernel = "5.15.0-91-generic (buildd@lcy02-amd64-045) (gcc (Ubuntu 11.4.0-1ubuntu1~22.04) 11.4.0, GNU ld (GNU Binutils for Ubuntu) 2.38) #101-Ubuntu SMP Tue Nov 14 13:30:08 UTC 2023";

    #endregion // Constants

    #region Fields

    private static readonly string[] _crashAndRecoveryEvents = ["mariadb.recovery_start", "mariadb.recovery_end", "mariadb.abort"];
    private static readonly string[] _remoteTextSignatures = ["disk_full", "mariadb_too_many_connections"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The built-in catalog lists the signatures in the documented order.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesCatalogListsTheSignaturesInOrder()
    {
        // Arrange
        string[] expected = ["oom_kill", "mariadb_crash", "mariadb_recovery_start", "mariadb_recovery_end", "mariadb_too_many_connections", "service_start", "service_stop", "service_failed", "disk_full", "postfix_fatal", "dovecot_fatal", "boot"];

        // Act
        var ids = BuiltInSignatures.Catalog.Signatures.Select(signature => signature.Id).ToList();
        var catalogAgain = BuiltInSignatures.Catalog;

        // Assert
        Assert.AreSequenceEqual(expected, ids, "signature IDs in catalog order");
        Assert.AreSame(BuiltInSignatures.Catalog, catalogAgain, "loaded once");
    }

    /// <summary>
    /// Every pattern is compiled with exactly the options of the non-backtracking engine.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesPatternsRunWithoutBacktracking()
    {
        // Act
        var patterns = BuiltInSignatures.Catalog.Signatures.Where(signature => signature.Pattern is not null).ToList();

        // Assert
        Assert.IsNotEmpty(patterns, "some signatures have a pattern");

        foreach (var signature in patterns)
        {
            Assert.AreEqual(Signature.PatternOptions, signature.Pattern!.Options, $"options of {signature.Id}");
        }
    }

    /// <summary>
    /// No pattern holds the Unicode digit class, which matches digits of other scripts.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesPatternsUseAsciiDigitsOnly()
    {
        // Arrange
        const string digitClass = "\\d";

        // Act
        var patterns = BuiltInSignatures.Catalog.Signatures.Where(signature => signature.Pattern is not null).ToList();

        // Assert
        Assert.IsNotEmpty(patterns, "some signatures have a pattern");

        foreach (var signature in patterns)
        {
            Assert.DoesNotContain(digitClass, signature.Pattern!.ToString(), $"pattern of {signature.Id} has no backslash-d");
        }
    }

    /// <summary>
    /// Every signature has an event or an origin condition, and the two that read messages with remote text anchor their pattern at the start of the message.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesEverySignatureHasAnEventOrOriginCondition()
    {
        // Act
        var signatures = BuiltInSignatures.Catalog.Signatures;

        // Assert
        foreach (var signature in signatures)
        {
            Assert.IsTrue(signature.Events.Count > 0 || signature.Programs.Count > 0 || signature.Sources.Count > 0, $"{signature.Id} has an event or an origin condition");
        }

        foreach (var id in _remoteTextSignatures)
        {
            var signature = signatures.Single(candidate => candidate.Id == id);

            Assert.IsEmpty(signature.Events, $"{id} has no event condition");
            Assert.IsNotNull(signature.Pattern, $"{id} has a pattern");
            Assert.StartsWith("\\A", signature.Pattern.ToString(), $"the pattern of {id} starts with the start-of-text anchor");
        }
    }

    /// <summary>
    /// The kill line of the OOM killer gives the victim, its anon-RSS in bytes and the score adjustment, with or without the kernel's time stamp and in the lines of a report.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected fields</param>
    [TestMethod]
    [DataRow("kernel", "[123456.789100] oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/system.slice/mariadb.service,task=mariadbd,pid=4242,uid=110\n" + KillPrefixed, OomFields)]
    [DataRow("kernel", KillPrefixed, OomFields)]
    [DataRow("kernel", KillBare, OomFields)]
    [DataRow("kernel", "Memory cgroup out of memory: Killed process 42 (php-fpm) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-1000", "n:victim_pid=42\nt:victim_command=php-fpm\nn:anon_rss_bytes=7168\nn:oom_score_adj=-1000")]
    [DataRow("kernel", "Out of memory (oom_kill_allocating_task): Killed process 42 (a (b)) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB", "n:victim_pid=42\nt:victim_command=a (b)\nn:anon_rss_bytes=7168")]
    [DataRow("kernel", "Out of memory: Killed process 42 (x) total-vm:1kB) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-17", "n:victim_pid=42\nt:victim_command=x) total-vm:1kB\nn:anon_rss_bytes=7168\nn:oom_score_adj=-17")]
    public void BuiltInSignaturesOomKillReadsTheKillLine(string program, string message, string expected)
    {
        AssertDetected("oom_kill", "syslog", program, string.Empty, string.Empty, message, expected);
    }

    /// <summary>
    /// Other lines of the OOM report, other writers and other programs give no OOM kill.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("sshd", KillPrefixed)]
    [DataRow("kernel", "oom_reaper: reaped process 4242 (mariadbd), now anon-rss:0kB, file-rss:0kB, shmem-rss:0kB")]
    [DataRow("kernel", "Out of memory: OOM victim 42 (x) is already exiting. Skip killing the task")]
    [DataRow("kernel", "Out of memory and no killable processes...")]
    public void BuiltInSignaturesOomKillIgnoresOtherLines(string program, string message)
    {
        AssertNothing("syslog", program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// A process ID written in Arabic-Indic digits is not a number, so the kill line is not read.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesOomKillIgnoresArabicIndicDigits()
    {
        // Arrange
        var message = "Out of memory: Killed process \u0664\u0662 (mariadbd) total-vm:8400000kB, anon-rss:7400000kB, file-rss:0kB, shmem-rss:0kB";

        // Assert
        Assert.Contains(character => character > 127 && char.IsDigit(character), message, "the input holds a digit above U+007F, so the escape was not lost");

        // Act and assert
        AssertNothing("syslog", "kernel", string.Empty, string.Empty, message);
    }

    /// <summary>
    /// The abort event of MariaDB gives the signal, from the error log and from the journal.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected fields</param>
    [TestMethod]
    [DataRow("mariadb", "", "", "mariadb.abort", "mysqld got signal 6 ;\nThis could be because you hit a bug.", "n:signal=6")]
    [DataRow("journal", "mariadbd", "web-1", "mariadb.abort", "mysqld got signal 6 ;\nThis could be because you hit a bug.", "n:signal=6")]
    public void BuiltInSignaturesMariaDbCrashReadsTheSignal(string source, string program, string host, string eventName, string message, string expected)
    {
        AssertDetected("mariadb_crash", source, program, host, eventName, message, expected);
    }

    /// <summary>
    /// The signal text without the abort event, or with another event, is no crash.
    /// </summary>
    /// <param name="eventName">The event</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("mariadb.start")]
    public void BuiltInSignaturesMariaDbCrashIgnoresOtherLines(string eventName)
    {
        AssertNothing("mariadb", string.Empty, string.Empty, eventName, "mysqld got signal 6 ;");
    }

    /// <summary>
    /// The recovery start event of MariaDB is a recovery start.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesMariaDbRecoveryStartMatchesItsEvent()
    {
        AssertDetected("mariadb_recovery_start", "mariadb", string.Empty, string.Empty, "mariadb.recovery_start", "InnoDB: Starting crash recovery from checkpoint LSN=1", string.Empty);
    }

    /// <summary>
    /// The recovery start text without its event, or with the end event, is no recovery start.
    /// </summary>
    /// <param name="eventName">The event</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("mariadb.recovery_end")]
    public void BuiltInSignaturesMariaDbRecoveryStartIgnoresOtherLines(string eventName)
    {
        AssertNoneOf("mariadb_recovery_start", "mariadb", string.Empty, string.Empty, eventName, "InnoDB: Starting crash recovery from checkpoint LSN=1");
    }

    /// <summary>
    /// The recovery end event of MariaDB is a recovery end.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesMariaDbRecoveryEndMatchesItsEvent()
    {
        AssertDetected("mariadb_recovery_end", "mariadb", string.Empty, string.Empty, "mariadb.recovery_end", "InnoDB: 10.6.12 started; log sequence number 1; transaction id 2", string.Empty);
    }

    /// <summary>
    /// The recovery end text without its event, or with the start event, is no recovery end.
    /// </summary>
    /// <param name="eventName">The event</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("mariadb.recovery_start")]
    public void BuiltInSignaturesMariaDbRecoveryEndIgnoresOtherLines(string eventName)
    {
        AssertNoneOf("mariadb_recovery_end", "mariadb", string.Empty, string.Empty, eventName, "InnoDB: 10.6.12 started; log sequence number 1; transaction id 2");
    }

    /// <summary>
    /// The MySQL client errors of Postfix and Dovecot that report too many connections are recognized at the start of the message.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("syslog", "postfix/proxymap", "warning: connect to mysql server localhost: Too many connections")]
    [DataRow("syslog", "postfix/cleanup", "warning: connect to mysql server localhost: Too many connections")]
    [DataRow("syslog", "dovecot", DovecotConnect)]
    [DataRow("dovecot", "", DovecotConnect)]
    public void BuiltInSignaturesTooManyConnectionsMatchesClientErrors(string source, string program, string message)
    {
        AssertDetected("mariadb_too_many_connections", source, program, string.Empty, string.Empty, message, string.Empty);
    }

    /// <summary>
    /// Other lines about connections, other writers and the error text inside other text give no event.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("syslog", "postfix/postscreen", "NOQUEUE: reject: CONNECT from [192.0.2.9]:4242: 421 4.7.0 Error: too many connections from 192.0.2.9")]
    [DataRow("syslog", "sshd", "warning: connect to mysql server localhost: Too many connections")]
    [DataRow("syslog", "postfix/smtpd", "NOQUEUE: reject: RCPT from x[192.0.2.9]: 554 5.7.1 <warning: connect to mysql server x: Too many connections>: Recipient address rejected")]
    [DataRow("syslog", "dovecot", "imap-login: Info: Disconnected: user=<auth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections>")]
    [DataRow("syslog", "dovecot", "master: Info: x\nauth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections")]
    [DataRow("apache-error", "", "[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3")]
    [DataRow("nginx-error", "", "[alert] 1021#1021: accept4() failed (24: Too many open files)")]
    public void BuiltInSignaturesTooManyConnectionsIgnoresOtherLines(string source, string program, string message)
    {
        AssertNothing(source, program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// A started unit of systemd gives its description and, on newer systemd versions, its unit.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected fields</param>
    [TestMethod]
    [DataRow("Started MariaDB 10.6.12 database server.", "t:description=MariaDB 10.6.12 database server")]
    [DataRow("Started mariadb.service - MariaDB 10.11.6 database server.", "t:unit=mariadb.service\nt:description=MariaDB 10.11.6 database server")]
    public void BuiltInSignaturesServiceStartReadsDescriptionAndUnit(string message, string expected)
    {
        AssertDetected("service_start", "syslog", "systemd", string.Empty, string.Empty, message, expected);
    }

    /// <summary>
    /// Starting and finished jobs and the start text of other programs give no service start.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("systemd", "Starting MariaDB 10.6.12 database server...")]
    [DataRow("systemd", "Finished Daily apt upgrade and clean activities.")]
    [DataRow("logger", "Started MariaDB 10.6.12 database server.")]
    public void BuiltInSignaturesServiceStartIgnoresOtherLines(string program, string message)
    {
        AssertNothing("syslog", program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// A stopped unit of systemd gives its description and, on newer systemd versions, its unit.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected fields</param>
    [TestMethod]
    [DataRow("Stopped MariaDB 10.6.12 database server.", "t:description=MariaDB 10.6.12 database server")]
    [DataRow("Stopped mariadb.service - MariaDB 10.11.6 database server.", "t:unit=mariadb.service\nt:description=MariaDB 10.11.6 database server")]
    public void BuiltInSignaturesServiceStopReadsDescriptionAndUnit(string message, string expected)
    {
        AssertDetected("service_stop", "syslog", "systemd", string.Empty, string.Empty, message, expected);
    }

    /// <summary>
    /// Stopping jobs, stops with an error and deactivation messages give no service stop.
    /// </summary>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("Stopping MariaDB 10.6.12 database server...")]
    [DataRow("Stopped (with error) MariaDB 10.6.12 database server.")]
    [DataRow("mariadb.service: Deactivated successfully.")]
    public void BuiltInSignaturesServiceStopIgnoresOtherLines(string message)
    {
        AssertNothing("syslog", "systemd", string.Empty, string.Empty, message);
    }

    /// <summary>
    /// A failed service gives its unit and the result.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="expected">The expected fields</param>
    [TestMethod]
    [DataRow("mariadb.service: Failed with result 'oom-kill'.", "t:unit=mariadb.service\nt:result=oom-kill")]
    [DataRow("plesk-php82-fpm.service: Failed with result 'exit-code'.", "t:unit=plesk-php82-fpm.service\nt:result=exit-code")]
    public void BuiltInSignaturesServiceFailedReadsUnitAndResult(string message, string expected)
    {
        AssertDetected("service_failed", "syslog", "systemd", string.Empty, string.Empty, message, expected);
    }

    /// <summary>
    /// Failures of other units, other messages about a service, other programs and a result with a line feed give no failed service.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("systemd", "session-3.scope: Failed with result 'oom-kill'.")]
    [DataRow("systemd", "mariadb.service: Main process exited, code=killed, status=9/KILL")]
    [DataRow("cron", "mariadb.service: Failed with result 'oom-kill'.")]
    [DataRow("systemd", "x.service: Failed with result 'a\nb'.")]
    public void BuiltInSignaturesServiceFailedIgnoresOtherLines(string program, string message)
    {
        AssertNothing("syslog", program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// The write errors of MariaDB about a full disk are recognized in the error log and, by program, in the journal and syslog.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("mariadb", "", DiskFullMessage)]
    [DataRow("mariadb", "", "Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 \"No space left on device\")")]
    [DataRow("mariadb", "", "/usr/sbin/mariadbd: Disk is full writing './mysql-bin.000042' (Errcode: 28 \"No space left on device\"). Waiting for someone to free space...")]
    [DataRow("journal", "mariadbd", DiskFullMessage)]
    [DataRow("syslog", "mysqld", DiskFullMessage)]
    public void BuiltInSignaturesDiskFullMatchesMariaDbWriteErrors(string source, string program, string message)
    {
        AssertDetected("disk_full", source, program, string.Empty, string.Empty, message, string.Empty);
    }

    /// <summary>
    /// Other texts, the error text on a later line, other writers and other programs give no full disk.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("mariadb", "", "Disk quota exceeded")]
    [DataRow("mariadb", "", "no space left on device")]
    [DataRow("mariadb", "", "Access denied for user 'a'@'h'\nmariadbd: Disk full (x); waiting")]
    [DataRow("plesk-panel", "", "ERR [panel] file_put_contents(): Write of 4096 bytes failed with errno=28 No space left on device")]
    [DataRow("syslog", "postfix/cleanup", "mariadbd: Disk full (x); waiting")]
    [DataRow("nginx-error", "", "[crit] 1021#1021: *16 pwrite() \"/var/lib/nginx/body/0000000001\" failed (28: No space left on device), client: 198.51.100.7, server: example.org")]
    public void BuiltInSignaturesDiskFullIgnoresOtherLines(string source, string program, string message)
    {
        AssertNothing(source, program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// Both the fatal and the panic event of Postfix are a Postfix fatal error.
    /// </summary>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("postfix.fatal", "fatal: the Postfix mail system is not running")]
    [DataRow("postfix.panic", "panic: myfree: corrupt or unallocated memory block")]
    public void BuiltInSignaturesPostfixFatalMatchesFatalAndPanic(string eventName, string message)
    {
        AssertDetected("postfix_fatal", "syslog", "postfix/postfix-script", string.Empty, eventName, message, string.Empty);
    }

    /// <summary>
    /// Events of Dovecot, other Postfix events and the fatal text without its event are no Postfix fatal error.
    /// </summary>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("dovecot.fatal", "fatal: the Postfix mail system is not running")]
    [DataRow("postfix.database_unavailable", "fatal: the Postfix mail system is not running")]
    [DataRow("", "fatal: the Postfix mail system is not running")]
    public void BuiltInSignaturesPostfixFatalIgnoresOtherLines(string eventName, string message)
    {
        AssertNoneOf("postfix_fatal", "syslog", "postfix/postfix-script", string.Empty, eventName, message);
    }

    /// <summary>
    /// Both the fatal and the panic event of Dovecot are a Dovecot fatal error.
    /// </summary>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("dovecot.fatal", "auth: Fatal: master: service(auth): child 2290 returned error 89")]
    [DataRow("dovecot.panic", "Panic: file mail-index.c: line 42")]
    public void BuiltInSignaturesDovecotFatalMatchesFatalAndPanic(string eventName, string message)
    {
        AssertDetected("dovecot_fatal", "syslog", "dovecot", string.Empty, eventName, message, string.Empty);
    }

    /// <summary>
    /// Events of Postfix, other Dovecot events and the fatal text without its event are no Dovecot fatal error.
    /// </summary>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("postfix.fatal", "auth: Fatal: master: service(auth): child 2290 returned error 89")]
    [DataRow("dovecot.database_unavailable", "auth: Fatal: master: service(auth): child 2290 returned error 89")]
    [DataRow("", "Fatal: x")]
    public void BuiltInSignaturesDovecotFatalIgnoresOtherLines(string eventName, string message)
    {
        AssertNoneOf("dovecot_fatal", "syslog", "dovecot", string.Empty, eventName, message);
    }

    /// <summary>
    /// The first kernel line of a boot gives the kernel version, with or without the time stamp.
    /// </summary>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("[    0.000000] Linux version " + Kernel)]
    [DataRow("Linux version " + Kernel)]
    public void BuiltInSignaturesBootReadsTheKernelVersion(string message)
    {
        AssertDetected("boot", "syslog", "kernel", string.Empty, string.Empty, message, "t:kernel_version=5.15.0-91-generic");
    }

    /// <summary>
    /// The same text of another program, other kernel lines and the version text inside another line give no boot.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("sshd", "[    0.000000] Linux version " + Kernel)]
    [DataRow("kernel", "Command line: BOOT_IMAGE=/vmlinuz-5.15.0-91-generic root=UUID=1 ro")]
    [DataRow("kernel", "x Linux version 5.15.0-91-generic (gcc)")]
    public void BuiltInSignaturesBootIgnoresOtherLines(string program, string message)
    {
        AssertNothing("syslog", program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// A line that two signatures match gives both events, in catalog order.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesLineMatchingTwoSignaturesYieldsBoth()
    {
        // Act
        var events = Detect("mariadb", string.Empty, string.Empty, "mariadb.abort", DiskFullMessage + "\nmysqld got signal 6 ;");

        // Assert
        Assert.AreSequenceEqual(["mariadb_crash", "disk_full"], events.Select(detected => detected.SignatureId).ToList(), "crash, then disk full");
        Assert.AreEqual("n:signal=6", SignatureSamples.Fields(events[0]), "fields of the crash");
        Assert.AreEqual(string.Empty, SignatureSamples.Fields(events[1]), "fields of the full disk");
    }

    /// <summary>
    /// Client-supplied text in the lines of the web server error logs triggers no signature.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message as the error log parser stores it</param>
    [TestMethod]
    [DataRow("nginx-error", "[error] 1021#1021: *15 open() \"/var/www/vhosts/example.org/httpdocs/x\" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /x HTTP/1.1\", host: \"example.org\", referrer: \"No space left on device\"")]
    [DataRow("nginx-error", "[error] 1021#1021: *15 open() \"/var/www/vhosts/example.org/httpdocs/x\" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /x HTTP/1.1\", host: \"example.org\", referrer: \"mariadbd: Disk full (x); Too many connections\"")]
    [DataRow("nginx-error", "[crit] 1021#1021: *16 open() \"/var/www/vhosts/example.org/httpdocs/x\" failed (28: No space left on device)\" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /x%22%20failed HTTP/1.1\"")]
    [DataRow("nginx-error", "[error] 1021#1021: *15 FastCGI sent in stderr: \"PHP message: x\nDisk full (y); waiting\nwarning: connect to mysql server x: Too many connections\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("apache-error", "[core:error] [pid 902] [client 198.51.100.7:51234] AH00126: Invalid URI in request GET /x HTTP/1.1, referer: Disk is full writing './x'; Too many connections")]
    [DataRow("apache-error", "[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  x, referer: auth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections")]
    public void BuiltInSignaturesClientTextInWebServerLogsYieldsNothing(string source, string message)
    {
        AssertNothing(source, string.Empty, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// The OOM report that syslog and kern.log both hold is one event that links the report record of each file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesOomReportInSyslogAndKernLogIsOneEvent()
    {
        // Arrange
        var modTime = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var parser = new SyslogParser(DateTimeZone.Utc);
        var kernLog = await ParseAsync(parser, new LogFile("var/log/kern.log", modTime), "testdata/logs/kern.log-oom", TestContext.CancellationToken);
        var syslog = await ParseAsync(parser, new LogFile("var/log/syslog", modTime), "testdata/logs/kern.log-oom", TestContext.CancellationToken);
        var kernLogReport = Array.FindIndex(kernLog.ToArray(), pair => RecordingEmitter.Line(pair.Record).Message.Contains("Killed process", StringComparison.Ordinal));
        var syslogRecords = syslog.Select(pair => (Id: (long)kernLog.Count + pair.Id, pair.Record)).ToList();
        var records = kernLog.Concat(syslogRecords).OrderBy(pair => pair.Record.CapturedAt).ThenBy(pair => pair.Id).ToList();

        // Act
        var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

        // Assert
        Assert.AreNotEqual(-1, kernLogReport, "the kern.log copy holds the report record");
        Assert.HasCount(1, events, "one event");

        var detected = events[0];

        Assert.AreEqual("oom_kill", detected.SignatureId, "signature");
        Assert.AreEqual("web-1", detected.Host, "host");
        Assert.AreEqual(SignatureSamples.Expected(OomFields), SignatureSamples.Fields(detected), "fields");
        Assert.AreSequenceEqual([kernLog[kernLogReport].Id, syslogRecords[kernLogReport].Id], detected.RecordIds.ToList(), "the report record of kern.log, then that of syslog");
        Assert.AreEqual(2L, detected.LineCount, "two lines");
        Assert.AreEqual(kernLog[kernLogReport].Record.CapturedAt, detected.CapturedAt, "time of the first line");
    }

    /// <summary>
    /// The MariaDB error log fixture gives the recoveries and the crash, each with the one record of its entry, and no boot event.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesMariaDbErrorLogYieldsCrashAndRecoveries()
    {
        // Arrange
        var records = await ParseAsync(new MariaDbErrorLogParser(DateTimeZone.Utc), new LogFile("mysql/error.log", null), "testdata/logs/mariadb-error.log", TestContext.CancellationToken);
        var withEvent = records.Where(pair => _crashAndRecoveryEvents.Contains(RecordingEmitter.Line(pair.Record).Event)).ToList();

        // Act
        var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

        // Assert
        Assert.AreSequenceEqual(["mariadb_recovery_start", "mariadb_recovery_end", "mariadb_crash", "mariadb_recovery_start", "mariadb_recovery_end"], events.Select(detected => detected.SignatureId).ToList(), "events in time order");
        Assert.AreSequenceEqual(withEvent.Select(pair => pair.Id).ToList(), events.Select(detected => detected.RecordIds.Single()).ToList(), "each event has the one ID of its entry");
        Assert.AreEqual("n:signal=6", SignatureSamples.Fields(events[2]), "signal of the crash");
        Assert.IsTrue(events.All(detected => detected.LineCount == 1), "one line each");
    }

    /// <summary>
    /// The mail log fixture gives one event per daemon: the Postfix fatal error and panic, then the Dovecot panic and fatal error, and nothing for the forged user name.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesMailLogYieldsOneEventPerDaemon()
    {
        // Arrange
        var records = await ParseAsync(new SyslogParser(DateTimeZone.Utc), new LogFile("mail.log", new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)), MailLogFixture.RelativePath, TestContext.CancellationToken);

        // Act
        var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

        // Assert
        Assert.AreSequenceEqual(["postfix_fatal", "dovecot_fatal"], events.Select(detected => detected.SignatureId).ToList(), "one event per daemon, in this order");
        Assert.AreEqual(MailLogFixture.Host, events[0].Host, "host of the Postfix event");
        Assert.AreSequenceEqual([IdOf(records, "fatal: the Postfix mail system is not running"), IdOf(records, "panic: myfree: corrupt")], events[0].RecordIds.ToList(), "the fatal and the panic line of Postfix");
        Assert.AreEqual(2L, events[0].LineCount, "two Postfix lines");
        Assert.AreSequenceEqual([IdOf(records, "Panic: file mail-index.c"), IdOf(records, "auth: Fatal: master: service(auth)")], events[1].RecordIds.ToList(), "the panic and the fatal line of Dovecot");
        Assert.AreEqual(2L, events[1].LineCount, "two Dovecot lines");
    }

    /// <summary>
    /// The Dovecot log fixture gives one fatal event that links the panic and the fatal line, and nothing for the forged user name or the connect error.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesDovecotLogYieldsOneFatalEvent()
    {
        // Arrange
        var records = await ParseAsync(new DovecotLogParser(DateTimeZone.Utc), new LogFile("dovecot.log-20261010", null), "testdata/logs/dovecot.log", TestContext.CancellationToken);

        // Act
        var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

        // Assert
        Assert.HasCount(11, records, "the fixture has eleven lines");
        Assert.HasCount(1, events, "exactly one event");
        Assert.AreEqual("dovecot_fatal", events[0].SignatureId, "signature");
        Assert.AreSequenceEqual([records[5].Id, records[6].Id], events[0].RecordIds.ToList(), "the panic line, then the fatal line");
        Assert.AreEqual(2L, events[0].LineCount, "two lines");
        Assert.DoesNotContain(records[8].Id, events[0].RecordIds, "the forged line is not linked");
    }

    /// <summary>
    /// The fixtures of the web server and Plesk logs give no event.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesErrorLogFixturesYieldNothing()
    {
        // Arrange
        var zone = DateTimeZone.Utc;
        var cases = new (ILogParser Parser, LogFile File, string Path)[]
                    {
                        (new ErrorLogParser(ErrorLogParser.NginxType, ErrorLogFormat.Nginx, ErrorLogPaths.DefaultNginx, true, zone), new LogFile("logs/sample", null), "testdata/logs/nginx-error.log"),
                        (new ErrorLogParser(ErrorLogParser.ApacheType, ErrorLogFormat.Apache, ErrorLogPaths.DefaultApache, true, zone), new LogFile("logs/sample", null), "testdata/logs/apache-error.log"),
                        (new ErrorLogParser(ErrorLogParser.PleskPanelType, ErrorLogFormat.PleskPanel, ErrorLogPaths.DefaultPleskPanel, true, zone), new LogFile("logs/sample", null), "testdata/logs/plesk-panel.log")
                    };

        foreach (var (parser, file, path) in cases)
        {
            var records = await ParseAsync(parser, file, path, TestContext.CancellationToken);

            // Act
            var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

            // Assert
            Assert.IsNotEmpty(records, $"{path} gives records");
            Assert.IsEmpty(events, $"{path} gives no event");
        }
    }

    /// <summary>
    /// Parses a fixture and numbers the records from 1.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="file">The file the fixture stands for</param>
    /// <param name="path">The fixture, relative to the repository root</param>
    /// <param name="cancellationToken">The token the parser honors</param>
    /// <returns>A task that returns the records with their IDs</returns>
    private static async Task<List<(long Id, DataRecord Record)>> ParseAsync(ILogParser parser, LogFile file, string path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(path), cancellationToken);
        var emitter = await RecordingEmitter.ParseAsync(parser, file, content, cancellationToken);

        Assert.IsEmpty(emitter.Skips, $"nothing of {path} is skipped");

        return emitter.Records.Select((record, index) => ((long)index + 1, record)).ToList();
    }

    /// <summary>
    /// Returns the ID of the one record whose message holds a text.
    /// </summary>
    /// <param name="records">The records with their IDs</param>
    /// <param name="text">The text</param>
    /// <returns>The ID</returns>
    private static long IdOf(List<(long Id, DataRecord Record)> records, string text)
    {
        return records.Single(pair => RecordingEmitter.Line(pair.Record).Message.Contains(text, StringComparison.Ordinal)).Id;
    }

    /// <summary>
    /// Runs the built-in catalog over one log line.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    /// <returns>The detected events</returns>
    private static List<DetectedEvent> Detect(string source, string program, string host, string eventName, string message)
    {
        return SignatureSamples.Run(BuiltInSignatures.Catalog, SignatureSamples.Line(message, source, program, host, eventName));
    }

    /// <summary>
    /// Asserts that one log line gives exactly one event of a signature with exactly the given fields.
    /// </summary>
    /// <param name="id">The signature ID</param>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    /// <param name="expected">The lines <c>t:name=value</c> and <c>n:name=value</c> separated by line feeds</param>
    private static void AssertDetected(string id, string source, string program, string host, string eventName, string message, string expected)
    {
        var events = Detect(source, program, host, eventName, message);

        Assert.HasCount(1, events, "exactly one event");
        Assert.AreEqual(id, events[0].SignatureId, "signature");
        Assert.AreEqual(SignatureSamples.Expected(expected), SignatureSamples.Fields(events[0]), "fields, no others");
        Assert.AreEqual(1L, events[0].LineCount, "one line");
    }

    /// <summary>
    /// Asserts that one log line gives no event at all.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    private static void AssertNothing(string source, string program, string host, string eventName, string message)
    {
        var events = Detect(source, program, host, eventName, message);

        Assert.IsEmpty(events, "no event");
    }

    /// <summary>
    /// Asserts that one log line gives no event of a signature; it may give the event of another signature.
    /// </summary>
    /// <param name="id">The signature ID</param>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="eventName">The event</param>
    /// <param name="message">The message</param>
    private static void AssertNoneOf(string id, string source, string program, string host, string eventName, string message)
    {
        var events = Detect(source, program, host, eventName, message);

        Assert.DoesNotContain(id, events.Select(detected => detected.SignatureId).ToList(), "no event of this signature");
    }

    #endregion // Methods
}