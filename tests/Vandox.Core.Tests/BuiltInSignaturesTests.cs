using System.Text;

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

    private const string TooManyText = "PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections";
    private const string WriteText = "PHP message: PHP Notice:  fwrite(): Write of 1 bytes failed with errno=28 No space left on device";
    private const string NginxWriteText = "pwrite() \"/x\" failed (28: No space left on device)";
    private const string NginxOpenHead = "[error] 2101#2101: *15 open() \"/var/www/vhosts/example.org/httpdocs/x\" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /";
    private const string NginxUpstreamHead = "[error] 2101#2101: *15 upstream timed out (110: Connection timed out) while reading response header from upstream, client: 198.51.100.7, server: example.org, request: \"GET /x HTTP/1.1\", upstream: \"http://127.0.0.1:7080/";
    private const string NginxNotFoundHead = "[error] 2101#2101: *15 \"/var/www/vhosts/example.org/httpdocs/";
    private const string NginxFcgiHead = "[error] 2101#2101: *15 FastCGI sent in stderr: \"";
    private const string ExifTail = "): Image has corrupt COM section: some software set wrong length information in /var/www/vhosts/example.org/";
    private const string ApacheProxyHead = "[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error '";
    private const string ApacheModuleHead = "[php:warn] [pid 901] [client 198.51.100.7:51234] ";
    private const string ApacheNoticeHead = "[php:notice] [pid 901] [client 198.51.100.7:51234] ";

    #endregion // Constants

    #region Fields

    private static readonly string[] _crashAndRecoveryEvents = ["mariadb.recovery_start", "mariadb.recovery_end", "mariadb.abort"];
    private static readonly string[] _remoteTextSignatures = ["disk_full", "mariadb_too_many_connections", "php_too_many_connections", "php_disk_full", "nginx_disk_full"];

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
        string[] expected = ["oom_kill", "mariadb_crash", "mariadb_recovery_start", "mariadb_recovery_end", "mariadb_too_many_connections", "php_too_many_connections", "service_start", "service_stop", "service_failed", "disk_full", "php_disk_full", "nginx_disk_full", "postfix_fatal", "dovecot_fatal", "boot"];

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
    /// Every signature has an event or an origin condition, and the signatures that read messages with remote text anchor their pattern at the start of the message.
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
    /// The signatures of PHP and nginx read only the sources of the web servers and no program.
    /// </summary>
    [TestMethod]
    public void BuiltInSignaturesWebSignaturesReadOnlyTheWebServerSources()
    {
        // Arrange
        var signatures = BuiltInSignatures.Catalog.Signatures;
        string[] phpSources = ["nginx-error", "sw-cp-server-error", "apache-error"];
        string[] nginxSources = ["nginx-error", "sw-cp-server-error"];

        // Act
        var phpTooMany = signatures.Single(candidate => candidate.Id == "php_too_many_connections");
        var phpDiskFull = signatures.Single(candidate => candidate.Id == "php_disk_full");
        var nginxDiskFull = signatures.Single(candidate => candidate.Id == "nginx_disk_full");

        // Assert
        Assert.AreSequenceEqual(phpSources, phpTooMany.Sources.ToList(), "sources of php_too_many_connections");
        Assert.AreSequenceEqual(phpSources, phpDiskFull.Sources.ToList(), "sources of php_disk_full");
        Assert.AreSequenceEqual(nginxSources, nginxDiskFull.Sources.ToList(), "sources of nginx_disk_full");
        Assert.IsEmpty(phpTooMany.Programs, "php_too_many_connections has no program");
        Assert.IsEmpty(phpDiskFull.Programs, "php_disk_full has no program");
        Assert.IsEmpty(nginxDiskFull.Programs, "nginx_disk_full has no program");
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
        AssertNoneOf("mariadb_too_many_connections", source, program, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// The mysqli and PDO errors about too many connections are recognized at the first PHP message of nginx and Apache lines.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/httpdocs/db.php on line 3; PHP message: PHP Warning:  mysqli_query(): Argument #1 must be of type mysqli, bool given in /var/www/vhosts/example.org/httpdocs/db.php on line 5\" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: \"GET / HTTP/1.1\", upstream: \"fastcgi://unix:/var/www/vhosts/system/example.org/php-fpm.sock:\", host: \"example.org\", referrer: \"https://example.org/\"")]
    [DataRow("sw-cp-server-error", "[error] 1201#0: *42 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli::__construct(): (08004/1040): Too many connections in /opt/psa/admin/plib/Db.php on line 9\" while reading response header from upstream, client: 198.51.100.7, server: , request: \"GET /admin/ HTTP/1.1\", upstream: \"fastcgi://unix:/var/run/sw-engine.sock:\", host: \"panel.example.org:8443\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *16 FastCGI sent in stderr: \"PHP message: PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:3")]
    [DataRow("nginx-error", "[error] 2101#2101: *17 FastCGI sent in stderr: \"PHP message: PHP Fatal error:  Uncaught PDOException: SQLSTATE[08004] [1040] Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:4")]
    [DataRow("apache-error", "[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Fatal error:  Uncaught PDOException: SQLSTATE[HY000] [1040] Too many connections in /var/www/vhosts/example.org/httpdocs/wp-db.php:12', referer: https://example.org/")]
    [DataRow("apache-error", "[fcgid:warn] [pid 3101:tid 140231] [client 2001:db8::7:51234] mod_fcgid: stderr: PHP Warning:  mysqli_real_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/httpdocs/db.php on line 4")]
    [DataRow("apache-error", "[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3")]
    [DataRow("apache-error", "[php:error] [pid 901] [client 198.51.100.7:51234] PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/index.php:3")]
    [DataRow("apache-error", "[php7:warn] [pid 901:tid 140231] [client 198.51.100.7:51234] PHP Warning:  mysqli::real_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3")]
    public void BuiltInSignaturesPhpTooManyConnectionsMatchesMysqliAndPdoErrors(string source, string message)
    {
        AssertDetected("php_too_many_connections", source, string.Empty, string.Empty, string.Empty, message, string.Empty);
    }

    /// <summary>
    /// The write errors of PHP about a full disk are recognized at the first PHP message of nginx and Apache lines.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7\" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: \"GET / HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *16 FastCGI sent in stderr: \"PHP message: PHP Notice:  file_put_contents(): write of 4096 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 9\"")]
    [DataRow("sw-cp-server-error", "[error] 1201#0: *42 FastCGI sent in stderr: \"PHP message: PHP Notice:  SplFileObject::fwrite(): Write of 512 bytes failed with errno=28 No space left on device in /opt/psa/admin/plib/Cache.php on line 20\"")]
    [DataRow("apache-error", "[proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7', referer: https://example.org/")]
    [DataRow("apache-error", "[php:notice] [pid 901] [client 198.51.100.7:51234] PHP Notice:  fflush(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/index.php on line 5")]
    [DataRow("apache-error", "[fcgid:warn] [pid 3101:tid 140231] [client 198.51.100.7:51234] mod_fcgid: stderr: PHP Notice:  fputs(): Write of 512 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/log.php on line 4")]
    public void BuiltInSignaturesPhpDiskFullMatchesWriteErrors(string source, string message)
    {
        AssertDetected("php_disk_full", source, string.Empty, string.Empty, string.Empty, message, string.Empty);
    }

    /// <summary>
    /// The failed writes of nginx with error 28 are recognized at the start of the message.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("nginx-error", "[crit] 2101#2101: *16 pwrite() \"/var/lib/nginx/body/0000000001\" failed (28: No space left on device), client: 198.51.100.7, server: example.org, request: \"POST /upload.php HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", "[crit] 2101#2101: *17 pwritev() \"/var/cache/nginx/fastcgi_temp/3/00/0000000003\" failed (28: No space left on device) while reading upstream, client: 198.51.100.7, server: example.org, request: \"GET /report HTTP/1.1\", upstream: \"fastcgi://unix:/run/php/php8.1-fpm.sock:\", host: \"example.org\"")]
    [DataRow("nginx-error", "[crit] 2101#2101: *18 writev() \"/var/cache/nginx/proxy_temp/1/00/0000000001\" failed (28: No space left on device) while reading upstream, client: 198.51.100.7, server: example.org, request: \"GET / HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", "[alert] 2101#2101: *19 write() to \"/var/log/nginx/access.log\" failed (28: No space left on device) while logging request, client: 198.51.100.7, server: example.org, request: \"GET / HTTP/1.1\", host: \"example.org\"")]
    [DataRow("sw-cp-server-error", "[crit] 1201#0: pwrite() \"/var/cache/sw-cp-server/x\" failed (28: No space left on device)")]
    public void BuiltInSignaturesNginxDiskFullMatchesWriteErrors(string source, string message)
    {
        AssertDetected("nginx_disk_full", source, string.Empty, string.Empty, string.Empty, message, string.Empty);
    }

    /// <summary>
    /// Apache's escaped line feed does not let a later PHP message count.
    /// </summary>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow(ApacheProxyHead + "PHP message: PHP Warning:  x in /x.php on line 1\\n" + TooManyText + " in /x.php on line 2'")]
    [DataRow(ApacheProxyHead + "PHP message: PHP Warning:  x in /x.php on line 1\\n" + WriteText + " in /x.php on line 2'")]
    public void BuiltInSignaturesApacheEscapedLineFeedReadsOnlyTheFirstPhpMessage(string message)
    {
        // Assert
        Assert.Contains("\\", message, "the input holds the backslash of the escape, not a line feed");

        // Act and assert
        AssertNothing("apache-error", string.Empty, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// The mysqli and PDO lines of the wrong source, text, level or origin give no too-many-connections event of PHP.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("plesk-panel", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /x.php on line 3\"")]
    [DataRow("syslog", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /x.php on line 3\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1045): Access denied for user 'u'@'localhost' (using password: YES) in /x.php on line 3\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Fatal error:  Uncaught Exception: Too many connections in /x.php:3")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Fatal error:  Uncaught PDOException: SQLSTATE[HY000] [2002] Connection refused in /x.php:3")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): x in /x.php on line 3\"")]
    [DataRow("nginx-error", "[warn] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /x.php on line 3\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  mysqli_query(): (HY000/1040): Too many connections in /x.php on line 3\"")]
    [DataRow("apache-error", "[:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /x.php on line 3")]
    public void BuiltInSignaturesPhpTooManyConnectionsIgnoresOtherLines(string source, string message)
    {
        AssertNothing(source, string.Empty, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// Other PHP errors, other writers and write errors without a script file give no full-disk event of PHP.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Notice:  fwrite(): Write of 1 bytes failed with errno=122 Disk quota exceeded in /x.php on line 3\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Notice:  fread(): Read of 8192 bytes failed with errno=28 No space left on device in /x.php on line 3\"")]
    [DataRow("nginx-error", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Warning:  file_put_contents(): Only 0 of 4096 bytes written, possibly out of free disk space in /x.php on line 3\"")]
    [DataRow("plesk-panel", "[error] 2101#2101: *15 FastCGI sent in stderr: \"PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /x.php on line 7\"")]
    [DataRow("apache-error", "[fcgid:warn] [pid 3101:tid 140231] [client 198.51.100.7:51234] mod_fcgid: stderr: PHP Notice:  Unknown: Write of 512 bytes failed with errno=28 No space left on device in Unknown on line 0")]
    [DataRow("nginx-error", "[error] 2101#2101: *16 FastCGI sent in stderr: \"PHP message: PHP Notice:  PHP Request Shutdown: Write of 512 bytes failed with errno=28 No space left on device in Unknown on line 0\"")]
    public void BuiltInSignaturesPhpDiskFullIgnoresOtherLines(string source, string message)
    {
        AssertNothing(source, string.Empty, string.Empty, string.Empty, message);
    }

    /// <summary>
    /// Other nginx errors, levels, calls and sources give no full-disk event of nginx.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("apache-error", "[crit] 2101#2101: *16 pwrite() \"/var/lib/nginx/body/0000000001\" failed (28: No space left on device), client: 198.51.100.7")]
    [DataRow("nginx-error", "[error] 2101#2101: *16 pwrite() \"/x\" failed (28: No space left on device)")]
    [DataRow("nginx-error", "[crit] 2101#2101: *16 open() \"/var/lib/nginx/body/0000000001\" failed (28: No space left on device)")]
    [DataRow("nginx-error", "[emerg] 2101#2101: mkdir() \"/var/lib/nginx/body\" failed (28: No space left on device)")]
    [DataRow("nginx-error", "[crit] 2101#2101: *16 pwrite() \"/x\" failed (5: Input/output error)")]
    [DataRow("apache-error", "[core:error] [pid 812] (28)No space left on device: AH00023: Couldn't create the ssl-cache mutex")]
    public void BuiltInSignaturesNginxDiskFullIgnoresOtherLines(string source, string message)
    {
        AssertNothing(source, string.Empty, string.Empty, string.Empty, message);
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
        AssertNoneOf("disk_full", source, program, string.Empty, string.Empty, message);
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
    [DataRow("nginx-error", NginxOpenHead + TooManyText + " HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"" + TooManyText + "\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"example.org\", referrer: \"" + TooManyText + "\"")]
    [DataRow("nginx-error", NginxUpstreamHead + TooManyText + "\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxNotFoundHead + TooManyText + "/index.html\" is not found (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /" + TooManyText + "/ HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxOpenHead + WriteText + " HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"" + WriteText + "\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"example.org\", referrer: \"" + WriteText + "\"")]
    [DataRow("nginx-error", NginxUpstreamHead + WriteText + "\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxNotFoundHead + WriteText + "/index.html\" is not found (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /" + WriteText + "/ HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxOpenHead + NginxWriteText + " HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"" + NginxWriteText + "\"")]
    [DataRow("nginx-error", NginxOpenHead + "x HTTP/1.1\", host: \"example.org\", referrer: \"" + NginxWriteText + "\"")]
    [DataRow("nginx-error", NginxUpstreamHead + NginxWriteText + "\", host: \"example.org\"")]
    [DataRow("nginx-error", NginxNotFoundHead + NginxWriteText + "/index.html\" is not found (2: No such file or directory), client: 198.51.100.7, server: example.org, request: \"GET /" + NginxWriteText + "/ HTTP/1.1\", host: \"example.org\"")]
    [DataRow("nginx-error", "[crit] 2101#2101: *16 stat() \"/var/www/vhosts/example.org/httpdocs/x\" failed (28: No space left on device)\" failed (13: Permission denied), client: 198.51.100.7, server: example.org, request: \"GET /x%22%20failed%20(28:%20No%20space%20left%20on%20device) HTTP/1.1\"")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Warning:  Undefined array key \"q\" in /var/www/vhosts/example.org/httpdocs/index.php on line 2; " + TooManyText + " in /x.php on line 1\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Warning:  Undefined array key \"q\" in /var/www/vhosts/example.org/httpdocs/index.php on line 2; " + WriteText + " in /x.php on line 1\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Warning:  Undefined array key \"" + TooManyText + "\" in /var/www/vhosts/example.org/httpdocs/index.php on line 2\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Warning:  getimagesize(): (HY000/1040): Too many connections): Failed to open stream: No such file or directory in /var/www/vhosts/example.org/httpdocs/img.php on line 4\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("apache-error", ApacheModuleHead + "PHP Warning:  include(): (HY000/1040): Too many connections.php): Failed to open stream: No such file or directory in /var/www/vhosts/example.org/index.php on line 3")]
    [DataRow("apache-error", ApacheProxyHead + "PHP message: PHP Warning:  file_get_contents(): (HY000/1040): Too many connections): Failed to open stream: No such file or directory in /x.php on line 4'")]
    [DataRow("apache-error", ApacheModuleHead + "PHP Warning:  fopen(): Write of 1 bytes failed with errno=28 No space left on device): Failed to open stream: No such file or directory in /x.php on line 4")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Notice:  exif_read_data(): Write of 1 bytes failed with errno=28 No space left on device" + ExifTail + "httpdocs/upload.php on line 9\" while reading response header from upstream, client: 198.51.100.7")]
    [DataRow("apache-error", ApacheNoticeHead + "PHP Notice:  exif_read_data(): Write of 1 bytes failed with errno=28 No space left on device" + ExifTail + "upload.php on line 9")]
    [DataRow("apache-error", ApacheNoticeHead + "PHP Notice:  exif_read_data(): Write of 1 bytes failed with errno=28 No space left on device in Unknown on line 0\"" + ExifTail + "upload.php on line 9")]
    [DataRow("nginx-error", NginxFcgiHead + "PHP message: PHP Warning:  Undefined array key \"q\nPHP message: PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /x.php on line 1\" in /var/www/vhosts/example.org/httpdocs/index.php on line 2\"")]
    [DataRow("apache-error", ApacheProxyHead + "PHP message: PHP Warning:  Undefined variable $x in /var/www/vhosts/example.org/httpdocs/index.php on line 12', referer: " + TooManyText)]
    [DataRow("apache-error", ApacheProxyHead + "PHP message: PHP Warning:  Undefined variable $x in /var/www/vhosts/example.org/httpdocs/index.php on line 12', referer: " + WriteText)]
    [DataRow("apache-error", "[authz_core:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01630: client denied by server configuration: /var/www/vhosts/example.org/httpdocs/" + TooManyText)]
    [DataRow("apache-error", ApacheProxyHead + "PHP message: PHP Warning:  Undefined array key \"q\" in /x.php on line 2; " + TooManyText + " in /x.php on line 1'")]
    [DataRow("apache-error", "[proxy_fcgi:error] [pid 3101:tid 140231] [client example.org:0] AH01071: Got error '" + TooManyText + " in /x.php on line 1'")]
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
    /// Lines of the nginx and Apache error logs give the events of PHP and nginx with their records, and the copies of one full disk are one event.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents()
    {
        // Arrange
        const string nginxContent = "2026/10/12 10:00:00 [crit] 2101#2101: *16 pwrite() \"/var/lib/nginx/body/0000000001\" failed (28: No space left on device), client: 198.51.100.7, server: example.org, request: \"POST /upload.php HTTP/1.1\", host: \"example.org\"\n"
                                    + "2026/10/12 10:00:01 [crit] 2101#2101: *17 pwrite() \"/var/lib/nginx/body/0000000002\" failed (28: No space left on device), client: 198.51.100.8, server: example.org, request: \"POST /upload.php HTTP/1.1\", host: \"example.org\"\n"
                                    + "2026/10/12 10:00:02 [error] 2101#2101: *18 FastCGI sent in stderr: \"PHP message: PHP Fatal error:  Uncaught mysqli_sql_exception: Too many connections in /var/www/vhosts/example.org/httpdocs/db.php:3\n"
                                    + "Stack trace:\n"
                                    + "#0 /var/www/vhosts/example.org/httpdocs/db.php(3): mysqli->__construct()\n"
                                    + "#1 {main}\n"
                                    + "  thrown in /var/www/vhosts/example.org/httpdocs/db.php on line 3\" while reading response header from upstream, client: 198.51.100.7, server: example.org, request: \"GET / HTTP/1.1\", host: \"example.org\"\n";
        const string apacheContent = "[Mon Oct 12 10:00:03.123456 2026] [proxy_fcgi:error] [pid 3101:tid 140231] [client 198.51.100.7:0] AH01071: Got error 'PHP message: PHP Notice:  fwrite(): Write of 8192 bytes failed with errno=28 No space left on device in /var/www/vhosts/example.org/httpdocs/cache.php on line 7', referer: https://example.org/\n";
        var zone = DateTimeZone.Utc;
        var nginx = await ParseContentAsync(new ErrorLogParser(ErrorLogParser.NginxType, ErrorLogFormat.Nginx, ErrorLogPaths.DefaultNginx, true, zone), new LogFile("logs/sample", null), nginxContent, 0, TestContext.CancellationToken);
        var apache = await ParseContentAsync(new ErrorLogParser(ErrorLogParser.ApacheType, ErrorLogFormat.Apache, ErrorLogPaths.DefaultApache, true, zone), new LogFile("logs/sample", null), apacheContent, nginx.Count, TestContext.CancellationToken);
        var records = nginx.Concat(apache).OrderBy(pair => pair.Record.CapturedAt).ThenBy(pair => pair.Id).ToList();

        // Act
        var events = SignatureSamples.Run(BuiltInSignatures.Catalog, records);

        // Assert
        Assert.AreSequenceEqual(["nginx_disk_full", "php_too_many_connections", "php_disk_full"], events.Select(detected => detected.SignatureId).ToList(), "events in time order");
        Assert.AreSequenceEqual([IdOf(records, "body/0000000001"), IdOf(records, "body/0000000002")], events[0].RecordIds.ToList(), "the two pwrite lines");
        Assert.AreEqual(2L, events[0].LineCount, "two nginx lines");
        Assert.AreSequenceEqual([IdOf(records, "Uncaught mysqli_sql_exception")], events[1].RecordIds.ToList(), "the Uncaught line");
        Assert.AreEqual(1L, events[1].LineCount, "one connection line");
        Assert.AreSequenceEqual([IdOf(records, "Write of 8192 bytes")], events[2].RecordIds.ToList(), "the Apache line");
        Assert.AreEqual(1L, events[2].LineCount, "one write line");
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

        return await ParseBytesAsync(parser, file, content, 0, path, cancellationToken);
    }

    /// <summary>
    /// Parses inline content and numbers the records after a given count.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="file">The file the content stands for</param>
    /// <param name="text">The content</param>
    /// <param name="idOffset">The number of IDs already used; the first record gets the next one</param>
    /// <param name="cancellationToken">The token the parser honors</param>
    /// <returns>A task that returns the records with their IDs</returns>
    private static Task<List<(long Id, DataRecord Record)>> ParseContentAsync(ILogParser parser, LogFile file, string text, int idOffset, CancellationToken cancellationToken)
    {
        return ParseBytesAsync(parser, file, Encoding.UTF8.GetBytes(text), idOffset, file.Name, cancellationToken);
    }

    /// <summary>
    /// Parses bytes and numbers the records after a given count.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="file">The file the bytes stand for</param>
    /// <param name="content">The bytes</param>
    /// <param name="idOffset">The number of IDs already used</param>
    /// <param name="name">The name used in assert messages</param>
    /// <param name="cancellationToken">The token the parser honors</param>
    /// <returns>A task that returns the records with their IDs</returns>
    private static async Task<List<(long Id, DataRecord Record)>> ParseBytesAsync(ILogParser parser, LogFile file, byte[] content, int idOffset, string name, CancellationToken cancellationToken)
    {
        var emitter = await RecordingEmitter.ParseAsync(parser, file, content, cancellationToken);

        Assert.IsEmpty(emitter.Skips, $"nothing of {name} is skipped");

        return emitter.Records.Select((record, index) => ((long)idOffset + index + 1, record)).ToList();
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