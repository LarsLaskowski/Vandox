using System.Globalization;
using System.Text;

using NodaTime;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ErrorLogParser"/>
/// </summary>
[TestClass]
public class ErrorLogParserTests
{
    #region Constants

    private const string Sentinel = "QXZ-7741-SENTINEL";
    private const string NotPlesk = "not a Plesk panel log line";
    private const string NotNginx = "not an nginx error log line";
    private const string NotApache = "not an Apache error log line";
    private const string InvalidDate = "invalid date";
    private const string OutsideRange = "time outside the storable range";
    private const string NoTime = "raw line without a time";
    private const string Empty = "empty line";
    private const string PleskHead = "[2026-10-09 03:12:12.123] 4821:5f3a9c1e INFO [panel] Session opened\n";
    private const string NginxHead = "2026/10/09 03:12:12 [error] 1021#1021: *15 connect() failed\n";
    private const string ApacheHead = "[Fri Oct 09 03:12:12.123456 2026] [core:error] [pid 1] x\n";
    private const string Apache22Head = "[Fri Oct 09 03:12:12 2026] [error] [client 198.51.100.8] x\n";
    private const string NginxLine = "2026/10/09 03:12:12 [error] 1021#1021: *15 connect() failed";
    private const string NginxLineLater = "2026/10/09 03:12:15 [warn] 1021#1021: later";

    #endregion // Constants

    #region Fields

    private static readonly int[] _warningsBeforeRecords = [0, 1, 1, 2];
    private static readonly DateTimeOffset _modTime = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A line of the Plesk panel format becomes a record of source <c>plesk-panel</c> and origin import, with the time stamp read in the zone and the fraction added, the priority of the level, the text after the time stamp as message, and empty host, program and event.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncFillsTheFieldsOfAPleskPanelRecord()
    {
        // Arrange
        var parser = Create(ErrorLogParser.PleskPanelType, BerlinZone());

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("plesk/panel.log", null), PleskHead, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("plesk-panel", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12, TimeSpan.Zero).AddTicks(1230000), record.CapturedAt, "03:12:12.123 in Berlin summer time is 01:12:12.123 UTC");
        Assert.AreEqual(TimeSpan.Zero, record.CapturedAt.Offset, "UTC");
        Assert.AreEqual("plesk/panel.log", payload.Log, "log is the file name");
        Assert.AreEqual(string.Empty, payload.Host, "host");
        Assert.AreEqual(string.Empty, payload.Program, "program");
        Assert.AreEqual(0, payload.Pid, "process ID");
        Assert.AreEqual((byte)6, payload.Priority, "priority of INFO");
        Assert.AreEqual(string.Empty, payload.Event, "event");
        Assert.AreEqual("4821:5f3a9c1e INFO [panel] Session opened", payload.Message, "text after the time stamp");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// A line of the nginx format becomes a record of the parser's own source type, for the nginx error log and the sw-cp-server error log alike.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(ErrorLogParser.NginxType)]
    [DataRow(ErrorLogParser.SwCpServerType)]
    public async Task ErrorLogParserParseAsyncFillsTheFieldsOfAnNginxRecord(string type)
    {
        // Arrange
        var parser = Create(type, BerlinZone());

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", null), NginxHead, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual(type, record.Source, "source is the type of the parser");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12, TimeSpan.Zero), record.CapturedAt, "03:12:12 in Berlin summer time is 01:12:12 UTC");
        Assert.AreEqual("nginx/error.log", payload.Log, "log is the file name");
        Assert.AreEqual(string.Empty, payload.Host, "host");
        Assert.AreEqual(string.Empty, payload.Program, "program");
        Assert.AreEqual(0, payload.Pid, "process ID");
        Assert.AreEqual((byte)3, payload.Priority, "priority of error");
        Assert.AreEqual(string.Empty, payload.Event, "event");
        Assert.AreEqual("[error] 1021#1021: *15 connect() failed", payload.Message, "text from the bracket of the level on");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// A line of the Apache format becomes a record of source <c>apache-error</c> with the fraction of microseconds added to the local time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncFillsTheFieldsOfAnApacheRecord()
    {
        // Arrange
        var parser = Create(ErrorLogParser.ApacheType, BerlinZone());

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("apache2/error.log", null), ApacheHead, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.HasCount(1, emitter.Records, "one record");

        var record = emitter.Records[0];
        var payload = RecordingEmitter.Line(record);

        Assert.AreEqual(RecordOrigin.Import, record.Origin, "origin");
        Assert.AreEqual("apache-error", record.Source, "source");
        Assert.AreEqual(0UL, record.Seq, "sequence number");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12, TimeSpan.Zero).AddTicks(1234560), record.CapturedAt, "03:12:12.123456 in Berlin summer time is 01:12:12.123456 UTC");
        Assert.AreEqual("apache2/error.log", payload.Log, "log is the file name");
        Assert.AreEqual(string.Empty, payload.Host, "host");
        Assert.AreEqual(string.Empty, payload.Program, "program");
        Assert.AreEqual(0, payload.Pid, "process ID");
        Assert.AreEqual((byte)3, payload.Priority, "priority of error");
        Assert.AreEqual(string.Empty, payload.Event, "event");
        Assert.AreEqual("[core:error] [pid 1] x", payload.Message, "text from the bracket of the level on");
        Assert.IsFalse(payload.Truncated, "not truncated");
    }

    /// <summary>
    /// Each fixture gives a record per line with the priority of the level, the time of the line and the text after the time stamp; the one continuation line is a raw record with the time of the record before it and one warning.
    /// </summary>
    /// <param name="path">The fixture</param>
    /// <param name="type">The source type of the parser</param>
    /// <param name="priorities">The expected priorities in file order, a dash for none</param>
    /// <param name="times">The expected capture times in file order</param>
    /// <param name="reason">The expected reason of the warning</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("testdata/logs/plesk-panel.log", ErrorLogParser.PleskPanelType, "6|4|3|-|7|2", "01:12:12.1230000|01:12:13.4560000|01:12:14.0000000|01:12:14.0000000|01:12:15.0007890|01:12:16.0000000", NotPlesk)]
    [DataRow("testdata/logs/nginx-error.log", ErrorLogParser.NginxType, "5|4|3|-|2|6", "01:12:12.0000000|01:12:13.0000000|01:12:14.0000000|01:12:14.0000000|01:12:15.0000000|01:12:16.0000000", NotNginx)]
    [DataRow("testdata/logs/apache-error.log", ErrorLogParser.ApacheType, "5|5|3|-|4|3", "01:12:12.1234560|01:12:13.2345670|01:12:14.3456780|01:12:14.3456780|01:12:15.4567890|01:12:16.0000000", NotApache)]
    public async Task ErrorLogParserParseAsyncReadsTheFixtures(string path, string type, string priorities, string times, string reason)
    {
        // Arrange
        // The Plesk sample is constructed from the documented shape of the panel log; the nginx and Apache samples follow the formats of the sources.
        var parser = Create(type, BerlinZone());
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(path), TestContext.CancellationToken);
        var lines = Encoding.UTF8.GetString(content).TrimEnd('\n').Split('\n');

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("logs/sample", null), content, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(6, lines, "the fixture has six lines");
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(6, emitter.Records, "one record per line");
        Assert.AreEqual(priorities, string.Join('|', emitter.Records.Select(record => RecordingEmitter.Line(record).Priority is { } priority ? priority.ToString(CultureInfo.InvariantCulture) : "-")), "priorities");
        Assert.AreEqual(times, string.Join('|', emitter.Records.Select(record => record.CapturedAt.UtcDateTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture))), "times of day in UTC");
        Assert.IsTrue(emitter.Records.All(record => record.CapturedAt.UtcDateTime.Date == new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)), "all lines are of 2026-10-09");
        Assert.AreSequenceEqual<(long, string)>([(4, reason)], emitter.Warnings, "one warning, for the continuation line");

        for (var index = 0; index < lines.Length; index++)
        {
            var record = emitter.Records[index];

            Assert.AreEqual(type, record.Source, $"source of line {index + 1}");
            Assert.AreEqual(index == 3 ? lines[index] : MessageOf(type, lines[index]), RecordingEmitter.Line(record).Message, $"message of line {index + 1}");
        }
    }

    /// <summary>
    /// The three samples become the records of their golden files, the records the Go collector is compared with as well.
    /// </summary>
    /// <param name="name">The name of the sample and its golden file</param>
    /// <param name="type">The source type of the parser</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("plesk-panel", ErrorLogParser.PleskPanelType)]
    [DataRow("nginx-error", ErrorLogParser.NginxType)]
    [DataRow("apache-error", ErrorLogParser.ApacheType)]
    public async Task ErrorLogParserParseAsyncParsesTheSamplesAsTheGoldenRecords(string name, string type)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.ForOffset(Offset.FromHours(2)));
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path($"testdata/logs/{name}.log"), TestContext.CancellationToken);
        var golden = await ErrorLogGolden.ReadLinesAsync($"testdata/logs/{name}.records.jsonl", TestContext.CancellationToken);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("logs/sample", null), content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(6, golden, "one golden record per line of the sample");
        Assert.HasCount(golden.Count, emitter.Records, "one record per golden record");

        for (var index = 0; index < golden.Count; index++)
        {
            Assert.AreEqual(ErrorLogGolden.DescribeLine(golden[index]), ErrorLogGolden.Describe(emitter.Records[index]), $"record {index}");
        }
    }

    /// <summary>
    /// The web signature fixtures become the records of their golden files in UTC.
    /// </summary>
    /// <param name="name">The name of the fixture and its golden file</param>
    /// <param name="type">The source type of the parser</param>
    /// <param name="count">The number of golden records</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("web-signatures-nginx", ErrorLogParser.NginxType, 7)]
    [DataRow("web-signatures-apache", ErrorLogParser.ApacheType, 1)]
    public async Task ErrorLogParserParseAsyncParsesTheWebSignatureFixturesAsTheGoldenRecords(string name, string type, int count)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.Utc);
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path($"testdata/logs/{name}.log"), TestContext.CancellationToken);
        var golden = await ErrorLogGolden.ReadLinesAsync($"testdata/logs/{name}.records.jsonl", TestContext.CancellationToken);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("logs/sample", null), content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(count, golden, "the golden records of the fixture");
        Assert.HasCount(golden.Count, emitter.Records, "one record per golden record");

        for (var index = 0; index < golden.Count; index++)
        {
            Assert.AreEqual(ErrorLogGolden.DescribeLine(golden[index]), ErrorLogGolden.Describe(emitter.Records[index]), $"record {index}");
        }
    }

    /// <summary>
    /// The daylight saving fixture, a time in the skipped hour and two passes through the repeated hour, becomes the records of its golden file in Europe/Berlin.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncParsesTheDaylightSavingFixtureAsTheGoldenRecords()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, BerlinZone());
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/logs/nginx-error-dst.log"), TestContext.CancellationToken);
        var golden = await ErrorLogGolden.ReadLinesAsync("testdata/logs/nginx-error-dst.records.jsonl", TestContext.CancellationToken);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("logs/sample", null), content, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.HasCount(5, golden, "five golden records");
        Assert.HasCount(golden.Count, emitter.Records, "one record per golden record");

        for (var index = 0; index < golden.Count; index++)
        {
            Assert.AreEqual(ErrorLogGolden.DescribeLine(golden[index]), ErrorLogGolden.Describe(emitter.Records[index]), $"record {index}");
        }
    }

    /// <summary>
    /// A time in the repeated hour at the end of daylight saving time takes the earlier offset unless that lies more than ten minutes before the previous recognized line, in which case it takes the later offset.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncResolvesTheRepeatedHourFromThePreviousEntry()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, BerlinZone());
        var text = string.Join('\n',
                               "2026/10/25 02:10:00 [error] first, before the repeated hour has ended",
                               "2026/10/25 02:50:00 [error] second, still the first pass through the hour",
                               "2026/10/25 02:05:00 [error] third, the second pass through the hour",
                               "2026/10/25 03:10:00 [error] fourth, after the hour",
                               string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", null), text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.AreEqual("00:10|00:50|01:05|02:10", string.Join('|', emitter.Records.Select(record => record.CapturedAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))), "the third line takes the later offset because the earlier one lies 45 minutes before the second");
        Assert.IsTrue(emitter.Records.All(record => record.CapturedAt.UtcDateTime.Date == new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc)), "all lines are of 2026-10-25");
    }

    /// <summary>
    /// A line whose date does not exist or whose instant is outside the storable range is kept as a raw record with the reason, also when only the fraction leaves the range.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <param name="line">The line</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(ErrorLogParser.NginxType, "2026/02/30 03:12:12 [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.NginxType, "2026/13/01 03:12:12 [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.NginxType, "2026/10/09 24:00:00 [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.NginxType, "2026/10/09 03:60:00 [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.NginxType, "1500/01/01 00:00:00 [error] x", OutsideRange)]
    [DataRow(ErrorLogParser.NginxType, "2300/01/01 00:00:00 [error] x", OutsideRange)]
    [DataRow(ErrorLogParser.PleskPanelType, "[2026-02-30 03:12:12] INFO x", InvalidDate)]
    [DataRow(ErrorLogParser.PleskPanelType, "[2262-04-11 23:47:16.999999] INFO x", OutsideRange)]
    [DataRow(ErrorLogParser.PleskPanelType, "[2262-04-11 23:47:17] INFO x", OutsideRange)]
    [DataRow(ErrorLogParser.PleskPanelType, "[1677-09-21 00:12:42] INFO x", OutsideRange)]
    [DataRow(ErrorLogParser.PleskPanelType, "[1677-09-21 00:12:43.5] INFO x", OutsideRange)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Feb 30 03:12:12 2026] [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 09 24:00:00 2026] [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 99 03:12:12 2026] [error] x", InvalidDate)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 09 03:12:12 1500] [error] x", OutsideRange)]
    public async Task ErrorLogParserParseAsyncKeepsLinesWithAnInvalidOrUnstorableTimeAsRawLines(string type, string line, string reason)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.Utc);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("logs/error.log", _modTime), line + "\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.AreSequenceEqual<(long, string)>([(1, reason)], emitter.Warnings, "one warning with the reason of the time");
        Assert.HasCount(1, emitter.Records, "the line is stored");

        var payload = RecordingEmitter.Line(emitter.Records[0]);

        Assert.AreEqual(line, payload.Message, "the whole line is the message");
        Assert.IsNull(payload.Priority, "no priority");
        Assert.AreEqual(_modTime, emitter.Records[0].CapturedAt, "the modification time stands in for the time that does not resolve");
    }

    /// <summary>
    /// A time at the end of the storable range that the fraction does not leave is stored as a line of the format.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncStoresATimeWhoseFractionStaysInsideTheStorableRange()
    {
        // Arrange
        var parser = Create(ErrorLogParser.PleskPanelType, DateTimeZone.Utc);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("plesk/panel.log", null), "[2262-04-11 23:47:16.5] INFO x\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.IsEmpty(emitter.Warnings, "nothing is warned about");
        Assert.HasCount(1, emitter.Records, "one record");
        Assert.AreEqual(new DateTimeOffset(2262, 4, 11, 23, 47, 16, TimeSpan.Zero).AddTicks(5000000), emitter.Records[0].CapturedAt, "the fraction is added to the whole second");
        Assert.AreEqual((byte)6, RecordingEmitter.Line(emitter.Records[0]).Priority, "priority of INFO");
    }

    /// <summary>
    /// Without a time zone the first line of the format fails the file with a fixed message; the empty line and the raw line before it are handled first and the raw line is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncWithoutZoneFailsAtTheFirstLineOfTheFormat()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, null);
        var emitter = new RecordingEmitter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"\ngarbage\n{NginxLine}\n{NginxLine}\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(new LogFile("nginx/error.log", _modTime), input, emitter, TestContext.CancellationToken), "the parse fails");

        // Assert
        Assert.AreEqual("import.time_zone is not set", exception.Message, "fixed message without input text");
        Assert.AreEqual(1, emitter.Calls, "only the raw line before the first line of the format is emitted");
        Assert.AreSequenceEqual<(long, string)>([(1, Empty)], emitter.Skips, "the empty line is skipped");
        Assert.AreSequenceEqual<(long, string)>([(2, NotNginx)], emitter.Warnings, "the raw line is warned about");
        Assert.AreEqual("garbage", RecordingEmitter.Line(emitter.Records.Single()).Message, "the raw line is stored");
    }

    /// <summary>
    /// A non-empty line that is not of the format, or whose time does not resolve, becomes a record without priority whose message is the whole line, with the capture time of the record before it; the parser warns before it emits the record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncKeepsUnreadableLinesAsRawRecordsWithAWarning()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var warningsSeen = new List<int>();
        var emitter = new RecordingEmitter();

        emitter.Observed = _ => warningsSeen.Add(emitter.Warnings.Count);

        var text = string.Join('\n', NginxLine, "garbage continuation", NginxLineLater, "2026/02/30 03:12:12 [error] bad date", string.Empty);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));

        // Act
        await parser.ParseAsync(new LogFile("nginx/error.log", _modTime), input, emitter, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.AreSequenceEqual<(long, string)>([(2, NotNginx), (4, InvalidDate)], emitter.Warnings, "both raw lines are warned about with their line numbers");
        Assert.AreSequenceEqual(_warningsBeforeRecords, warningsSeen, "each raw line is warned about before its record is emitted");
        Assert.HasCount(4, emitter.Records, "every line is stored");

        var first = new DateTimeOffset(2026, 10, 9, 3, 12, 12, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 10, 9, 3, 12, 15, TimeSpan.Zero);

        Assert.AreEqual("garbage continuation", RecordingEmitter.Line(emitter.Records[1]).Message, "message of the first raw line");
        Assert.AreEqual("2026/02/30 03:12:12 [error] bad date", RecordingEmitter.Line(emitter.Records[3]).Message, "message of the second raw line");
        Assert.IsNull(RecordingEmitter.Line(emitter.Records[1]).Priority, "no priority on the first raw line");
        Assert.IsNull(RecordingEmitter.Line(emitter.Records[3]).Priority, "no priority on the second raw line");
        Assert.AreEqual(first, emitter.Records[1].CapturedAt, "the first raw line takes the time of the line before it");
        Assert.AreEqual(later, emitter.Records[3].CapturedAt, "the second raw line takes the time of the line before it");
        Assert.AreEqual("nginx-error", emitter.Records[1].Source, "source of a raw line");
        Assert.AreEqual(RecordOrigin.Import, emitter.Records[1].Origin, "origin of a raw line");
        Assert.AreEqual("nginx/error.log", RecordingEmitter.Line(emitter.Records[1]).Log, "log of a raw line");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[1]).Truncated, "a raw line is not truncated");
    }

    /// <summary>
    /// A raw line before the first line of the format takes the modification time of the file, also at both ends of the storable range.
    /// </summary>
    /// <param name="kind">Which modification time</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("min")]
    [DataRow("max")]
    public async Task ErrorLogParserParseAsyncGivesRawLinesBeforeTheFirstEntryTheModificationTime(string kind)
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var modTime = ModTimeOf(kind);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", modTime), "garbage\nmore garbage\n", TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.AreSequenceEqual<(long, string)>([(1, NotNginx), (2, NotNginx)], emitter.Warnings, "both lines are warned about");
        Assert.HasCount(2, emitter.Records, "both lines are stored");
        Assert.AreEqual(modTime, emitter.Records[0].CapturedAt, "the first raw line takes the modification time");
        Assert.AreEqual(modTime, emitter.Records[1].CapturedAt, "the second raw line takes the time of the record before it");
    }

    /// <summary>
    /// A raw line without a time from the record before it and without a storable modification time is skipped, with no warning and no record; the line of the format after it is stored and the following raw line takes its time.
    /// </summary>
    /// <param name="kind">Which modification time</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("null")]
    [DataRow("2300")]
    [DataRow("1600")]
    [DataRow("max+1")]
    public async Task ErrorLogParserParseAsyncSkipsRawLinesWithoutAnyTime(string kind)
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var text = string.Join('\n', "garbage one", "garbage two", NginxLine, "garbage three", string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", ModTimeOf(kind)), text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, NoTime), (2, NoTime)], emitter.Skips, "the raw lines before the first line of the format are skipped");
        Assert.AreSequenceEqual<(long, string)>([(4, NotNginx)], emitter.Warnings, "only the raw line after the line of the format is warned about");
        Assert.HasCount(2, emitter.Records, "the line of the format and the raw line after it are stored");
        Assert.AreEqual((byte)3, RecordingEmitter.Line(emitter.Records[0]).Priority, "the line of the format is stored normally");
        Assert.AreEqual("garbage three", RecordingEmitter.Line(emitter.Records[1]).Message, "the raw line after it is stored");
        Assert.AreEqual(emitter.Records[0].CapturedAt, emitter.Records[1].CapturedAt, "the raw line takes the time of the line of the format");
    }

    /// <summary>
    /// An empty line is skipped with its 1-based line number and no warning, and the parse continues.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncSkipsEmptyLines()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var text = string.Join('\n', string.Empty, string.Empty, NginxLine, string.Empty, string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", _modTime), text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, Empty), (2, Empty), (4, Empty)], emitter.Skips, "the empty lines are skipped (the line after the last line feed does not exist)");
        Assert.IsEmpty(emitter.Warnings, "no warning");
        Assert.HasCount(1, emitter.Records, "the line of the format is stored");
    }

    /// <summary>
    /// A whole file of an unknown format becomes raw records only, with the reason of the parser's own format: text of no format, and the lines of another known format.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <param name="source">The fixture to read, or <c>text</c> for text of no format</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(ErrorLogParser.NginxType, "text", NotNginx)]
    [DataRow(ErrorLogParser.NginxType, "testdata/logs/apache-error.log", NotNginx)]
    [DataRow(ErrorLogParser.SwCpServerType, "testdata/logs/apache-error.log", NotNginx)]
    [DataRow(ErrorLogParser.PleskPanelType, "testdata/logs/nginx-error.log", NotPlesk)]
    [DataRow(ErrorLogParser.ApacheType, "testdata/logs/plesk-panel.log", NotApache)]
    public async Task ErrorLogParserParseAsyncReadsAWholeFileOfAnUnknownFormatAsRawLines(string type, string source, string reason)
    {
        // Arrange
        var parser = Create(type, BerlinZone());
        var content = source == "text" ? Encoding.UTF8.GetBytes("Just some text\nthat is no log\n\nof any format\n") : await File.ReadAllBytesAsync(RepositoryFiles.Path(source), TestContext.CancellationToken);
        var lines = Encoding.UTF8.GetString(content).Split('\n').Where(line => line.Length > 0).ToList();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("some/file", _modTime), content, TestContext.CancellationToken);

        // Assert
        Assert.IsGreaterThan(2, lines.Count, "the input has lines");
        Assert.HasCount(lines.Count, emitter.Records, "every non-empty line is stored");
        Assert.HasCount(lines.Count, emitter.Warnings, "every non-empty line is warned about");
        Assert.IsTrue(emitter.Warnings.All(warning => warning.Reason == reason), "the reason is the one of the parser's format");
        Assert.AreEqual(string.Join('\n', lines), string.Join('\n', emitter.Records.Select(record => RecordingEmitter.Line(record).Message)), "the messages are the lines");
        Assert.IsTrue(emitter.Records.All(record => RecordingEmitter.Line(record).Priority is null && record.CapturedAt == _modTime && record.Source == type), "no priority, the modification time and the source of the parser");
    }

    /// <summary>
    /// A byte order mark is removed from the first line only: there the line of the format is a record, a raw line is stored without it, and on a later line the mark stays in the message of the raw line.
    /// </summary>
    /// <param name="mode">Where the mark is: <c>entry</c> on a line of the format on line 1, <c>raw</c> on a raw line 1, <c>later</c> on line 2</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("entry")]
    [DataRow("raw")]
    [DataRow("later")]
    public async Task ErrorLogParserParseAsyncRemovesTheByteOrderMarkOfLineOne(string mode)
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] content = mode switch
                         {
                             "entry" => [.. bom, .. Encoding.UTF8.GetBytes(NginxLine + "\n")],
                             "raw" => [.. bom, .. "garbage\n"u8],
                             _ => [.. "\n"u8, .. bom, .. Encoding.UTF8.GetBytes(NginxLine + "\n")]
                         };

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", _modTime), content, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, emitter.Records, "one record");

        var payload = RecordingEmitter.Line(emitter.Records[0]);

        switch (mode)
        {
            case "entry":
                {
                    Assert.IsEmpty(emitter.Warnings, "the line of line 1 is a line of the format");
                    Assert.AreEqual((byte)3, payload.Priority, "priority");
                    Assert.AreEqual("[error] 1021#1021: *15 connect() failed", payload.Message, "message");
                }
                break;

            case "raw":
                {
                    Assert.AreSequenceEqual<(long, string)>([(1, NotNginx)], emitter.Warnings, "the raw line is warned about");
                    Assert.AreEqual("garbage", payload.Message, "the mark is not part of the raw message on line 1");
                }
                break;

            default:
                {
                    Assert.AreSequenceEqual<(long, string)>([(2, NotNginx)], emitter.Warnings, "the mark on line 2 makes the line a raw line");
                    Assert.AreEqual(new string((char)0xFEFF, 1) + NginxLine, payload.Message, "the mark stays in the message of a later line");
                }
                break;
        }
    }

    /// <summary>
    /// A message of invalid bytes that grow when they are decoded is cut to the limit in UTF-8 bytes and the record is marked as truncated, for a line of the format and for a raw line.
    /// </summary>
    /// <param name="prefixText">The start of the line</param>
    /// <param name="expectedStart">The expected start of the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026/10/09 03:12:12 [error] ", "[error] ")]
    [DataRow("garbage ", "garbage ")]
    public async Task ErrorLogParserParseAsyncCutsALongMessageAndMarksItTruncated(string prefixText, string expectedStart)
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var prefix = Encoding.UTF8.GetBytes(prefixText);
        var line = prefix.Concat(Enumerable.Repeat((byte)0xFF, LogLineReader.MaxLineBytes - prefix.Length)).Append((byte)'\n').ToArray();

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", _modTime), line, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "no record is refused");
        Assert.HasCount(1, emitter.Records, "one record");

        var payload = RecordingEmitter.Line(emitter.Records[0]);

        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(payload.Message), "message within the limit in UTF-8 bytes");
        Assert.IsTrue(payload.Truncated, "truncated");
        Assert.StartsWith(expectedStart, payload.Message, "the start of the message is kept");
    }

    /// <summary>
    /// A line cut by the line reader is marked as truncated and its message stays within the limit; the next line is not truncated.
    /// </summary>
    /// <param name="prefixText">The start of the first line</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("2026/10/09 03:12:12 [error] ")]
    [DataRow("garbage ")]
    public async Task ErrorLogParserParseAsyncMarksALineCutByTheReaderAsTruncated(string prefixText)
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var text = $"{prefixText}{new string('a', LogLineReader.MaxLineBytes + 5000)}\n{NginxLineLater}\n";

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", _modTime), text, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(emitter.Skips, "nothing is skipped");
        Assert.HasCount(2, emitter.Records, "both lines are records");
        Assert.IsTrue(RecordingEmitter.Line(emitter.Records[0]).Truncated, "truncated");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(RecordingEmitter.Line(emitter.Records[0]).Message), "message within the limit");
        Assert.IsFalse(RecordingEmitter.Line(emitter.Records[1]).Truncated, "the next line is not truncated");
    }

    /// <summary>
    /// A cancelled token ends the parse with an operation canceled exception before anything is emitted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncHonorsACancelledToken()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var emitter = new RecordingEmitter();
        using var cancelled = new CancellationTokenSource();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(NginxLine + "\n"));

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => parser.ParseAsync(new LogFile("nginx/error.log", null), input, emitter, cancelled.Token), "cancelled parse");
        Assert.AreEqual(0, emitter.Calls, "nothing was emitted");
    }

    /// <summary>
    /// An exception of the emitter ends the parse with that exception and without another call.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncPassesOnTheExceptionOfTheEmitter()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var failure = new InvalidOperationException("store is full");
        var emitter = new RecordingEmitter
                      {
                          Failure = (_, _) => failure
                      };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"{NginxLine}\n{NginxLineLater}\n"));

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => parser.ParseAsync(new LogFile("nginx/error.log", null), input, emitter, TestContext.CancellationToken), "the emitter fails");

        // Assert
        Assert.AreSame(failure, thrown, "the exception of the emitter");
        Assert.AreEqual(1, emitter.Calls, "the emitter is not called again");
    }

    /// <summary>
    /// A skip reason and a warning reason are one of the fixed texts and never hold a part of the line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncReportsOnlyFixedReasons()
    {
        // Arrange
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var text = string.Join('\n',
                               Sentinel,
                               string.Empty,
                               NginxLine,
                               Sentinel,
                               $"2026/02/30 03:12:12 [error] {Sentinel}",
                               $"1000/01/01 00:00:00 [error] {Sentinel}",
                               string.Empty);

        // Act
        var emitter = await RecordingEmitter.ParseAsync(parser, new LogFile("nginx/error.log", null), text, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual<(long, string)>([(1, NoTime), (2, Empty)], emitter.Skips, "the skip reasons are fixed texts");
        Assert.AreSequenceEqual<(long, string)>([(4, NotNginx), (5, InvalidDate), (6, OutsideRange)], emitter.Warnings, "the warning reasons are fixed texts");
        Assert.DoesNotContain(Sentinel, string.Join('|', emitter.Skips.Concat(emitter.Warnings).Select(entry => entry.Reason)), "no reason holds text of the line");
    }

    /// <summary>
    /// A file is claimed by content when its first line is a line of the format, under any name that is no syslog name, also when the head ends inside that line after the level.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", PleskHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", PleskHead + PleskHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "[2026-10-09 03:12:12.123] 4821:5f3a9c1e INFO [pa", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "[2026-10-09 03:12:12] INFO", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "[2026-10-09 03:12:12] WAR", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "[2026-10-09 03:12:12]", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "[2026-10-09 03:12:12.12", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "\n" + PleskHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "garbage\n" + PleskHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", NginxHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", ApacheHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.PleskPanelType, "logs/other.txt", "", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", NginxHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", NginxHead + "garbage\n", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", "2026/10/09 03:12:12 [error] 1021#10", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", "2026/10/09 03:12:12 [error]", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", "2026/10/09 03:12:12 [err", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", "2026/10/09 03:12:12 ", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", "garbage\n" + NginxHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", PleskHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "logs/other.txt", ApacheHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "apache2/error.log", NginxHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.NginxType, "apache2/error.log", ApacheHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.NginxType, "error.log", NginxHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", ApacheHead, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", Apache22Head, Confidence.MatchContent)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", ApacheHead + "garbage\n", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", "[Fri Oct 09 03:12:12 2026] [error]", Confidence.MatchContent)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", "[Fri Oct 09 03:12:12 2026] [err", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", "[Fri Oct 09 03:12", Confidence.NoMatch)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", "AH00558: apache2: Could not reliably determine the server's fully qualified domain name\n" + ApacheHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", PleskHead, Confidence.NoMatch)]
    [DataRow(ErrorLogParser.ApacheType, "logs/other.txt", NginxHead, Confidence.NoMatch)]
    public void ErrorLogParserDetectClaimsAFileByItsFirstLine(string type, string name, string head, Confidence expected)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(expected, confidence, "confidence");
    }

    /// <summary>
    /// A byte order mark at the start of the head and a carriage return before the line feed of the first line do not keep a first line from being recognized.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <param name="head">The head of the file, the first line ending in a level</param>
    /// <param name="withMark">Whether the head starts with a byte order mark</param>
    [TestMethod]
    [DataRow(ErrorLogParser.PleskPanelType, "[2026-10-09 03:12:12] INFO\r\nnext\r\n", true)]
    [DataRow(ErrorLogParser.PleskPanelType, "[2026-10-09 03:12:12] INFO\r\nnext\r\n", false)]
    [DataRow(ErrorLogParser.PleskPanelType, "[2026-10-09 03:12:12] INFO x\n", true)]
    [DataRow(ErrorLogParser.NginxType, "2026/10/09 03:12:12 [error]\r\nnext\r\n", true)]
    [DataRow(ErrorLogParser.NginxType, "2026/10/09 03:12:12 [error]\r\nnext\r\n", false)]
    [DataRow(ErrorLogParser.NginxType, "2026/10/09 03:12:12 [error] x\n", true)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 09 03:12:12 2026] [error]\r\nnext\r\n", true)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 09 03:12:12 2026] [error]\r\nnext\r\n", false)]
    [DataRow(ErrorLogParser.ApacheType, "[Fri Oct 09 03:12:12 2026] [error] x\n", true)]
    public void ErrorLogParserDetectIgnoresAByteOrderMarkAndACarriageReturn(string type, string head, bool withMark)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.Utc);
        byte[] mark = [0xEF, 0xBB, 0xBF];
        var bytes = withMark ? mark.Concat(Encoding.UTF8.GetBytes(head)).ToArray() : Encoding.UTF8.GetBytes(head);

        // Act
        var confidence = parser.Detect(new LogFile("logs/other.txt", null), bytes);

        // Assert
        Assert.AreEqual(Confidence.MatchContent, confidence, "the first line is recognized");
    }

    /// <summary>
    /// A syslog name is never claimed by content, also when the first line is a line of the format.
    /// </summary>
    /// <param name="name">The file name</param>
    [TestMethod]
    [DataRow("syslog")]
    [DataRow("syslog.1")]
    [DataRow("kern.log")]
    [DataRow("kern.log-20261010")]
    [DataRow("var/log/syslog-20261010")]
    public void ErrorLogParserDetectNeverClaimsContentUnderASyslogName(string name)
    {
        // Arrange
        (string Type, string Head)[] cases = [(ErrorLogParser.PleskPanelType, PleskHead), (ErrorLogParser.NginxType, NginxHead), (ErrorLogParser.ApacheType, ApacheHead)];

        foreach (var (type, head) in cases)
        {
            var parser = Create(type, DateTimeZone.Utc);

            // Act
            var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

            // Assert
            Assert.AreEqual(Confidence.NoMatch, confidence, $"\"{name}\" is a syslog name, parser {type}");
        }
    }

    /// <summary>
    /// A file at the configured path of the parser is claimed as a path match whatever its head holds, also another known format, binary bytes or nothing.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    /// <param name="configuredPath">The configured path of the parser</param>
    /// <param name="name">The file name</param>
    /// <param name="head">The head of the file</param>
    [TestMethod]
    [DataRow(ErrorLogParser.NginxType, "/var/log/nginx/error.log", "nginx/error.log", ApacheHead)]
    [DataRow(ErrorLogParser.NginxType, "/var/log/nginx/error.log", "nginx/error.log", NginxHead)]
    [DataRow(ErrorLogParser.NginxType, "/var/log/nginx/error.log", "var/log/nginx/error.log.2", "")]
    [DataRow(ErrorLogParser.NginxType, "/var/log/nginx/error.log", "copy/nginx/error.log-20261010", "just text\n")]
    [DataRow(ErrorLogParser.NginxType, "/var/log/syslog", "log/syslog", NginxHead)]
    [DataRow(ErrorLogParser.SwCpServerType, "/var/log/sw-cp-server/error_log", "sw-cp-server/error_log", NginxHead)]
    [DataRow(ErrorLogParser.SwCpServerType, "/var/log/sw-cp-server/error_log", "sw-cp-server/error_log", ApacheHead)]
    [DataRow(ErrorLogParser.SwCpServerType, "/var/log/sw-cp-server/error_log", "x/sw-cp-server/error_log.3", "")]
    [DataRow(ErrorLogParser.PleskPanelType, "/var/log/plesk/panel.log", "plesk/panel.log", "garbage\n")]
    [DataRow(ErrorLogParser.PleskPanelType, "/usr/local/psa/admin/logs/panel.log", "logs/panel.log.1", PleskHead)]
    [DataRow(ErrorLogParser.ApacheType, "/var/log/apache2/error.log", "apache2/error.log", "")]
    [DataRow(ErrorLogParser.ApacheType, "/var/log/httpd/error_log", "httpd/error_log-20261010", NginxHead)]
    public void ErrorLogParserDetectClaimsAFileAtItsConfiguredPathWhateverItsContent(string type, string configuredPath, string name, string head)
    {
        // Arrange
        var parser = Create(type, DateTimeZone.Utc, configuredPath);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(head));

        // Assert
        Assert.AreEqual(Confidence.MatchPath, confidence, "path match");
    }

    /// <summary>
    /// The sw-cp-server parser has the nginx format but claims a file only by its path, never by content.
    /// </summary>
    /// <param name="name">The file name</param>
    /// <param name="expected">The expected confidence</param>
    [TestMethod]
    [DataRow("sw-cp-server/error_log", Confidence.MatchPath)]
    [DataRow("copy/sw-cp-server/error_log.1", Confidence.MatchPath)]
    [DataRow("nginx/error.log", Confidence.NoMatch)]
    [DataRow("other/error_log", Confidence.NoMatch)]
    [DataRow("sw-cp-server/error_log.old", Confidence.NoMatch)]
    [DataRow("error_log", Confidence.NoMatch)]
    public void ErrorLogParserDetectOfSwCpServerClaimsOnlyByPath(string name, Confidence expected)
    {
        // Arrange
        var parser = Create(ErrorLogParser.SwCpServerType, DateTimeZone.Utc);

        // Act
        var confidence = parser.Detect(new LogFile(name, null), Encoding.UTF8.GetBytes(NginxHead));

        // Assert
        Assert.AreEqual(expected, confidence, $"\"{name}\" with an nginx first line");
    }

    /// <summary>
    /// The parser keeps the type of its constructor and is framed by lines only, so an import may continue its files at the start of a line.
    /// </summary>
    /// <param name="type">The source type of the parser</param>
    [TestMethod]
    [DataRow(ErrorLogParser.PleskPanelType)]
    [DataRow(ErrorLogParser.SwCpServerType)]
    [DataRow(ErrorLogParser.NginxType)]
    [DataRow(ErrorLogParser.ApacheType)]
    public void ErrorLogParserIsLineFramedAndKeepsItsType(string type)
    {
        // Act
        var parser = Create(type, DateTimeZone.Utc);

        // Assert
        Assert.AreEqual(type, parser.Type, "type");
        Assert.IsTrue(parser.LineFramed, "line framed");
    }

    /// <summary>
    /// Parsing the same content twice gives the same records, skips and warnings in the same order.
    /// </summary>
    /// <param name="path">The fixture</param>
    /// <param name="type">The source type of the parser</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("testdata/logs/plesk-panel.log", ErrorLogParser.PleskPanelType)]
    [DataRow("testdata/logs/nginx-error.log", ErrorLogParser.NginxType)]
    [DataRow("testdata/logs/apache-error.log", ErrorLogParser.ApacheType)]
    [DataRow("testdata/logs/apache-error.log", ErrorLogParser.NginxType)]
    public async Task ErrorLogParserParseAsyncIsDeterministic(string path, string type)
    {
        // Arrange
        var content = await File.ReadAllBytesAsync(RepositoryFiles.Path(path), TestContext.CancellationToken);
        var parser = Create(type, BerlinZone());
        var file = new LogFile("logs/sample", _modTime);

        // Act
        var first = await RecordingEmitter.ParseAsync(parser, file, content, TestContext.CancellationToken);
        var second = await RecordingEmitter.ParseAsync(parser, file, content, TestContext.CancellationToken);

        // Assert
        Assert.IsNotEmpty(first.Records, "records were emitted");
        Assert.AreSequenceEqual(first.Records.Select(RecordingEmitter.Describe).ToList(), second.Records.Select(RecordingEmitter.Describe).ToList(), "same records in the same order");
        Assert.AreSequenceEqual(first.Skips, second.Skips, "same skips");
        Assert.AreSequenceEqual(first.Warnings, second.Warnings, "same warnings");
    }

    /// <summary>
    /// Many raw lines are emitted one by one: the memory the parse retains does not grow with their number.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ErrorLogParserParseAsyncRetainsBoundedMemoryForManyRawLines()
    {
        // Arrange
        const int lineBytes = 16;
        const long totalBytes = 8L * 1024 * 1024;
        var parser = Create(ErrorLogParser.NginxType, DateTimeZone.Utc);
        var emitter = new CountingEmitter();
        var before = 0L;
        var middle = 0L;

        emitter.Observed = _ =>
                           {
                               if (emitter.Records == totalBytes / lineBytes / 2)
                               {
                                   middle = RetainedBytes();
                               }
                           };
        before = RetainedBytes();

        using var input = new PatternStream([], "raw garbage lne\n"u8.ToArray(), totalBytes, []);

        // Act
        await parser.ParseAsync(new LogFile("nginx/error.log", _modTime), input, emitter, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(totalBytes / lineBytes, emitter.Records, "every line is a record");
        Assert.AreEqual(totalBytes / lineBytes, emitter.Warnings, "every line is warned about");
        Assert.IsGreaterThan(0L, middle, "the memory was measured in the middle of the parse");
        Assert.IsLessThan(1024 * 1024, middle - before, "retained bytes after half of the lines, keeping them would take more than 8 MiB");
    }

    /// <summary>
    /// Returns the modification time a test names.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The time, or <c>null</c></returns>
    private static DateTimeOffset? ModTimeOf(string kind)
    {
        return kind switch
               {
                   "null" => null,
                   "2300" => new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero),
                   "1600" => new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero),
                   "max+1" => StorableTime.Max.AddTicks(1),
                   "min" => StorableTime.Min,
                   "max" => StorableTime.Max,
                   _ => _modTime
               };
    }

    /// <summary>
    /// Returns the zone Europe/Berlin.
    /// </summary>
    /// <returns>The zone</returns>
    private static DateTimeZone BerlinZone()
    {
        return DateTimeZoneProviders.Tzdb["Europe/Berlin"];
    }

    /// <summary>
    /// Returns the message the parser of a type makes of a line of the format: the text after the time stamp.
    /// </summary>
    /// <param name="type">The source type</param>
    /// <param name="line">The line</param>
    /// <returns>The message</returns>
    private static string MessageOf(string type, string line)
    {
        return type == ErrorLogParser.NginxType ? line["2026/10/09 03:12:12 ".Length..] : line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
    }

    /// <summary>
    /// Creates the parser of a source type as the registry of the built-in parsers does.
    /// </summary>
    /// <param name="type">The source type</param>
    /// <param name="zone">The time zone, or <c>null</c></param>
    /// <param name="path">The configured path; the default path of the type when <c>null</c></param>
    /// <returns>The parser</returns>
    private static ErrorLogParser Create(string type, DateTimeZone? zone, string? path = null)
    {
        return type switch
               {
                   ErrorLogParser.PleskPanelType => new ErrorLogParser(type, ErrorLogFormat.PleskPanel, path ?? ErrorLogPaths.DefaultPleskPanel, true, zone),
                   ErrorLogParser.SwCpServerType => new ErrorLogParser(type, ErrorLogFormat.Nginx, path ?? ErrorLogPaths.DefaultSwCpServer, false, zone),
                   ErrorLogParser.NginxType => new ErrorLogParser(type, ErrorLogFormat.Nginx, path ?? ErrorLogPaths.DefaultNginx, true, zone),
                   _ => new ErrorLogParser(type, ErrorLogFormat.Apache, path ?? ErrorLogPaths.DefaultApache, true, zone)
               };
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

    #endregion // Methods
}