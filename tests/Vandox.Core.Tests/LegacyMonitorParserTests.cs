using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

using Samples = Vandox.Core.Tests.LegacyMonitorSamples;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyMonitorParser"/>
/// </summary>
[TestClass]
public partial class LegacyMonitorParserTests
{
    #region Constants

    private const string TruncatedSnapshot = "truncated snapshot";
    private const string MalformedSnapshot = "malformed snapshot";
    private const string BeforeFirst = "line before the first snapshot";
    private const string RowNotRead = "process row not read";
    private const string DuplicateProcess = "duplicate process id";
    private const string MeminfoMarker = "----- meminfo -----";
    private const string PressureMarker = "----- pressure -----";
    private const string VmstatMarker = "----- vmstat -----";
    private const string LsofMarker = "----- lsof -----";
    private const string OldFixture = "testdata/logs/legacy-monitor-old.log";
    private const string NewFixture = "testdata/logs/legacy-monitor-new.log";
    private const string ListenSocket = "*:ssh (LISTEN)";
    private const string UnmappedSocket = "sshd 100 root 3u IPv4 1 0t0 TCP 127.1:22 (LISTEN)";
    private const string FirstUtc = "2026-10-02T07:15:01Z";
    private const string SecondUtc = "2026-10-02T07:20:01Z";
    private const string ThirdUtc = "2026-10-02T07:25:01Z";
    private const double Delta = 1e-9;
    private const string TraditionalHead = "Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root\n";
    private const string JournalHead = "__CURSOR=s=0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1\n__REALTIME_TIMESTAMP=1772368215123456\nMESSAGE=hello\n\n";
    private const string MariaDbHead = "2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed\n";
    private const string DovecotHead = "Oct 09 03:12:12 master: Info: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)\n";
    private const string PleskHead = "[2026-10-09 03:12:12.123] 4821:5f3a9c1e INFO [panel] Session opened\n";
    private const string NginxHead = "2026/10/09 03:12:12 [error] 1021#1021: *15 connect() failed\n";
    private const string ApacheHead = "[Fri Oct 09 03:12:12.123456 2026] [core:error] [pid 1] x\n";
    private const string LegacyHeadLine = "===== Fri Oct  2 09:15:01 CEST 2026 =====\n";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeZone _berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];
    private static readonly LogFile _file = new("monitor.log", null);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The parser has the type legacy-monitor and is framed by lines.
    /// </summary>
    [TestMethod]
    public void LegacyMonitorParserHasTypeAndIsLineFramed()
    {
        // Act
        var parser = new LegacyMonitorParser(DateTimeZone.Utc);

        // Assert
        Assert.AreEqual("legacy-monitor", parser.Type, "the type");
        Assert.AreEqual(LegacyMonitorParser.ParserType, parser.Type, "the constant");
        Assert.IsTrue(parser.LineFramed, "line framed");
        Assert.IsTrue(ParserTypes.IsValid(parser.Type), "the type is valid");
    }

    /// <summary>
    /// A file whose first line is a valid header is claimed by content, also with a byte order mark and a carriage return.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head</param>
    [TestMethod]
    [DataRow("monitor.log", LegacyHeadLine)]
    [DataRow("logs/top.log", LegacyHeadLine + Samples.Load + "\n")]
    [DataRow("notes.txt", "﻿===== Fri Oct  2 09:15:01 CEST 2026 =====\r\ntop - 09:15:01 up\r\n")]
    [DataRow("monitor.log", "===== Sun Oct 25 02:30:00 CET 2026 =====\n")]
    [DataRow("monitor.log", "===== Mon Jan 05 00:00:00 UTC 2026 =====\n")]
    public void LegacyMonitorParserDetectClaimsHeaderFirstLine(string name, string head)
    {
        // Arrange
        var parser = new LegacyMonitorParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.MatchContent, confidence, "claimed by content");
    }

    /// <summary>
    /// Other heads, a header that is not the first line, an invalid header and syslog file names are not claimed.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="head">The head</param>
    [TestMethod]
    [DataRow("monitor.log", "")]
    [DataRow("monitor.log", "\n" + LegacyHeadLine)]
    [DataRow("monitor.log", "garbage\n" + LegacyHeadLine)]
    [DataRow("monitor.log", "===== Fri Oct  2 09:15:01 CEST 2026 ===== x\n")]
    [DataRow("monitor.log", "===== Fri Oct  2 09:15:01 CEST =====\n")]
    [DataRow("monitor.log", "===== anything =====\n")]
    [DataRow("monitor.log", Samples.Load + "\n")]
    [DataRow("monitor.log", TraditionalHead)]
    [DataRow("monitor.log", JournalHead)]
    [DataRow("monitor.log", MariaDbHead)]
    [DataRow("monitor.log", DovecotHead)]
    [DataRow("monitor.log", NginxHead)]
    [DataRow("monitor.log", ApacheHead)]
    [DataRow("monitor.log", PleskHead)]
    [DataRow("syslog", LegacyHeadLine)]
    [DataRow("kern.log.1", LegacyHeadLine)]
    public void LegacyMonitorParserDetectRefusesOtherHeads(string name, string head)
    {
        // Arrange
        var parser = new LegacyMonitorParser(DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.NoMatch, confidence, "not claimed");
    }

    /// <summary>
    /// The first valid header of a parse needs the time zone option.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseThrowsWithoutTimeZone()
    {
        // Arrange
        var parser = new LegacyMonitorParser(null);
        var content = Samples.Text(Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken));

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "the message");
        Assert.AreEqual(SyslogParser.TimeZoneNotSet, exception.Message, "the text of the syslog parser");
    }

    /// <summary>
    /// A file without a valid header does not need the time zone option.
    /// </summary>
    /// <param name="content">The content</param>
    /// <param name="reason">The expected reason of the first skip</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("garbage\nmore garbage\n", BeforeFirst)]
    [DataRow("===== Fri Oct  2 09:15:01 CEST =====\ntop - x\n", "invalid snapshot header")]
    [DataRow("", "")]
    public async Task LegacyMonitorParserParseWithoutHeaderDoesNotNeedTimeZone(string content, string reason)
    {
        // Arrange
        var parser = new LegacyMonitorParser(null);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, _file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Records, "no records");
        Assert.AreEqual(reason, emitter.Skips.Count > 0 ? emitter.Skips[0].Reason : string.Empty, "the first skip");
    }

    /// <summary>
    /// A time zone that does not match the first header of a parse makes the parse fail with a fixed message.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseThrowsWhenFirstZoneDoesNotMatch()
    {
        // Arrange
        var content = Samples.Text(Samples.Old("Fri Oct  2 09:15:01 EST 2026", Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await ParseAsync(content));

        // Assert
        Assert.AreEqual("the time zone of the first snapshot header does not match import.time_zone", exception.Message, "the message");
        Assert.AreEqual(LegacyMonitorParser.ZoneMismatchFirst, exception.Message, "the constant");
    }

    /// <summary>
    /// After a header of the parse resolved, a snapshot with another zone name is skipped at its header line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsLaterSnapshotWithOtherZone()
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = Samples.Old(Samples.Oct2, row);
        var otherLine = lines.Count + 1;

        lines.AddRange(Samples.Old("Fri Oct  2 09:20:01 EST 2026", row));
        lines.AddRange(Samples.Old(Samples.Oct2Last, row));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.HasCount(1, emitter.Skips, "one skip");
        Assert.AreEqual((long)otherLine, emitter.Skips[0].Line, "at the header line");
        Assert.AreEqual("time zone does not match import.time_zone", emitter.Skips[0].Reason, "the reason");
        Assert.AreSequenceEqual([Utc(FirstUtc), Utc(ThirdUtc)], emitter.Records.Select(record => record.CapturedAt).Distinct().ToList(), "the other snapshots are read");
    }

    /// <summary>
    /// Snapshots across the end of daylight saving time are ordered by their instants, also in the repeated hour.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseOrdersSnapshotsAcrossDaylightSavingEnd()
    {
        // Arrange
        string[] dates = [
                             "Sun Oct 25 01:55:01 CEST 2026",
                             "Sun Oct 25 02:00:01 CEST 2026",
                             "Sun Oct 25 02:30:01 CEST 2026",
                             "Sun Oct 25 02:55:01 CEST 2026",
                             "Sun Oct 25 02:00:01 CET 2026",
                             "Sun Oct 25 02:05:01 CET 2026",
                             "Sun Oct 25 02:55:01 CET 2026",
                             "Sun Oct 25 03:00:01 CET 2026"
                         ];
        string[] expected = [
                                "2026-10-24T23:55:01Z",
                                "2026-10-25T00:00:01Z",
                                "2026-10-25T00:30:01Z",
                                "2026-10-25T00:55:01Z",
                                "2026-10-25T01:00:01Z",
                                "2026-10-25T01:05:01Z",
                                "2026-10-25T01:55:01Z",
                                "2026-10-25T02:00:01Z"
                            ];
        var lines = dates.SelectMany(date => Samples.Old(date, Samples.Row(100, "root", "1000", "apache2"))).ToList();

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var instants = Metrics(emitter.Records).Where(metric => metric.Point.Name == "load1").Select(metric => metric.Record.CapturedAt).ToList();

        // Assert
        Assert.AreSequenceEqual(expected.Select(Utc).ToList(), instants, "the instants of the snapshots");
        Assert.IsEmpty(emitter.Skips, "no skip");

        for (var index = 1; index < instants.Count; index++)
        {
            Assert.IsGreaterThan(instants[index - 1], instants[index], $"snapshot {index} is after the one before");
        }
    }

    /// <summary>
    /// The summary lines become metrics of origin import in a fixed order, with units, labels and values in bytes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseEmitsSummaryMetricsInOrder()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "www-data", "2.5m", "spamd"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var records = emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).ToList();
        var metrics = Metrics(records);

        // Assert
        Assert.HasCount(20, records, "nineteen metrics and the process snapshot");
        Assert.AreSequenceEqual(Samples.SummaryNames, metrics.Select(metric => metric.Point.Name).ToList(), "the names in order");
        Assert.IsTrue(records.All(record => record.Origin == RecordOrigin.Import && record.Source == Samples.Source && record.Seq == 0UL), "origin, source and sequence");

        double[] values = [
                              0.52,
                              0.48,
                              0.45,
                              3.1,
                              1.2,
                              0.0,
                              95.0,
                              0.5,
                              0.0,
                              0.2,
                              0.0,
                              131926528d * 1024,
                              1234567d * 1024,
                              80000000d * 1024,
                              50000000d * 1024,
                              49000000d * 1024,
                              8388604d * 1024,
                              8388604d * 1024,
                              0.0
                          ];

        for (var index = 0; index < values.Length; index++)
        {
            Assert.AreEqual(values[index], metrics[index].Point.Value, Delta, $"value of {metrics[index].Point.Name} at {index}");
        }

        string[] modes = ["user", "system", "nice", "idle", "iowait", "irq", "softirq", "steal"];

        Assert.AreSequenceEqual(modes, metrics.Skip(3).Take(8).Select(metric => metric.Point.Labels?["mode"] ?? string.Empty).ToList(), "the CPU modes");
        Assert.IsTrue(metrics.Skip(3).Take(8).All(metric => metric.Point.Unit == "percent"), "CPU unit");
        Assert.IsTrue(metrics.Skip(11).All(metric => metric.Point.Unit == "bytes"), "memory unit");
        Assert.IsTrue(metrics.Take(3).All(metric => metric.Point.Unit.Length == 0), "load has no unit");
        Assert.IsTrue(metrics.Where(metric => metric.Point.Name != "cpu_percent").All(metric => (metric.Point.Labels?.Count ?? 0) == 0), "only the CPU metrics carry labels");
        Assert.IsInstanceOfType<ProcessSnapshot>(records[19].Data, "the process snapshot follows the metrics");
    }

    /// <summary>
    /// A table in the old format becomes a process snapshot with the normalized command, the cut flag and per-program sums.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsOldFormatProcessSnapshot()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "www-data", "2.5m", "apache2"), Samples.Row(102, "psaadm", "123.4m", "sw-engi+"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.IsTrue(snapshot.Complete, "three rows of three tasks");
        Assert.AreEqual(3u, snapshot.Total, "total from the Tasks line");
        Assert.IsNotNull(snapshot.Processes, "processes");
        Assert.AreSequenceEqual([100, 101, 102], snapshot.Processes.Select(process => process.Pid).ToList(), "process IDs");
        Assert.AreSequenceEqual(["root", "www-data", "psaadm"], snapshot.Processes.Select(process => process.User).ToList(), "users");
        Assert.AreSequenceEqual(["apache2", "apache2", "sw-engi"], snapshot.Processes.Select(process => process.Command).ToList(), "commands");
        Assert.AreSequenceEqual([false, false, true], snapshot.Processes.Select(process => process.Truncated).ToList(), "cut flags");
        Assert.AreSequenceEqual([1024000UL, 2621440UL, 129394278UL], snapshot.Processes.Select(process => process.RssBytes).ToList(), "resident bytes");
        Assert.IsTrue(snapshot.Processes.All(process => process.Cmdline.Length == 0 && process.State == "S" && Math.Abs(process.CpuPercent - 1.5) < Delta && process.PssBytes is null), "state, CPU and empty command line");
        Assert.IsNotNull(snapshot.Programs, "programs");
        Assert.AreSequenceEqual(["apache2", "sw-engi"], snapshot.Programs.Select(program => program.Program).ToList(), "programs in order of first appearance");
        Assert.AreSequenceEqual([2u, 1u], snapshot.Programs.Select(program => program.Count).ToList(), "counts");
        Assert.AreSequenceEqual([3645440UL, 129394278UL], snapshot.Programs.Select(program => program.RssBytes).ToList(), "resident sums");
        Assert.AreSequenceEqual([true, false], snapshot.Programs.Select(program => program.RssOvercounted).ToList(), "overcounted flags");
        Assert.AreEqual(3.0, snapshot.Programs[0].CpuPercent, Delta, "CPU sum");
    }

    /// <summary>
    /// A table in the new format carries the command line, derives the command from it and cuts the command line at 2,048 bytes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsNewFormatProcessSnapshot()
    {
        // Arrange
        var longLine = "/usr/bin/long " + new string('x', 3000);
        var lines = Samples.New(Samples.Oct2,
                                [MeminfoMarker, "MemTotal: 1000 kB"],
                                Samples.Row(200, "www-data", "1204", "/usr/sbin/apache2 -k start"),
                                Samples.Row(201, "root", "0", "[kworker/0:1-events]"),
                                Samples.Row(202, "psaadm", "44.8m", "php-fpm: pool www"),
                                Samples.Row(203, "mysql", "300", "/usr/sbin/mariadbd --user=mysql+"),
                                Samples.Row(204, "root", "1", longLine));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.IsNotNull(snapshot.Processes, "processes");
        Assert.AreSequenceEqual(["apache2", "kworker/0:1-events", "php-fpm", "mariadbd", "long"], snapshot.Processes.Select(process => process.Command).ToList(), "commands");
        Assert.AreEqual("/usr/sbin/apache2 -k start", snapshot.Processes[0].Cmdline, "command line");
        Assert.AreEqual("[kworker/0:1-events]", snapshot.Processes[1].Cmdline, "bracketed command line");
        Assert.AreEqual("php-fpm: pool www", snapshot.Processes[2].Cmdline, "command line with spaces");
        Assert.AreEqual("/usr/sbin/mariadbd --user=mysql", snapshot.Processes[3].Cmdline, "the plus sign is removed");
        Assert.AreSequenceEqual([false, false, false, true, true], snapshot.Processes.Select(process => process.Truncated).ToList(), "cut flags");
        Assert.AreEqual(2048, Encoding.UTF8.GetByteCount(snapshot.Processes[4].Cmdline), "command line cut at 2,048 bytes");
        Assert.IsNotNull(snapshot.Programs, "programs");
        Assert.AreSequenceEqual(["apache2", "kworker/0:1-events", "php-fpm", "mariadbd", "long"], snapshot.Programs.Select(program => program.Program).ToList(), "programs");
    }

    /// <summary>
    /// A snapshot with fewer rows than tasks is marked incomplete and keeps the total.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseMarksSnapshotIncompleteWhenRowsMissing()
    {
        // Arrange
        var lines = new List<string>
                    {
                        Samples.Header(Samples.Oct2)
                    };

        lines.AddRange(Samples.Top(5, Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "root", "1000", "apache2"), Samples.Row(102, "root", "1000", "spamd")));
        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.IsFalse(snapshot.Complete, "three rows of five tasks");
        Assert.AreEqual(5u, snapshot.Total, "total from the Tasks line");
        Assert.HasCount(3, snapshot.Processes!, "three rows");
        Assert.IsEmpty(emitter.Skips, "no skip");
    }

    /// <summary>
    /// A command with no derivable name still gives a process snapshot that passes the validation.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseKeepsSnapshotWithEmptyDerivedNames()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(300, "root", "1", "+"));

        lines.AddRange(Samples.New(Samples.Oct2Later, [MeminfoMarker, "MemTotal: 1000 kB"], Samples.Row(301, "u", "1", "/opt/x/"), Samples.Row(302, "u", "1", ":"), Samples.Row(303, "u", "1", "[]")));
        lines.AddRange(Samples.Old(Samples.Oct2Last, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshots = Payloads<ProcessSnapshot>(emitter.Records);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip and no record refused by the validation");
        Assert.HasCount(3, snapshots, "three process snapshots");
        Assert.AreEqual("+", snapshots[0].Processes![0].Command, "the old command is the column as printed");
        Assert.IsTrue(snapshots[0].Processes![0].Truncated, "the plus sign marks a cut");
        Assert.AreEqual("+", snapshots[0].Programs![0].Program, "the program is the command");
        Assert.AreSequenceEqual(["/opt/x/", ":", "[]"], snapshots[1].Processes!.Select(process => process.Command).ToList(), "new commands");
        Assert.AreSequenceEqual(["/opt/x/", ":", "[]"], snapshots[1].Processes!.Select(process => process.Cmdline).ToList(), "new command lines");
        Assert.AreSequenceEqual(["/opt/x/", ":", "[]"], snapshots[1].Programs!.Select(program => program.Program).ToList(), "new programs");
    }

    /// <summary>
    /// The resident memory sum of a program saturates at the largest 64-bit value instead of wrapping.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSaturatesProgramRssSum()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(100, "root", "15.999e", "apache2"), Samples.Row(101, "root", "15.999e", "apache2"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.AreSequenceEqual([18445591152204944769UL, 18445591152204944769UL], snapshot.Processes!.Select(process => process.RssBytes).ToList(), "each process");
        Assert.AreEqual(18446744073709551615UL, snapshot.Programs![0].RssBytes, "the sum saturates");
        Assert.AreEqual(2u, snapshot.Programs[0].Count, "count");
        Assert.IsTrue(snapshot.Programs[0].RssOvercounted, "overcounted");
    }

    /// <summary>
    /// Processes are summed per program, the program is the first word of the command, and the programs keep the order of first appearance.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseAggregatesPerProgram()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2,
                                Samples.Row(1, "www-data", "100", "apache2", "1.0"),
                                Samples.Row(2, "popuser", "400", "spamd", "0.5"),
                                Samples.Row(3, "www-data", "200", "apache2", "2.0"),
                                Samples.Row(4, "root", "600", "cron", "0.5"),
                                Samples.Row(5, "popuser", "500", "spamd child", "2.5"),
                                Samples.Row(6, "www-data", "300", "apache2", "3.0"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var programs = Payloads<ProcessSnapshot>(emitter.Records)[0].Programs!;

        // Assert
        Assert.AreSequenceEqual(["apache2", "spamd", "cron"], programs.Select(program => program.Program).ToList(), "programs in order of first appearance");
        Assert.AreSequenceEqual([3u, 2u, 1u], programs.Select(program => program.Count).ToList(), "counts");
        Assert.AreSequenceEqual([6.0, 3.0, 0.5], programs.Select(program => program.CpuPercent).ToList(), "CPU sums");
        Assert.AreSequenceEqual([614400UL, 921600UL, 614400UL], programs.Select(program => program.RssBytes).ToList(), "resident sums");
        Assert.AreSequenceEqual([true, true, false], programs.Select(program => program.RssOvercounted).ToList(), "overcounted exactly when more than one");
        Assert.IsTrue(programs.All(program => program.PssBytes is null), "no proportional memory");
    }

    /// <summary>
    /// A snapshot cut before its table header is skipped whole with one skip at its header line, and the snapshots before it are read.
    /// </summary>
    /// <param name="cut">Where the file is cut</param>
    /// <param name="lenient">Whether a malformed snapshot is as good a reason as a truncated one</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("header", false)]
    [DataRow("summary", false)]
    [DataRow("blank", false)]
    [DataRow("table", true)]
    public async Task LegacyMonitorParserParseSkipsTruncatedLastSnapshot(string cut, bool lenient)
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "root", "1000", "spamd"));
        var cutLine = lines.Count + 1;
        var top = Samples.Top(2);

        lines.Add(Samples.Header(Samples.Oct2Later));

        switch (cut)
        {
            case "summary":
                {
                    lines.AddRange(top.Take(3));
                }
                break;

            case "blank":
                {
                    lines.AddRange(top.Take(6));
                }
                break;

            case "table":
                {
                    lines.AddRange(top.Take(6));
                    lines.Add("  PID USER      PR  NI");
                }
                break;
        }

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.HasCount(1, emitter.Skips, "one skip");
        Assert.AreEqual((long)cutLine, emitter.Skips[0].Line, "at the header line");
        Assert.IsTrue(emitter.Skips[0].Reason == TruncatedSnapshot || (lenient && emitter.Skips[0].Reason == MalformedSnapshot), $"the reason: {emitter.Skips[0].Reason}");
        Assert.HasCount(20, emitter.Records, "only the first snapshot is read");
        Assert.IsTrue(emitter.Records.All(record => record.CapturedAt == Utc(FirstUtc)), "all records are those of the first snapshot");
    }

    /// <summary>
    /// A snapshot with the signs of another run's output among its rows is skipped whole, and the snapshots around it are read.
    /// </summary>
    /// <param name="foreign">The line that does not belong in the table</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(Samples.Load)]
    [DataRow("Tasks: 99 total,   1 running")]
    [DataRow(Samples.Cpu)]
    [DataRow(Samples.Mem)]
    [DataRow(Samples.Swap)]
    [DataRow("MiB Mem :  128834.5 total,    120.6 free,  78125.0 used,  48828.1 buff/cache")]
    [DataRow(Samples.TableHeader)]
    public async Task LegacyMonitorParserParseSkipsInterleavedSnapshot(string foreign)
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = Samples.Old(Samples.Oct2, row, row.Replace("100 ", "101 "), row.Replace("100 ", "102 "));
        var bHeader = lines.Count + 1;

        lines.Add(Samples.Header(Samples.Oct2Later));
        lines.AddRange(Samples.Top(3, row));
        lines.Add(foreign);
        lines.Add(row.Replace("100 ", "103 "));
        lines.AddRange(Samples.Old(Samples.Oct2Last, row, row.Replace("100 ", "101 "), row.Replace("100 ", "102 ")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.HasCount(1, emitter.Skips, "one skip");
        Assert.AreEqual(((long)bHeader, MalformedSnapshot), emitter.Skips[0], "at the header line of the interleaved snapshot");
        Assert.HasCount(40, emitter.Records, "the records of the snapshots around it");
        Assert.AreSequenceEqual([Utc(FirstUtc), Utc(ThirdUtc)], emitter.Records.Select(record => record.CapturedAt).Distinct().ToList(), "the instants of the snapshots that are read");
    }

    /// <summary>
    /// A top part with a missing or out-of-order summary line, an unreadable summary line or a section before the table is skipped whole.
    /// </summary>
    /// <param name="defect">The defect</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no-tasks")]
    [DataRow("swapped")]
    [DataRow("unreadable-memory")]
    [DataRow("unreadable-load")]
    [DataRow("marker-before-table")]
    [DataRow("garbage")]
    public async Task LegacyMonitorParserParseSkipsMalformedTopPart(string defect)
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = Samples.Old(Samples.Oct2, row);
        var bHeader = lines.Count + 1;
        var broken = BrokenTop(defect, row);

        lines.Add(Samples.Header(Samples.Oct2Later));
        lines.AddRange(broken);
        lines.AddRange(Samples.Old(Samples.Oct2Last, row));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.AreEqual(((long)bHeader, MalformedSnapshot), emitter.Skips.Single(), "one skip at the header line");
        Assert.AreSequenceEqual([Utc(FirstUtc), Utc(ThirdUtc)], emitter.Records.Select(record => record.CapturedAt).Distinct().ToList(), "the instants of the snapshots that are read");
    }

    /// <summary>
    /// A snapshot whose header is not valid, or does not resolve to a time, is skipped with the reason and its lines give no further skip.
    /// </summary>
    /// <param name="date">The text of the header</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("Fri Oct  2 09:20:01 CEST", "invalid snapshot header")]
    [DataRow("Fri Feb 30 09:20:01 CET 2026", "invalid date")]
    [DataRow("Fri Oct  2 24:00:00 CEST 2026", "invalid date")]
    [DataRow("Sun Mar 29 02:30:00 CET 2026", "invalid date")]
    [DataRow("Fri Oct  2 09:20:01 CEST 9999", "time outside the storable range")]
    [DataRow("Fri Oct  2 09:20:01 CEST 0001", "time outside the storable range")]
    public async Task LegacyMonitorParserParseSkipsSnapshotWithInvalidHeader(string date, string reason)
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = Samples.Old(Samples.Oct2, row);
        var badLine = lines.Count + 1;

        lines.AddRange(Samples.New(date, [MeminfoMarker, "MemTotal: 1 kB", LsofMarker, Samples.LsofHeader, Samples.Socket("sshd", 100, "IPv4", 1, "TCP", ListenSocket)], row));
        lines.AddRange(Samples.Old(Samples.Oct2Last, row));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.AreEqual(((long)badLine, reason), emitter.Skips.Single(), "one skip at the header line");
        Assert.HasCount(40, emitter.Records, "the records of the snapshots around it");
        Assert.AreSequenceEqual([Utc(FirstUtc), Utc(ThirdUtc)], emitter.Records.Select(record => record.CapturedAt).Distinct().ToList(), "the instants of the snapshots that are read");
    }

    /// <summary>
    /// A row whose process ID an earlier row has is left out at its line, the first row is kept and the snapshot is marked incomplete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseDropsDuplicatePidRow()
    {
        // Arrange
        var lines = Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "root", "1000", "spamd"), Samples.Row(100, "other", "2000", "nginx"));
        var duplicateLine = lines.Count;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.AreEqual(((long)duplicateLine, DuplicateProcess), emitter.Skips.Single(), "one skip at the row");
        Assert.AreSequenceEqual(["apache2", "spamd"], snapshot.Processes!.Select(process => process.Command).ToList(), "the first row is kept");
        Assert.IsFalse(snapshot.Complete, "a row was left out");
        Assert.AreEqual(3u, snapshot.Total, "total from the Tasks line");
        Assert.AreSequenceEqual(["apache2", "spamd"], snapshot.Programs!.Select(program => program.Program).ToList(), "programs over the kept rows");
    }

    /// <summary>
    /// A row that does not parse is left out at its line, the other rows, the summary metrics and the sections are kept.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseDropsUnparsableRow()
    {
        // Arrange
        const string bad = "abc root 20 0 1 1 1 S 1.0 0.1 0:00.01 x";
        var lines = Samples.New(Samples.Oct2, [VmstatMarker, "pswpin 5"], Samples.Row(100, "root", "1000", "apache2"), bad, Samples.Row(102, "root", "1000", "spamd"));
        var badLine = lines.IndexOf(bad) + 1;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var records = emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).ToList();
        var snapshot = Payloads<ProcessSnapshot>(records).Single();

        // Assert
        Assert.AreEqual(((long)badLine, RowNotRead), emitter.Skips.Single(), "one skip at the row");
        Assert.AreSequenceEqual([100, 102], snapshot.Processes!.Select(process => process.Pid).ToList(), "the other rows are kept");
        Assert.IsFalse(snapshot.Complete, "a row was left out");
        Assert.HasCount(19, Metrics(records).Where(metric => metric.Point.Name != "vmstat_total").ToList(), "the summary metrics");
        Assert.ContainsSingle(metric => metric.Point.Name == "vmstat_total", Metrics(records), "the section is read");
    }

    /// <summary>
    /// A snapshot closed by the end of input may be cut: its open list is emitted incomplete, and a following header line makes it complete.
    /// </summary>
    /// <param name="kind">Where the file ends</param>
    /// <param name="appendHeader">Whether a header line follows the cut snapshot</param>
    /// <param name="expected">The expected value of complete</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("lsof", false, false)]
    [DataRow("lsof", true, true)]
    [DataRow("vmstat", false, true)]
    [DataRow("vmstat", true, true)]
    [DataRow("old", false, false)]
    [DataRow("old", true, true)]
    public async Task LegacyMonitorParserParseMarksSnapshotCutAtEndOfInputIncomplete(string kind, bool appendHeader, bool expected)
    {
        // Arrange
        var rows = new[] { Samples.Row(100, "root", "1000", "apache2"), Samples.Row(101, "root", "1000", "spamd") };
        var socket = Samples.Socket("sshd", 100, "IPv4", 1001, "TCP", ListenSocket);
        var lines = kind switch
                    {
                        "lsof" => Samples.New(Samples.Oct2, [MeminfoMarker, "MemTotal: 1000 kB", LsofMarker, Samples.LsofHeader, socket, Samples.Socket("sshd", 100, "IPv6", 1002, "TCP", ListenSocket)], rows),
                        "vmstat" => Samples.New(Samples.Oct2, [LsofMarker, Samples.LsofHeader, socket, VmstatMarker, "pswpin 5"], rows),
                        _ => Samples.Old(Samples.Oct2, rows)
                    };

        if (appendHeader)
        {
            lines.Add(Samples.Header(Samples.Oct2Later));
        }

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var actual = kind == "old" ? Payloads<ProcessSnapshot>(emitter.Records).Single().Complete : Payloads<ConnectionSnapshot>(emitter.Records).Single().Complete;

        // Assert
        Assert.AreEqual(expected, actual, "whether the open list is complete");

        if (kind == "vmstat")
        {
            Assert.ContainsSingle(metric => metric.Point.Name == "vmstat_total", Metrics(emitter.Records), "the metric of the cut section is emitted as read");
        }
    }

    /// <summary>
    /// The rest of a snapshot cut by a copy boundary is skipped line by line when the import continues, and the next snapshot is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsRestOfCutSnapshotAtContinuation()
    {
        // Arrange
        var rest = new List<string>
                   {
                       Samples.Socket("sshd", 100, "IPv6", 1002, "TCP", ListenSocket),
                       Samples.Socket("sshd", 100, "IPv4", 1003, "TCP", ListenSocket),
                       VmstatMarker,
                       "pswpin 5"
                   };
        var lines = new List<string>(rest);

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.AreSequenceEqual(Enumerable.Range(1, rest.Count).Select(line => ((long)line, BeforeFirst)).ToList(), emitter.Skips.ToList(), "one skip per line of the rest");
        Assert.HasCount(20, emitter.Records, "the records of the next snapshot");
        Assert.IsTrue(emitter.Records.All(record => record.CapturedAt == Utc(SecondUtc)), "the next snapshot");
    }

    /// <summary>
    /// Lines before the first header line are skipped one by one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsLinesBeforeFirstSnapshot()
    {
        // Arrange
        var lines = new List<string>
                    {
                        "garbage",
                        "more garbage"
                    };

        lines.AddRange(Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));

        // Assert
        Assert.AreSequenceEqual([(1L, BeforeFirst), (2L, BeforeFirst)], emitter.Skips.ToList(), "one skip per line");
        Assert.HasCount(20, emitter.Records, "the snapshot is read");
    }

    /// <summary>
    /// A byte order mark on the first line and carriage returns do not change the records.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsByteOrderMarkAndCarriageReturnsLikePlainText()
    {
        // Arrange
        var lines = NewSnapshotLines(Samples.Oct2);

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        var plain = Samples.Text(lines);
        var windows = "﻿" + plain.Replace("\n", "\r\n", StringComparison.Ordinal);

        // Act
        var expected = await ParseAsync(plain);
        var actual = await ParseAsync(windows);

        // Assert
        Assert.IsGreaterThan(30, expected.Records.Count, "the plain text gives records");
        Assert.AreSequenceEqual(expected.Records.Select(Describe).ToList(), actual.Records.Select(Describe).ToList(), "the same records");
        Assert.IsEmpty(actual.Skips, "no skip");
    }

    /// <summary>
    /// Hostile input never throws, and the skips never exceed the number of lines.
    /// </summary>
    /// <param name="kind">The kind of input</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no-line-feed")]
    [DataRow("top-lines")]
    [DataRow("invalid-utf8")]
    [DataRow("big-row")]
    [DataRow("lsof-without-header")]
    public async Task LegacyMonitorParserParseSurvivesHostileInput(string kind)
    {
        // Arrange
        var bytes = Hostile(kind);
        var lineCount = bytes.Count(value => value == (byte)'\n') + 1 + (bytes.Length / 16384);
        var emitter = new CountingEmitter();
        using var input = new MemoryStream(bytes);

        // Act
        await new LegacyMonitorParser(_berlin).ParseAsync(_file, input, emitter, TestContext.CancellationToken);

        // Assert
        Assert.IsLessThanOrEqualTo(lineCount, emitter.Skips, "no more skips than lines");

        if (kind == "lsof-without-header")
        {
            Assert.AreEqual(100000L, emitter.Skips, "every line before the first snapshot is skipped");
            Assert.AreEqual(0L, emitter.Records, "no record");
        }
    }

    /// <summary>
    /// The lines of the meminfo, pressure and vmstat sections become metrics in line order, after the process snapshot and before the connection snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseEmitsSectionMetrics()
    {
        // Arrange
        var lines = NewSnapshotLines(Samples.Oct2);

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        string[] expected = [
                                "meminfo_bytes|135092764672|bytes|field=MemTotal",
                                "meminfo|0||field=HugePages_Total",
                                "pressure_avg10|0.5|percent|kind=some;resource=memory",
                                "pressure_avg60|0.25|percent|kind=some;resource=memory",
                                "pressure_avg300|0.1|percent|kind=some;resource=memory",
                                "pressure_stall_microseconds_total|12345|microseconds|kind=some;resource=memory",
                                "pressure_avg10|0.1|percent|kind=full;resource=memory",
                                "pressure_avg60|0.05|percent|kind=full;resource=memory",
                                "pressure_avg300|0.01|percent|kind=full;resource=memory",
                                "pressure_stall_microseconds_total|999|microseconds|kind=full;resource=memory",
                                "pressure_avg10|1|percent|kind=some;resource=io",
                                "pressure_avg60|2|percent|kind=some;resource=io",
                                "pressure_avg300|3|percent|kind=some;resource=io",
                                "pressure_stall_microseconds_total|7|microseconds|kind=some;resource=io",
                                "pressure_avg10|4|percent|kind=full;resource=io",
                                "pressure_avg60|5|percent|kind=full;resource=io",
                                "pressure_avg300|6|percent|kind=full;resource=io",
                                "pressure_stall_microseconds_total|8|microseconds|kind=full;resource=io",
                                "vmstat_total|0||counter=pswpin",
                                "vmstat_total|5||counter=pswpout",
                                "vmstat_total|1||counter=oom_kill"
                            ];

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var records = emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).ToList();

        // Assert
        Assert.HasCount(42, records, "nineteen summary metrics, the process snapshot, 21 section metrics and the connection snapshot");
        Assert.IsInstanceOfType<ProcessSnapshot>(records[19].Data, "the process snapshot comes first");
        Assert.IsInstanceOfType<ConnectionSnapshot>(records[41].Data, "the connection snapshot comes last");
        Assert.AreSequenceEqual(expected, Metrics(records.Skip(20).Take(21)).Select(metric => Brief(metric.Point)).ToList(), "the section metrics in line order");
        Assert.IsTrue(records.All(record => record.Origin == RecordOrigin.Import && record.Source == Samples.Source && record.Seq == 0UL), "origin, source and sequence");
        Assert.IsEmpty(emitter.Skips, "no skip");
    }

    /// <summary>
    /// An unreadable or repeated line of a section is skipped at its line and the rest of the section is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsUnreadableSectionLines()
    {
        // Arrange
        var sections = new List<string>
                       {
                           MeminfoMarker,
                           "MemTotal: 1 kB",
                           "MemTotal: 2 kB",
                           "garbage",
                           "Dirty: 3 kB",
                           PressureMarker,
                           "memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=1",
                           "memory: some avg10=0.60 avg60=0.25 avg300=0.10 total=2",
                           "garbage",
                           "cpu: some avg10=1.00 avg60=1.00 avg300=1.00 total=3",
                           VmstatMarker,
                           "pswpin 1",
                           "bad line here",
                           "pswpout 2"
                       };
        var lines = Samples.New(Samples.Oct2, [.. sections], Samples.Row(100, "root", "1000", "apache2"));
        var first = lines.Count - sections.Count;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        (long, string)[] expected = [
                                        (first + 3, "meminfo line not read"),
                                        (first + 4, "meminfo line not read"),
                                        (first + 8, "pressure line not read"),
                                        (first + 9, "pressure line not read"),
                                        (first + 13, "vmstat line not read")
                                    ];

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var section = Metrics(emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).Skip(20)).Select(metric => Brief(metric.Point)).ToList();

        // Assert
        Assert.AreSequenceEqual(expected, emitter.Skips.ToList(), "the unreadable lines in file order");
        Assert.AreEqual(2, section.Count(name => name.StartsWith("meminfo_bytes|", StringComparison.Ordinal)), "the readable meminfo lines");
        Assert.AreEqual(8, section.Count(name => name.StartsWith("pressure_", StringComparison.Ordinal)), "the readable pressure lines");
        Assert.AreEqual(2, section.Count(name => name.StartsWith("vmstat_total|", StringComparison.Ordinal)), "the readable vmstat lines");
    }

    /// <summary>
    /// A line shaped like a section marker whose name is not 1 to 32 lower-case letters is no marker: it is a line of the open section.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsMarkerShapedLineWithOddNameAsSectionLine()
    {
        // Arrange
        string[] sections = [MeminfoMarker, "MemTotal: 1 kB", "----- Meminfo -----", $"----- {new string('a', 33)} -----", "Dirty: 3 kB"];
        var lines = Samples.New(Samples.Oct2, sections, Samples.Row(100, "root", "1000", "apache2"));
        var first = lines.Count - sections.Length;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        (long, string)[] expected = [(first + 3, "meminfo line not read"), (first + 4, "meminfo line not read")];

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var section = Metrics(emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).Skip(20)).Select(metric => Brief(metric.Point)).ToList();

        // Assert
        Assert.AreSequenceEqual(expected, emitter.Skips.ToList(), "the odd marker lines are skipped as meminfo lines");
        Assert.AreSequenceEqual(["meminfo_bytes|1024|bytes|field=MemTotal", "meminfo_bytes|3072|bytes|field=Dirty"], section, "the section continues after them");
    }

    /// <summary>
    /// A vmstat counter repeated in one snapshot is skipped at its line: the first value stays, no second point is emitted and the counter history of the next snapshot uses the first value.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsRepeatedVmstatCounter()
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = VmstatSnapshot(Samples.Oct2, 100, 10, 1000, 0, row);

        lines.AddRange(Samples.New("Fri Oct  2 09:16:01 CEST 2026", [VmstatMarker, "pswpin 220", "pswpin 7", "pswpout 10"], row));

        var repeated = lines.LastIndexOf("pswpin 7") + 1;

        lines.AddRange(VmstatSnapshot("Fri Oct  2 09:17:01 CEST 2026", 340, 10, 1000, 0, row));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var totals = Metrics(emitter.Records.Where(record => record.CapturedAt == Utc("2026-10-02T07:16:01Z"))).Where(metric => metric.Point.Name == "vmstat_total").ToList();

        // Assert
        Assert.AreEqual(((long)repeated, "vmstat line not read"), emitter.Skips.Single(), "one skip at the repeated line");
        Assert.ContainsSingle(metric => metric.Point.Labels!["counter"] == "pswpin", totals, "one point for the counter");
        Assert.AreEqual(220.0, totals.Single(metric => metric.Point.Labels!["counter"] == "pswpin").Point.Value, Delta, "the first value is kept");
        AssertRates(Rates(emitter, "2026-10-02T07:16:01Z"), [("pswpin", 2.0), ("pswpout", 0.0)]);
        AssertRates(Rates(emitter, "2026-10-02T07:17:01Z"), [("pswpin", 2.0), ("pswpout", 0.0), ("pgmajfault", 0.0), ("oom_kill", 0.0)]);
    }

    /// <summary>
    /// A vmstat counter becomes a rate against the previous accepted snapshot of the parse: a falling counter gives none, a skipped snapshot is no predecessor and the first snapshot of a parse has none.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseConvertsVmstatCountersToRates()
    {
        // Arrange
        var row = Samples.Row(100, "root", "1000", "apache2");
        var lines = VmstatSnapshot(Samples.Oct2, 100, 10, 1000, 0, row);
        var secondHeader = lines.Count + 1;

        lines.AddRange(VmstatSnapshot("Fri Oct  2 09:16:01 CEST 2026", 220, 10, 1600, 1, row));
        lines.Add(Samples.Header("Fri Oct  2 09:16:31 CEST 2026"));
        lines.AddRange(Samples.Top(1, row));
        lines.AddRange([Samples.Load, VmstatMarker, "pswpout 9999"]);
        lines.AddRange(VmstatSnapshot("Fri Oct  2 09:17:01 CEST 2026", 50, 70, 1600, 1, row));

        var all = Samples.Text(lines);
        var fromSecond = Samples.Text(lines.Skip(secondHeader - 1));

        // Act
        var emitter = await ParseAsync(all);
        var continued = await ParseAsync(fromSecond);

        // Assert
        Assert.AreEqual(((long)lines.IndexOf(Samples.Header("Fri Oct  2 09:16:31 CEST 2026")) + 1, MalformedSnapshot), emitter.Skips.Single(), "one skipped snapshot");
        Assert.IsEmpty(Rates(emitter, "2026-10-02T07:15:01Z"), "the first snapshot of a parse has no rate");
        AssertRates(Rates(emitter, "2026-10-02T07:16:01Z"), [("pswpin", 2.0), ("pswpout", 0.0), ("pgmajfault", 10.0), ("oom_kill", 1.0 / 60)]);
        AssertRates(Rates(emitter, "2026-10-02T07:17:01Z"), [("pswpout", 1.0), ("pgmajfault", 0.0), ("oom_kill", 0.0)]);
        Assert.IsEmpty(Rates(continued, "2026-10-02T07:16:01Z"), "a parse that starts at the second header has no rate for it");
        AssertRates(Rates(continued, "2026-10-02T07:17:01Z"), [("pswpout", 1.0), ("pgmajfault", 0.0), ("oom_kill", 0.0)]);
        Assert.IsTrue(Metrics(emitter.Records).Where(metric => metric.Point.Name == "vmstat_per_second").All(metric => metric.Point.Unit == "per_second"), "the unit of a rate");
    }

    /// <summary>
    /// A section that appears twice in a snapshot, or has an unknown name, is skipped at its marker and its lines are not read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseSkipsRepeatedSection()
    {
        // Arrange
        var sections = new List<string>
                       {
                           MeminfoMarker,
                           "MemTotal: 1 kB",
                           MeminfoMarker,
                           "Dirty: 2 kB",
                           "----- zzz -----",
                           "MemTotal: 5 kB",
                           "garbage line",
                           VmstatMarker,
                           "pswpin 3"
                       };
        var lines = Samples.New(Samples.Oct2, [.. sections], Samples.Row(100, "root", "1000", "apache2"));
        var first = lines.Count - sections.Count;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var section = Metrics(emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).Skip(20)).Select(metric => Brief(metric.Point)).ToList();

        // Assert
        Assert.AreSequenceEqual([((long)first + 3, "repeated section"), ((long)first + 5, "unknown section")], emitter.Skips.ToList(), "one skip per marker");
        Assert.AreSequenceEqual(["meminfo_bytes|1024|bytes|field=MemTotal", "vmstat_total|3||counter=pswpin"], section, "only the lines of known sections are read");
    }

    /// <summary>
    /// The lsof lines become one connection snapshot with states, processes, remotes, listeners and connections, each in its fixed order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseBuildsConnectionSnapshot()
    {
        // Arrange
        var sockets = new List<string>
                      {
                          Samples.Socket("sshd", 100, "IPv4", 1001, "TCP", ListenSocket),
                          Samples.Socket("sshd", 100, "IPv6", 1002, "TCP", ListenSocket),
                          Samples.Socket("nginx", 200, "IPv4", 1003, "TCP", "192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)"),
                          Samples.Socket("nginx", 200, "IPv4", 1004, "TCP", "192.0.2.10:https->198.51.100.7:50002 (ESTABLISHED)"),
                          Samples.Socket("nginx", 200, "IPv4", 1005, "TCP", "192.0.2.10:https->203.0.113.9:50003 (TIME_WAIT)"),
                          Samples.Socket("nginx", 200, "IPv4", 1006, "TCP", "192.0.2.10:https->192.0.2.77:50004 (ESTABLISHED)"),
                          Samples.Socket("named", 300, "IPv4", 1007, "UDP", "127.0.0.53:domain")
                      };

        // Act
        var snapshot = await ConnectionSnapshotOfAsync(sockets);

        // Assert
        Assert.IsTrue(snapshot.Complete, "the section was closed by the next header line");
        Assert.AreSequenceEqual(["tcp|ESTABLISHED|3", "tcp|LISTEN|1", "tcp|TIME_WAIT|1", "tcp6|LISTEN|1", "udp||1"], snapshot.States!.Select(state => $"{state.Proto}|{state.State}|{state.Count}").ToList(), "states by protocol and state");
        Assert.AreSequenceEqual(["100|sshd|2", "200|nginx|4", "300|named|1"], snapshot.Processes!.Select(process => $"{process.Pid}|{process.Command}|{process.Count}").ToList(), "processes in order of first appearance");
        Assert.AreSequenceEqual(["198.51.100.7|2", "192.0.2.77|1", "203.0.113.9|1"], snapshot.Remotes!.Select(remote => $"{remote.Addr}|{remote.Count}").ToList(), "remotes by count, then address text");

        string[] expectedListeners = ["tcp|0.0.0.0:22|100|sshd", "tcp6|[::]:22|100|sshd", "udp|127.0.0.53:53|300|named"];
        string[] expectedConnections = [
                                           "tcp|192.0.2.10:443|198.51.100.7:50001|ESTABLISHED|200|nginx",
                                           "tcp|192.0.2.10:443|198.51.100.7:50002|ESTABLISHED|200|nginx",
                                           "tcp|192.0.2.10:443|203.0.113.9:50003|TIME_WAIT|200|nginx",
                                           "tcp|192.0.2.10:443|192.0.2.77:50004|ESTABLISHED|200|nginx"
                                       ];
        var listeners = snapshot.Listeners!.Select(listener => $"{listener.Proto}|{listener.Local}|{listener.Pid}|{listener.Command}");
        var connections = snapshot.Connections!.Select(connection => $"{connection.Proto}|{connection.Local}|{connection.Remote}|{connection.State}|{connection.Pid}|{connection.Command}");

        Assert.AreSequenceEqual(expectedListeners.Order(StringComparer.Ordinal).ToList(), listeners.Order(StringComparer.Ordinal).ToList(), "listeners");
        Assert.AreSequenceEqual(expectedConnections.Order(StringComparer.Ordinal).ToList(), connections.Order(StringComparer.Ordinal).ToList(), "connections");
    }

    /// <summary>
    /// A socket seen by two processes is counted once in the states, remotes and connections and once per line in the processes; its owner is the first line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseCountsSharedSocketOnce()
    {
        // Arrange
        List<string> sockets = [
                                   "nginx 200 root 6u IPv4 1003 0t0 TCP 192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)",
                                   "worker 201 www-data 11u IPv4 1003 0t0 TCP 192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)"
                               ];

        // Act
        var snapshot = await ConnectionSnapshotOfAsync(sockets);

        // Assert
        Assert.IsTrue(snapshot.Complete, "complete");
        Assert.AreSequenceEqual(["tcp|ESTABLISHED|1"], snapshot.States!.Select(state => $"{state.Proto}|{state.State}|{state.Count}").ToList(), "one socket");
        Assert.AreSequenceEqual(["200|nginx|1", "201|worker|1"], snapshot.Processes!.Select(process => $"{process.Pid}|{process.Command}|{process.Count}").ToList(), "one count per process line");
        Assert.AreSequenceEqual(["198.51.100.7|1"], snapshot.Remotes!.Select(remote => $"{remote.Addr}|{remote.Count}").ToList(), "one remote");
        Assert.HasCount(1, snapshot.Connections!, "one connection");
        Assert.AreEqual(200, snapshot.Connections![0].Pid, "the owner is the first line");
        Assert.AreEqual("nginx", snapshot.Connections[0].Command, "the command of the first line");
        Assert.AreEqual(0, snapshot.Listeners?.Count ?? 0, "no listener");
    }

    /// <summary>
    /// A line that is not read or whose address is not mapped is skipped at its line and marks the connection snapshot incomplete; the other sockets are kept.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseLeavesOutUnmappedSocketsAndMarksIncomplete()
    {
        // Arrange
        var lines = Samples.New(Samples.Oct2,
                                [LsofMarker, Samples.LsofHeader, Samples.Socket("sshd", 100, "IPv4", 1001, "TCP", ListenSocket), UnmappedSocket, "garbage line", Samples.Socket("sshd", 100, "IPv6", 1002, "TCP", ListenSocket)],
                                Samples.Row(100, "root", "1000", "apache2"));
        var unmappedLine = lines.IndexOf(UnmappedSocket) + 1;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ConnectionSnapshot>(emitter.Records).Single();

        // Assert
        Assert.AreSequenceEqual([((long)unmappedLine, "socket address not mapped"), ((long)unmappedLine + 1, "lsof line not read")], emitter.Skips.ToList(), "the two lines left out");
        Assert.IsFalse(snapshot.Complete, "a line was left out");
        Assert.HasCount(2, snapshot.Listeners!, "the other sockets are kept");
    }

    /// <summary>
    /// An lsof section without lines emits no record; one with only the header emits an empty, complete snapshot.
    /// </summary>
    /// <param name="withHeader">Whether the header line of lsof is present</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyMonitorParserParseHandlesEmptyLsofSection(bool withHeader)
    {
        // Arrange
        var sections = withHeader ? new[] { LsofMarker, Samples.LsofHeader } : [LsofMarker];
        var lines = Samples.New(Samples.Oct2, sections, Samples.Row(100, "root", "1000", "apache2"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshots = Payloads<ConnectionSnapshot>(emitter.Records);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip");

        if (withHeader)
        {
            Assert.HasCount(1, snapshots, "an empty snapshot");
            Assert.IsTrue(snapshots[0].Complete, "complete");
            Assert.AreEqual(0, (snapshots[0].States?.Count ?? 0) + (snapshots[0].Processes?.Count ?? 0) + (snapshots[0].Remotes?.Count ?? 0) + (snapshots[0].Listeners?.Count ?? 0) + (snapshots[0].Connections?.Count ?? 0), "no entries");
        }
        else
        {
            Assert.IsEmpty(snapshots, "no snapshot");
        }
    }

    /// <summary>
    /// Both fixtures give records that pass the validation of the importer, in the order summary metrics, process snapshot and then the sections in file order.
    /// </summary>
    /// <param name="path">The fixture</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(OldFixture)]
    [DataRow(NewFixture)]
    public async Task LegacyMonitorParserParseFixturesEmitValidRecords(string path)
    {
        // Arrange
        var text = await File.ReadAllTextAsync(RepositoryFiles.Path(path), TestContext.CancellationToken);

        // Act
        var emitter = await ParseAsync(text);
        var groups = emitter.Records.GroupBy(record => record.CapturedAt).ToList();

        // Assert
        Assert.IsEmpty(emitter.Skips, "no skip and no refused record");
        Assert.IsNotEmpty(groups, "records");

        foreach (var group in groups)
        {
            var records = group.ToList();

            Assert.AreSequenceEqual(Samples.SummaryNames, Metrics(records.Take(19)).Select(metric => metric.Point.Name).ToList(), $"summary metrics of {group.Key}");
            Assert.IsInstanceOfType<ProcessSnapshot>(records[19].Data, $"process snapshot of {group.Key}");

            var ranks = records.Skip(20).Select(Rank).ToList();

            Assert.AreSequenceEqual(ranks.Order().ToList(), ranks, $"sections in file order at {group.Key}");
        }
    }

    /// <summary>
    /// The old fixture gives three snapshots five minutes apart, with cut names, and only the last one is marked incomplete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsOldFixture()
    {
        // Arrange
        var text = await File.ReadAllTextAsync(RepositoryFiles.Path(OldFixture), TestContext.CancellationToken);

        // Act
        var emitter = await ParseAsync(text);
        var snapshots = Payloads<ProcessSnapshot>(emitter.Records);

        // Assert
        Assert.AreSequenceEqual([Utc(FirstUtc), Utc(SecondUtc), Utc(ThirdUtc)], emitter.Records.Select(record => record.CapturedAt).Distinct().ToList(), "five minutes apart");
        Assert.HasCount(60, emitter.Records, "twenty records per snapshot");
        Assert.AreSequenceEqual([true, true, false], snapshots.Select(snapshot => snapshot.Complete).ToList(), "only the snapshot closed by the end of input is incomplete");
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Processes!.Count == 20 && snapshot.Total == 20u && snapshot.Programs!.Count == 15), "twenty processes of fifteen programs");
        Assert.AreEqual(4, snapshots[0].Processes!.Count(process => process.Truncated), "four cut names");
        Assert.IsTrue(snapshots[0].Processes!.All(process => process.Cmdline.Length == 0), "no command lines in the old format");
    }

    /// <summary>
    /// The new fixture runs across the repeated hour of the end of daylight saving time and ends in a cut snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseReadsNewFixtureAcrossDaylightSavingEnd()
    {
        // Arrange
        var text = await File.ReadAllTextAsync(RepositoryFiles.Path(NewFixture), TestContext.CancellationToken);
        string[] instants = ["2026-10-25T00:58:01Z", "2026-10-25T00:59:01Z", "2026-10-25T01:00:01Z", "2026-10-25T01:01:01Z", "2026-10-25T01:02:01Z"];

        // Act
        var emitter = await ParseAsync(text);
        var groups = emitter.Records.GroupBy(record => record.CapturedAt).ToList();
        var connections = Payloads<ConnectionSnapshot>(emitter.Records);
        var first = connections[0];

        // Assert
        Assert.AreSequenceEqual(instants.Select(Utc).ToList(), groups.Select(group => group.Key).ToList(), "one minute apart and increasing across the repeated hour");
        Assert.AreSequenceEqual([48, 52, 52, 52, 52], groups.Select(group => group.Count()).ToList(), "records per snapshot, rates from the second one on");
        Assert.IsTrue(Payloads<ProcessSnapshot>(emitter.Records).All(snapshot => snapshot.Complete && snapshot.Total == 17u && snapshot.Processes!.Count == 17), "seventeen processes of seventeen tasks");
        Assert.AreSequenceEqual([true, true, true, true, false], connections.Select(connection => connection.Complete).ToList(), "the cut snapshot is incomplete");
        AssertRates(Rates(emitter, instants[2]), [("pswpin", 0.05), ("pswpout", 7.0 / 60), ("pgmajfault", 1.0), ("oom_kill", 0.0)]);
        Assert.AreSequenceEqual(["tcp|ESTABLISHED|4", "tcp|LISTEN|6", "tcp|TIME_WAIT|1", "tcp6|ESTABLISHED|1", "tcp6|LISTEN|3", "udp||2", "udp6||1"], first.States!.Select(state => $"{state.Proto}|{state.State}|{state.Count}").ToList(), "states");
        Assert.AreSequenceEqual(["198.51.100.7|2", "127.0.0.1|1", "198.51.100.99|1", "2001:db8:1::77|1", "203.0.113.25|1"], first.Remotes!.Select(remote => $"{remote.Addr}|{remote.Count}").ToList(), "remotes");
        Assert.HasCount(12, first.Listeners!, "listeners");
        Assert.HasCount(6, first.Connections!, "connections");
        Assert.AreSequenceEqual([812, 933, 1001, 1203, 1310, 1402, 1501, 1601, 1701], first.Processes!.Select(process => process.Pid).ToList(), "processes in order of first appearance");
        Assert.AreEqual("php-fpm ", first.Processes![7].Command, "the escape is decoded");
    }

    /// <summary>
    /// Parsing the same content twice gives the same records and skips.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseIsDeterministic()
    {
        // Arrange
        var text = await File.ReadAllTextAsync(RepositoryFiles.Path(NewFixture), TestContext.CancellationToken);

        // Act
        var first = await ParseAsync(text);
        var second = await ParseAsync(text);

        // Assert
        Assert.IsNotEmpty(first.Records, "records");
        Assert.AreSequenceEqual(first.Records.Select(Describe).ToList(), second.Records.Select(Describe).ToList(), "the same records");
        Assert.AreSequenceEqual(first.Skips.ToList(), second.Skips.ToList(), "the same skips");
    }

    /// <summary>
    /// A parse stopped by an emitter error has emitted a prefix of the complete parse and passes the error on.
    /// </summary>
    /// <param name="failAt">The number of the call of the emitter that fails</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(21)]
    [DataRow(25)]
    [DataRow(41)]
    [DataRow(42)]
    [DataRow(43)]
    [DataRow(60)]
    public async Task LegacyMonitorParserParseEmitsPrefixWhenEmitterFails(int failAt)
    {
        // Arrange
        var lines = NewSnapshotLines(Samples.Oct2);

        lines.AddRange(NewSnapshotLines(Samples.Oct2Later));

        var content = Encoding.UTF8.GetBytes(Samples.Text(lines));
        var complete = await RecordingEmitter.ParseAsync(new LegacyMonitorParser(_berlin), _file, content, TestContext.CancellationToken);
        var failing = new RecordingEmitter
                      {
                          Failure = (call, _) => call == failAt ? new InvalidOperationException("emitter failed") : null
                      };
        using var input = new MemoryStream(content);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await new LegacyMonitorParser(_berlin).ParseAsync(_file, input, failing, TestContext.CancellationToken));

        // Assert
        Assert.AreEqual("emitter failed", exception.Message, "the error is passed on");
        Assert.IsGreaterThan(failAt, complete.Records.Count, "the complete parse has more records than the failing call");
        Assert.AreSequenceEqual(complete.Records.Take(failAt - 1).Select(Describe).ToList(), failing.Records.Select(Describe).ToList(), "a prefix of the complete parse");
    }

    /// <summary>
    /// A cancelled parse throws, and what it emitted before is a prefix of the complete parse.
    /// </summary>
    /// <param name="after">The number of records after which the token is cancelled; zero cancels before the parse</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(25)]
    public async Task LegacyMonitorParserParseHonorsCancellation(int after)
    {
        // Arrange
        var lines = NewSnapshotLines(Samples.Oct2);

        lines.AddRange(NewSnapshotLines(Samples.Oct2Later));

        var content = Encoding.UTF8.GetBytes(Samples.Text(lines));
        var complete = await RecordingEmitter.ParseAsync(new LegacyMonitorParser(_berlin), _file, content, TestContext.CancellationToken);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var emitter = new RecordingEmitter();

        emitter.Observed = _ =>
                           {
                               if (emitter.Records.Count == after)
                               {
                                   source.Cancel();
                               }
                           };

        if (after == 0)
        {
            await source.CancelAsync();
        }

        using var input = new MemoryStream(content);

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new LegacyMonitorParser(_berlin).ParseAsync(_file, input, emitter, source.Token));

        // Assert
        Assert.IsLessThan(complete.Records.Count, emitter.Records.Count, "fewer records than the complete parse");
        Assert.AreSequenceEqual(complete.Records.Take(emitter.Records.Count).Select(Describe).ToList(), emitter.Records.Select(Describe).ToList(), "a prefix of the complete parse");
    }

    /// <summary>
    /// A snapshot reads at most 4,096 row lines, whether they parse or not; the first line beyond is reported once, the later ones are dropped unseen.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseCapsProcessRows()
    {
        // Arrange
        var rows = new List<string>();

        for (var pid = 1; pid <= 5; pid++)
        {
            rows.Add(Samples.Row(pid, "root", "1000", "apache2"));
        }

        rows.AddRange(Enumerable.Repeat("x", 4091));
        rows.Add("y");
        rows.Add(Samples.Row(999, "root", "1000", "late"));
        rows.AddRange(Enumerable.Repeat("z", 2));

        var lines = Samples.Old(Samples.Oct2, [.. rows]);
        var capLine = 1 + 7 + 4097;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ProcessSnapshot>(emitter.Records)[0];

        // Assert
        Assert.HasCount(4092, emitter.Skips, "4,091 rows not read and the cap");
        Assert.AreEqual(4091, emitter.Skips.Count(skip => skip.Reason == RowNotRead), "every unparsable row counts toward the cap and is reported");
        Assert.AreEqual(((long)capLine, "too many processes in a snapshot"), emitter.Skips.Single(skip => skip.Reason != RowNotRead), "one skip at the first line beyond the cap");
        Assert.HasCount(5, snapshot.Processes!, "the rows beyond the cap are not read");
        Assert.IsFalse(snapshot.Complete, "rows were left out");
        Assert.AreEqual(4100u, snapshot.Total, "total from the Tasks line");
        Assert.HasCount(1, snapshot.Programs!, "aggregates over the kept rows");
    }

    /// <summary>
    /// A snapshot reads at most 4,096 lsof lines, unmapped, too long and header lines included; the first line beyond is reported once, the later ones are dropped unseen.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseCapsSocketLines()
    {
        // Arrange
        var sockets = new List<string>
                      {
                          Samples.LsofHeader,
                          UnmappedSocket,
                          UnmappedSocket,
                          new string('u', 1100) + " 100 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)"
                      };

        for (var index = 0; index < 4092; index++)
        {
            sockets.Add(Samples.Socket("sshd", 100, "IPv4", 20000 + index, "TCP", ListenSocket));
        }

        sockets.Add(Samples.Socket("sshd", 100, "IPv4", 99991, "TCP", ListenSocket));
        sockets.Add(Samples.Socket("sshd", 100, "IPv4", 99992, "TCP", ListenSocket));
        sockets.Add("garbage");
        sockets.Add(UnmappedSocket);

        var lines = Samples.New(Samples.Oct2, [LsofMarker, .. sockets], Samples.Row(100, "root", "1000", "apache2"));
        var markerLine = lines.IndexOf(LsofMarker) + 1;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var snapshot = Payloads<ConnectionSnapshot>(emitter.Records).Single();

        // Assert
        Assert.AreSequenceEqual([((long)markerLine + 2, "socket address not mapped"), ((long)markerLine + 3, "socket address not mapped"), ((long)markerLine + 4, "lsof line not read"), ((long)markerLine + 4097, "too many sockets in a snapshot")],
                                emitter.Skips.ToList(),
                                "the lines left out and the cap, the later lines give no skip");
        Assert.IsFalse(snapshot.Complete, "lines were left out");
        Assert.HasCount(4092, snapshot.Listeners!, "the sockets within the cap");
        Assert.AreEqual(4092u, snapshot.States!.Single().Count, "counted once each");
    }

    /// <summary>
    /// A section reads at most 128 meminfo, 16 pressure and 64 vmstat lines; the first line beyond is reported once, the later ones are dropped unseen.
    /// </summary>
    /// <param name="section">The section</param>
    /// <param name="cap">The cap of the section</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("meminfo", 128)]
    [DataRow("pressure", 16)]
    [DataRow("vmstat", 64)]
    public async Task LegacyMonitorParserParseCapsSectionLines(string section, int cap)
    {
        // Arrange
        var sectionLines = SectionLines(section, cap);
        var lines = Samples.New(Samples.Oct2, [.. sectionLines], Samples.Row(100, "root", "1000", "apache2"));
        var firstLine = lines.Count - sectionLines.Count + 1;

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        // Act
        var emitter = await ParseAsync(Samples.Text(lines));
        var metrics = Metrics(emitter.Records.Where(record => record.CapturedAt == Utc(FirstUtc)).Skip(20)).Select(metric => Brief(metric.Point)).ToList();

        // Assert
        Assert.AreSequenceEqual([((long)firstLine + cap + 1, "too many lines in a section")], emitter.Skips.ToList(), "one skip at the first line beyond the cap");
        Assert.HasCount(ExpectedMetrics(section), metrics, "the lines within the cap are read");
    }

    /// <summary>
    /// The memory retained at the last record of 2,000 snapshots is within 1 MiB of that at the last record of 20.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseRetainsBoundedMemory()
    {
        // Arrange
        var rows = Enumerable.Range(0, 20).Select(index => Samples.Row(100 + index, "root", "1000", "/usr/sbin/apache2 -k start")).ToArray();
        var lines = Samples.New(Samples.Oct2, [MeminfoMarker, "MemTotal: 1000 kB", PressureMarker, "memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=1", VmstatMarker, "pswpin 5", LsofMarker, Samples.LsofHeader, Samples.Socket("sshd", 100, "IPv4", 1001, "TCP", ListenSocket)], rows);
        var snapshot = Encoding.UTF8.GetBytes(Samples.Text(lines));

        // Act
        var small = await MeasureAsync([(snapshot, 20)], 20);
        var large = await MeasureAsync([(snapshot, 2000)], 2000);

        // Assert
        Assert.IsGreaterThanOrEqualTo(0L, small.Retained, "the memory was taken at the last record of 20 snapshots");
        Assert.IsGreaterThanOrEqualTo(0L, large.Retained, "the memory was taken at the last record of 2,000 snapshots");
        Assert.AreEqual(0L, large.Emitter.Skips, $"no skip: {large.Emitter.FirstSkip}");
        Assert.IsLessThan(1024L * 1024, large.Retained - small.Retained, "retained bytes grow with the number of snapshots by less than 1 MiB");
    }

    /// <summary>
    /// The memory retained by one snapshot with 200,000 malformed vmstat lines and 200,000 unmapped lsof lines is within 1 MiB of that with 2,000 each, and the skips are bounded by the caps.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LegacyMonitorParserParseRetainsBoundedMemoryForSectionLines()
    {
        // Arrange
        var top = Encoding.UTF8.GetBytes(Samples.Text(Samples.New(Samples.Oct2, [VmstatMarker], Samples.Row(100, "root", "1000", "apache2"))));
        var malformed = Encoding.UTF8.GetBytes("bad line here\n");
        var lsof = Encoding.UTF8.GetBytes(LsofMarker + "\n" + Samples.LsofHeader + "\n");
        var unmapped = Encoding.UTF8.GetBytes(UnmappedSocket + "\n");

        // Act
        var small = await MeasureAsync([(top, 1), (malformed, 2000), (lsof, 1), (unmapped, 2000)], 1);
        var large = await MeasureAsync([(top, 1), (malformed, 200000), (lsof, 1), (unmapped, 200000)], 1);

        // Assert
        Assert.IsGreaterThanOrEqualTo(0L, small.Retained, "the memory was taken at the connection snapshot of the small input");
        Assert.IsGreaterThanOrEqualTo(0L, large.Retained, "the memory was taken at the connection snapshot of the large input");
        Assert.IsLessThan(1024L * 1024, large.Retained - small.Retained, "retained bytes grow with the number of lines by less than 1 MiB");
        Assert.IsGreaterThanOrEqualTo(2L, large.Emitter.Skips, "the two caps are reported");
        Assert.IsLessThanOrEqualTo(2L + 64 + 4096, large.Emitter.Skips, "the cap skips and at most 64 + 4,096 line skips");
    }

    /// <summary>
    /// The fixtures are small, and every address in them is in a documentation range, loopback or unspecified.
    /// </summary>
    /// <param name="path">The fixture</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(OldFixture)]
    [DataRow(NewFixture)]
    public async Task LegacyMonitorParserFixturesAreSmallAndUseDocumentationAddresses(string path)
    {
        // Arrange
        var full = RepositoryFiles.Path(path);
        var text = await File.ReadAllTextAsync(full, TestContext.CancellationToken);

        // Act
        var emitter = await ParseAsync(text);
        var parsed = new List<IPAddress>();

        foreach (var snapshot in Payloads<ConnectionSnapshot>(emitter.Records))
        {
            parsed.AddRange(snapshot.Listeners?.Select(listener => listener.Local!.Address) ?? []);
            parsed.AddRange(snapshot.Connections?.SelectMany(connection => new[] { connection.Local!.Address, connection.Remote!.Address }) ?? []);
            parsed.AddRange(snapshot.Remotes?.Select(remote => remote.Addr!) ?? []);
        }

        var written = Ipv4Pattern().Matches(text)
                                   .Select(match => match.Value)
                                   .Concat(BracketPattern().Matches(text).Select(match => match.Groups[1].Value))
                                   .Select(IPAddress.Parse)
                                   .ToList();

        // Assert
        Assert.IsLessThan(64L * 1024, new FileInfo(full).Length, "smaller than 64 KiB");
        Assert.IsTrue(parsed.All(IsDocumentationAddress), "every address in a connection snapshot is a documentation, loopback or unspecified address");
        Assert.IsTrue(written.All(IsDocumentationAddress), "every address written in the file is a documentation, loopback or unspecified address");
    }

    /// <summary>
    /// Returns the bytes the process retains after a full collection, which is how the heap-bound claims are measured.
    /// </summary>
    /// <returns>The number of bytes</returns>
    private static long RetainedBytes()
    {
#pragma warning disable S1215
        return GC.GetTotalMemory(true);
#pragma warning restore S1215
    }

    /// <summary>
    /// Builds a snapshot in the new format with every section, 21 section metrics and a connection snapshot.
    /// </summary>
    /// <param name="date">The output of date</param>
    /// <returns>The lines</returns>
    private static List<string> NewSnapshotLines(string date)
    {
        string[] sections = [
                                MeminfoMarker,
                                "MemTotal:       131926528 kB",
                                "HugePages_Total:       0",
                                PressureMarker,
                                "memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=12345",
                                "full avg10=0.10 avg60=0.05 avg300=0.01 total=999",
                                "cpu: n/a",
                                "io: some avg10=1.00 avg60=2.00 avg300=3.00 total=7 full avg10=4.00 avg60=5.00 avg300=6.00 total=8",
                                VmstatMarker,
                                "pswpin 0",
                                "pswpout 5",
                                "nr_free_pages 258000",
                                "oom_kill 1",
                                LsofMarker,
                                Samples.LsofHeader,
                                Samples.Socket("sshd", 100, "IPv4", 1001, "TCP", ListenSocket),
                                Samples.Socket("nginx", 200, "IPv4", 1003, "TCP", "192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)"),
                                Samples.Socket("named", 300, "IPv4", 1007, "UDP", "127.0.0.53:domain")
                            ];

        return Samples.New(date, sections, Samples.Row(100, "root", "1000", "/usr/sbin/apache2 -k start"), Samples.Row(101, "root", "1000", "spamd child"));
    }

    /// <summary>
    /// Builds a snapshot with a vmstat section.
    /// </summary>
    /// <param name="date">The output of date</param>
    /// <param name="pswpin">The value of pswpin</param>
    /// <param name="pswpout">The value of pswpout</param>
    /// <param name="pgmajfault">The value of pgmajfault</param>
    /// <param name="oomKill">The value of oom_kill</param>
    /// <param name="row">The one row of the table</param>
    /// <returns>The lines</returns>
    private static List<string> VmstatSnapshot(string date, int pswpin, int pswpout, int pgmajfault, int oomKill, string row)
    {
        return Samples.New(date, [VmstatMarker, $"pswpin {pswpin}", $"pswpout {pswpout}", $"pgmajfault {pgmajfault}", $"oom_kill {oomKill}"], row);
    }

    /// <summary>
    /// Builds the lines of a section that exceed its cap: the lines within the cap, the line beyond it and a line that would be an error if it were read.
    /// </summary>
    /// <param name="section">The section</param>
    /// <param name="cap">The cap of the section</param>
    /// <returns>The marker and the lines</returns>
    private static List<string> SectionLines(string section, int cap)
    {
        var lines = new List<string>();

        switch (section)
        {
            case "meminfo":
                {
                    lines.Add(MeminfoMarker);
                    lines.AddRange(Enumerable.Range(1, cap).Select(index => $"Field{index:D3}: {index} kB"));
                    lines.Add("Overflow: 1 kB");
                }
                break;

            case "pressure":
                {
                    lines.Add(PressureMarker);
                    lines.AddRange(["memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=1", "cpu: some avg10=0.50 avg60=0.25 avg300=0.10 total=2", "io: some avg10=0.50 avg60=0.25 avg300=0.10 total=3"]);
                    lines.AddRange(Enumerable.Repeat("n/a", cap - 3));
                    lines.Add("n/a");
                }
                break;

            default:
                {
                    lines.Add(VmstatMarker);
                    lines.AddRange(["pswpin 1", "pswpout 2", "pgmajfault 3", "oom_kill 4"]);
                    lines.AddRange(Enumerable.Range(1, cap - 4).Select(index => $"other{index} {index}"));
                    lines.Add("overflow 1");
                }
                break;
        }

        lines.Add("garbage that would not be read");

        return lines;
    }

    /// <summary>
    /// Builds hostile input of a kind.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The bytes</returns>
    private static byte[] Hostile(string kind)
    {
        var row = Samples.Row(100, "root", "1000", "apache2");

        switch (kind)
        {
            case "no-line-feed":
                {
                    return Encoding.ASCII.GetBytes(new string('x', 20000));
                }
            case "top-lines":
                {
                    return Encoding.UTF8.GetBytes(Samples.Text([Samples.Header(Samples.Oct2), .. Enumerable.Repeat(Samples.Load, 10000)]));
                }
            case "invalid-utf8":
                {
                    var lines = Samples.New(Samples.Oct2,
                                            [MeminfoMarker, "Memÿ: 1 kB", PressureMarker, "memþ: ÿ", VmstatMarker, "pswpÿin 1", LsofMarker, Samples.LsofHeader, "sshÿd 100 rÿoot 3u IPv4 1 0t0 TCP *:ssh (LISTEN)"],
                                            Samples.Row(100, "uÿser", "1000", "/usr/ÿbin/x þ"));

                    return Encoding.Latin1.GetBytes(Samples.Text(lines));
                }
            case "big-row":
                {
                    return Encoding.UTF8.GetBytes(Samples.Text(Samples.Old(Samples.Oct2, Samples.Row(100, "root", "1000", new string('x', 20000)), row)));
                }
            default:
                {
                    return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Samples.Socket("sshd", 100, "IPv4", 1, "TCP", ListenSocket) + "\n", 100000)));
                }
        }
    }

    /// <summary>
    /// Parses a UTC time.
    /// </summary>
    /// <param name="text">The time</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset Utc(string text)
    {
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    /// <summary>
    /// Returns the metric records of the given records.
    /// </summary>
    /// <param name="records">The records</param>
    /// <returns>The records with their metric</returns>
    private static List<(DataRecord Record, MetricPoint Point)> Metrics(IEnumerable<DataRecord> records)
    {
        return records.Where(record => record.Data is MetricPoint).Select(record => (record, (MetricPoint)record.Data!)).ToList();
    }

    /// <summary>
    /// Returns the payloads of a type of the given records.
    /// </summary>
    /// <typeparam name="TPayload">The type of the payload</typeparam>
    /// <param name="records">The records</param>
    /// <returns>The payloads in order</returns>
    private static List<TPayload> Payloads<TPayload>(IEnumerable<DataRecord> records)
        where TPayload : class, IPayload
    {
        return records.Select(record => record.Data).OfType<TPayload>().ToList();
    }

    /// <summary>
    /// Describes a record by its kind, origin, source, sequence, time and payload.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The description</returns>
    private static string Describe(DataRecord record)
    {
        return $"{record.Kind}|{record.Origin}|{record.Source}|{record.Seq}|{record.CapturedAt.UtcTicks}|{JsonSerializer.Serialize(record.Data, record.Data!.GetType())}";
    }

    /// <summary>
    /// Describes a metric by its name, value, unit and sorted labels.
    /// </summary>
    /// <param name="point">The metric</param>
    /// <returns>The description</returns>
    private static string Brief(MetricPoint point)
    {
        var labels = string.Join(';', (point.Labels ?? []).OrderBy(label => label.Key, StringComparer.Ordinal).Select(label => $"{label.Key}={label.Value}"));

        return $"{point.Name}|{point.Value.ToString("R", CultureInfo.InvariantCulture)}|{point.Unit}|{labels}";
    }

    /// <summary>
    /// Returns the position of a record in the order of the sections.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The rank</returns>
    private static int Rank(DataRecord record)
    {
        return record.Data switch
               {
                   ConnectionSnapshot => 3,
                   MetricPoint point when point.Name.StartsWith("meminfo", StringComparison.Ordinal) => 0,
                   MetricPoint point when point.Name.StartsWith("pressure_", StringComparison.Ordinal) => 1,
                   MetricPoint point when point.Name.StartsWith("vmstat_", StringComparison.Ordinal) => 2,
                   _ => -1
               };
    }

    /// <summary>
    /// Returns the vmstat rates of the snapshot at an instant.
    /// </summary>
    /// <param name="emitter">The emitter with the result</param>
    /// <param name="instant">The time of the snapshot in UTC</param>
    /// <returns>The counters and their rates</returns>
    private static List<(string Counter, double Value)> Rates(RecordingEmitter emitter, string instant)
    {
        return Metrics(emitter.Records.Where(record => record.CapturedAt == Utc(instant))).Where(metric => metric.Point.Name == "vmstat_per_second")
                                                                                          .Select(metric => (metric.Point.Labels!["counter"], metric.Point.Value))
                                                                                          .ToList();
    }

    /// <summary>
    /// Asserts that rates are the expected ones in any order.
    /// </summary>
    /// <param name="actual">The rates</param>
    /// <param name="expected">The expected rates</param>
    private static void AssertRates(List<(string Counter, double Value)> actual, (string Counter, double Value)[] expected)
    {
        Assert.HasCount(expected.Length, actual, "the number of rates");

        foreach (var (counter, value) in expected)
        {
            Assert.AreEqual(value, actual.Single(rate => rate.Counter == counter).Value, Delta, $"rate of {counter}");
        }
    }

    /// <summary>
    /// Creates the pattern of a dotted quad.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Pattern();

    /// <summary>
    /// Creates the pattern of text in brackets that can only be an IPv6 address.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"\[([0-9A-Fa-f:.]+)\]")]
    private static partial Regex BracketPattern();

    /// <summary>
    /// Returns the number of metrics a section produces within its cap.
    /// </summary>
    /// <param name="section">The section</param>
    /// <returns>The number of metrics</returns>
    private static int ExpectedMetrics(string section)
    {
        return section switch
               {
                   "meminfo" => 128, "pressure" => 12, _ => 4
               };
    }

    /// <summary>
    /// Builds the top part of a snapshot with a structural defect.
    /// </summary>
    /// <param name="defect">The defect</param>
    /// <param name="row">The one row of the table</param>
    /// <returns>The lines</returns>
    private static List<string> BrokenTop(string defect, string row)
    {
        switch (defect)
        {
            case "no-tasks":
                return [Samples.Load, Samples.Cpu, Samples.Mem, Samples.Swap, string.Empty, Samples.TableHeader, row];
            case "swapped":
                return [Samples.Load, Samples.Tasks(1), Samples.Mem, Samples.Cpu, Samples.Swap, string.Empty, Samples.TableHeader, row];
            case "unreadable-memory":
                return [Samples.Load, Samples.Tasks(1), Samples.Cpu, "KiB Mem : NaN total, 1 free, 1 used, 1 buff/cache", Samples.Swap, string.Empty, Samples.TableHeader, row];
            case "unreadable-load":
                return ["top - 09:20:01 up 1 day", Samples.Tasks(1), Samples.Cpu, Samples.Mem, Samples.Swap, string.Empty, Samples.TableHeader, row];
            case "marker-before-table":
                return [Samples.Load, Samples.Tasks(1), Samples.Cpu, Samples.Mem, Samples.Swap, string.Empty, MeminfoMarker, "MemTotal: 1 kB", Samples.TableHeader, row];
            default:
                return [Samples.Load, "garbage", Samples.Tasks(1), Samples.Cpu, Samples.Mem, Samples.Swap, string.Empty, Samples.TableHeader, row];
        }
    }

    /// <summary>
    /// Returns whether an address is in a documentation range, loopback or unspecified.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns><c>true</c> when the address is one of those</returns>
    private static bool IsDocumentationAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address) || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return bytes[0] == 127
               || IPAddress.Any.Equals(address)
               || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
               || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
               || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    /// <summary>
    /// Parses a text with the time zone Europe/Berlin.
    /// </summary>
    /// <param name="content">The text</param>
    /// <returns>The emitter with the result</returns>
    private Task<RecordingEmitter> ParseAsync(string content)
    {
        return RecordingEmitter.ParseAsync(new LegacyMonitorParser(_berlin), _file, content, TestContext.CancellationToken);
    }

    /// <summary>
    /// Parses a snapshot with the given lsof lines in its section, followed by a second snapshot, and returns its only connection snapshot.
    /// </summary>
    /// <param name="sockets">The lsof lines after the header line</param>
    /// <returns>The connection snapshot</returns>
    private async Task<ConnectionSnapshot> ConnectionSnapshotOfAsync(List<string> sockets)
    {
        var lines = Samples.New(Samples.Oct2, [LsofMarker, Samples.LsofHeader, .. sockets], Samples.Row(100, "root", "1000", "apache2"));

        lines.AddRange(Samples.Old(Samples.Oct2Later, Samples.Row(100, "root", "1000", "apache2")));

        var emitter = await ParseAsync(Samples.Text(lines));

        Assert.IsEmpty(emitter.Skips, "no skip");

        return Payloads<ConnectionSnapshot>(emitter.Records).Single();
    }

    /// <summary>
    /// Parses the segments and measures the retained memory at the connection snapshot with the given number.
    /// </summary>
    /// <param name="segments">The segments with the number of times each one repeats</param>
    /// <param name="measureAt">The number of the connection snapshot at which the memory is taken</param>
    /// <returns>The retained memory, or -1 when it was not taken, and the emitter</returns>
    private async Task<(long Retained, CountingEmitter Emitter)> MeasureAsync(IReadOnlyList<(byte[] Bytes, long Repeat)> segments, int measureAt)
    {
        var emitter = new CountingEmitter();
        var retained = -1L;
        var seen = 0;

        emitter.Observed = record =>
                           {
                               if (record.Data is ConnectionSnapshot && ++seen == measureAt)
                               {
                                   retained = RetainedBytes();
                               }
                           };

        using var input = new SegmentStream(segments);

        await new LegacyMonitorParser(_berlin).ParseAsync(_file, input, emitter, TestContext.CancellationToken);

        return (retained, emitter);
    }

    #endregion // Methods
}