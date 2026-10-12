using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Templates;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the template statistics and queries of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreTemplateTests
{
    #region Constants

    private const string Prefix = "store: invalid query: ";

    private const long NanosecondsPerHour = 3_600_000_000_000L;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Older lines written later move first seen earlier, and newer lines move last seen later.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchMovesFirstSeenEarlierForOlderLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "service restarted"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2), "service restarted"));

        // Act
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(-5), "service restarted"));
        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(3), "service restarted"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1).AddMinutes(5), "service restarted"));

        // Assert
        Assert.AreSequenceEqual([$"service restarted|{Ns(Hour(-5))}|{Ns(Hour(3))}|5"], await RowsAsync(directory.Path, "SELECT template, first_seen, last_seen, count FROM log_templates"), "template row");
        Assert.AreSequenceEqual([$"{Ns(Hour(-5))}|1", $"{Ns(Hour(1))}|2", $"{Ns(Hour(2))}|1", $"{Ns(Hour(3))}|1"], await RowsAsync(directory.Path, "SELECT hour, count FROM log_template_hours ORDER BY hour"), "template hours");
    }

    /// <summary>
    /// A resent agent record counts nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchDoesNotCountDuplicateAgentRecords()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = Samples.LogAt(RecordOrigin.Agent, 1, "syslog", "sshd", Hour(0), "login failed for user");
        var second = Samples.LogAt(RecordOrigin.Agent, 2, "syslog", "sshd", Hour(0).AddMinutes(1), "login failed for user");

        await store.WriteBatchAsync(Samples.AgentBatch(first), TestContext.CancellationToken);

        // Act
        var resent = await store.WriteBatchAsync(Samples.AgentBatch(first, second), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(new WriteResult(1, 1), resent, "one record stored, one duplicate");
        Assert.AreSequenceEqual(["login failed for user|2"], await RowsAsync(directory.Path, "SELECT template, count FROM log_templates"), "template count");
        Assert.AreSequenceEqual([$"{Ns(Hour(0))}|2"], await RowsAsync(directory.Path, "SELECT hour, count FROM log_template_hours"), "template hour count");
        Assert.AreSequenceEqual([$"syslog|{Ns(Hour(0))}|2"], await RowsAsync(directory.Path, "SELECT source, hour, count FROM log_source_hours"), "source hour count");
    }

    /// <summary>
    /// A batch without log lines writes no template row.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchWithoutLogLinesWritesNoTemplate()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await WriteAsync(store, Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"), Samples.Gap(1));

        // Assert
        Assert.IsEmpty(await TableRowsAsync(directory.Path), "no template row of any table");
    }

    /// <summary>
    /// The lines of a source are counted per hour, whatever their templates.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchCountsLinesPerSourceAndHour()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "first text"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "cron", Hour(0).AddMinutes(5), "second text"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(6), "third text"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2), "first text"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(0), "first text"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(50), "fourth text"));

        // Assert
        Assert.AreSequenceEqual([$"journal|{Ns(Hour(0))}|1", $"syslog|{Ns(Hour(0))}|4", $"syslog|{Ns(Hour(2))}|1"], await RowsAsync(directory.Path, "SELECT source, hour, count FROM log_source_hours ORDER BY source, hour"), "lines per source and hour");
    }

    /// <summary>
    /// Capture times at both edges of the storable range are counted without exception, and the first hour is clamped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchCountsLinesAtTheEdgesOfTheStorableRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = Ns(StorableTime.Min);
        var last = Ns(StorableTime.Max);
        var lastHour = last - (last % NanosecondsPerHour);

        // Act
        var result = await store.WriteBatchAsync(Samples.Batch(Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", StorableTime.Min, "edge reached"), Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", StorableTime.Max, "edge reached")), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(new WriteResult(2, 0), result, "both records stored");
        Assert.AreSequenceEqual([$"edge reached|{first}|{last}|2"], await RowsAsync(directory.Path, "SELECT template, first_seen, last_seen, count FROM log_templates"), "first and last seen are the two times");
        Assert.AreSequenceEqual([$"{long.MinValue}|1", $"{lastHour}|1"], await RowsAsync(directory.Path, "SELECT hour, count FROM log_template_hours ORDER BY hour"), "template hours: the first is clamped, the last is floored");
        Assert.AreSequenceEqual([$"syslog|{long.MinValue}|1", $"syslog|{lastHour}|1"], await RowsAsync(directory.Path, "SELECT source, hour, count FROM log_source_hours ORDER BY hour"), "source hours: the first is clamped, the last is floored");
    }

    /// <summary>
    /// The template of a long stored message is cut at 1,024 UTF-8 bytes and equals the normalizer's result.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchCutsLongTemplates()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };
        var message = string.Join(' ', Enumerable.Range(0, 3000).Select(index => words[index % words.Length]))[..16384];

        // Act
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), message));

        var stored = await RowsAsync(directory.Path, "SELECT template FROM log_templates");

        // Assert
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(message), "the message is 16,384 bytes");
        Assert.HasCount(1, stored, "one template row");
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(stored[0]), "the stored template is cut at 1,024 bytes");
        Assert.AreEqual(LogTemplateNormalizer.Normalize(message), stored[0], "the stored template equals the normalizer's result");
    }

    /// <summary>
    /// The template of a message is found by its source, program and message, with its statistics, and an unknown message gives none.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreFindTemplateReturnsTheTemplateOfAMessage()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "postfix", Hour(0), "connect from unknown[203.0.113.7]"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "postfix", Hour(4), "connect from unknown[198.51.100.2]"));

        var id = await RowsAsync(directory.Path, "SELECT id FROM log_templates");

        // Act
        var found = await store.FindTemplateAsync("syslog", "postfix", "connect from unknown[192.0.2.55]", TestContext.CancellationToken);
        var otherProgram = await store.FindTemplateAsync("syslog", "dovecot", "connect from unknown[192.0.2.55]", TestContext.CancellationToken);
        var otherSource = await store.FindTemplateAsync("journal", "postfix", "connect from unknown[192.0.2.55]", TestContext.CancellationToken);
        var otherText = await store.FindTemplateAsync("syslog", "postfix", "disconnect from unknown[192.0.2.55]", TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(found, "the template is found");
        Assert.AreEqual(long.Parse(id[0], CultureInfo.InvariantCulture), found.Id, "ID");
        Assert.AreEqual("syslog", found.Source, "source");
        Assert.AreEqual("postfix", found.Program, "program");
        Assert.AreEqual("connect from unknown[<ip>]", found.Template, "template");
        Assert.AreEqual(Hour(0), found.FirstSeen, "first seen");
        Assert.AreEqual(Hour(4), found.LastSeen, "last seen");
        Assert.AreEqual(2L, found.Count, "count");
        Assert.IsNull(otherProgram, "another program has no such template");
        Assert.IsNull(otherSource, "another source has no such template");
        Assert.IsNull(otherText, "another text has no such template");
    }

    /// <summary>
    /// Hourly counts are returned in order for the hours that start between the hour of from and to; hours without lines are left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateHoursReturnsHourlyCountsInOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "tick event", 3, 4).Concat(Lines("syslog", "sshd", "tick event", 0, 1)).Concat(Lines("syslog", "sshd", "tick event", 1, 2)).ToArray());

        var template = await store.FindTemplateAsync("syslog", "sshd", "tick event", TestContext.CancellationToken);

        // Act
        var midHour = await store.TemplateHoursAsync(HoursQuery(template!.Id, Hour(0).AddMinutes(30), Hour(3), 100), TestContext.CancellationToken);
        var all = await store.TemplateHoursAsync(HoursQuery(template.Id, Hour(0), Hour(3).AddMinutes(1), 100), TestContext.CancellationToken);
        var limited = await store.TemplateHoursAsync(HoursQuery(template.Id, Hour(0), Hour(3).AddMinutes(1), 2), TestContext.CancellationToken);
        var untilStart = await store.TemplateHoursAsync(HoursQuery(template.Id, Hour(0), Hour(1), 100), TestContext.CancellationToken);
        var unknown = await store.TemplateHoursAsync(HoursQuery(template.Id + 100, Hour(0), Hour(3), 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["0|1", "1|2"], midHour.Select(item => $"{(item.Hour - Hour(0)).TotalHours}|{item.Count}"), "from in the middle of an hour includes that hour, to is exclusive");
        Assert.AreSequenceEqual(["0|1", "1|2", "3|4"], all.Select(item => $"{(item.Hour - Hour(0)).TotalHours}|{item.Count}"), "hours without lines are not returned");
        Assert.AreSequenceEqual(["0|1", "1|2"], limited.Select(item => $"{(item.Hour - Hour(0)).TotalHours}|{item.Count}"), "the limit keeps the first hours");
        Assert.AreSequenceEqual(["0|1"], untilStart.Select(item => $"{(item.Hour - Hour(0)).TotalHours}|{item.Count}"), "an hour that starts at to is not included");
        Assert.IsEmpty(unknown, "an unknown template has no hours");
    }

    /// <summary>
    /// New templates are those first seen in the range whose source has an earlier template, ordered by first seen and then by identity.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreNewTemplatesReturnsTemplatesFirstSeenInTheRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(-2), "old message"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1).AddMinutes(20), "old message"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2).AddMinutes(30), "second hour"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "tied one"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "tied two"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "at start"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(3), "at end"));
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(5), "later"));

        // Act
        var templates = await store.NewTemplatesAsync(NewQuery(string.Empty, Hour(0), Hour(3), 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["at start", "tied one", "tied two", "second hour"], templates.Select(template => template.Template), "templates first seen in the range, from inclusive, to exclusive");
        Assert.AreSequenceEqual([Hour(0), Hour(1), Hour(1), Hour(2).AddMinutes(30)], templates.Select(template => template.FirstSeen), "first seen");
        Assert.IsTrue(templates.All(template => template.Source == "syslog" && template.Program == "sshd" && template.Count == 1), "source, program and count of each");
    }

    /// <summary>
    /// A source without earlier lines makes nothing new, even at the start of the range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreNewTemplatesSkipsSourcesWithoutEarlierLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(-1), "known"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "fresh syslog"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(1), "first import"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(2), "second import"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "nginx", "nginx", Hour(0), "begins at from"));

        // Act
        var templates = await store.NewTemplatesAsync(NewQuery(string.Empty, Hour(0), Hour(3), 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["fresh syslog"], templates.Select(template => template.Template), "only the source with an earlier template has a new one");
    }

    /// <summary>
    /// New templates can be limited to one source and by a limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreNewTemplatesFiltersBySourceAndLimits()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(-1), "s old"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(-1), "j old"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "s one"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(1).AddMinutes(10), "j one"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2), "s two"));

        // Act
        var everySource = await store.NewTemplatesAsync(NewQuery(string.Empty, Hour(0), Hour(3), 100), TestContext.CancellationToken);
        var journal = await store.NewTemplatesAsync(NewQuery("journal", Hour(0), Hour(3), 100), TestContext.CancellationToken);
        var limited = await store.NewTemplatesAsync(NewQuery(string.Empty, Hour(0), Hour(3), 2), TestContext.CancellationToken);
        var unknown = await store.NewTemplatesAsync(NewQuery("nginx", Hour(0), Hour(3), 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["s one", "j one", "s two"], everySource.Select(template => template.Template), "every source ordered by first seen");
        Assert.AreSequenceEqual(["j one"], journal.Select(template => template.Template), "one source");
        Assert.AreSequenceEqual(["s one", "j one"], limited.Select(template => template.Template), "the limit keeps the first");
        Assert.IsEmpty(unknown, "an unknown source");
    }

    /// <summary>
    /// A template is reported for an hour when its count is more than the factor times its usual level over the covered hours; the usual level and the covered hours are exact.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesReportsHoursAboveTheUsualLevel()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "alpha event", 0, 24, 2).Concat(Lines("syslog", "sshd", "beta event", 0, 24, 2)).Concat(Lines("syslog", "sshd", "alpha event", 24, 6)).Concat(Lines("syslog", "sshd", "beta event", 24, 7)).ToArray());

        // Act
        var spikes = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 24, 3, 1, 100), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, spikes, "6 is not above 3 times 2, 7 is");
        Assert.AreEqual("beta event", spikes[0].Template.Template, "template");
        Assert.AreEqual(55L, spikes[0].Template.Count, "the template's total count");
        Assert.AreEqual(Hour(24), spikes[0].Hour, "hour");
        Assert.AreEqual(7L, spikes[0].Count, "count of the hour");
        Assert.AreEqual(2.0, spikes[0].UsualLevel, "usual level: 48 lines over 24 covered hours");
        Assert.AreEqual(24, spikes[0].CoveredHours, "covered hours");
    }

    /// <summary>
    /// Hours in which the source has no line are unknown and do not count as zero.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesLeavesOutHoursWithoutLinesOfTheSource()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "tango event", 12, 12, 2).Concat(Lines("syslog", "sshd", "uniform event", 12, 12, 2)).Concat(Lines("syslog", "sshd", "tango event", 24, 4)).Concat(Lines("syslog", "sshd", "uniform event", 24, 7)).ToArray());

        // Act
        var spikes = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 12, 3, 1, 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["uniform event|7|2|12"], spikes.Select(spike => $"{spike.Template.Template}|{spike.Count}|{spike.UsualLevel.ToString(CultureInfo.InvariantCulture)}|{spike.CoveredHours}"), "4 would be a spike if the 12 uncovered hours counted as zero; it is not one against the usual level 2");
    }

    /// <summary>
    /// Nothing is reported when the source has fewer covered hours than the minimum.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesNeedsTheMinimumBaselineHours()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "tango event", 12, 12, 2).Concat(Lines("syslog", "sshd", "tango event", 24, 50)).ToArray());

        // Act
        var tooFew = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 13, 3, 1, 100), TestContext.CancellationToken);
        var enough = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 12, 3, 1, 100), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(tooFew, "12 covered hours are fewer than 13");
        Assert.AreSequenceEqual(["tango event|50|2|12"], enough.Select(spike => $"{spike.Template.Template}|{spike.Count}|{spike.UsualLevel.ToString(CultureInfo.InvariantCulture)}|{spike.CoveredHours}"), "12 covered hours are enough");
    }

    /// <summary>
    /// An hour is reported only when its count reaches the minimum count.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesNeedsTheMinimumCount()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "tango event", 0, 24, 1).Concat(Lines("syslog", "sshd", "tango event", 24, 9)).ToArray());

        // Act
        var belowMinimum = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 24, 3, 10, 100), TestContext.CancellationToken);
        var atMinimum = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 24, 3, 9, 100), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(belowMinimum, "9 lines are below the minimum count 10");
        Assert.AreSequenceEqual(["tango event|9|1|24"], atMinimum.Select(spike => $"{spike.Template.Template}|{spike.Count}|{spike.UsualLevel.ToString(CultureInfo.InvariantCulture)}|{spike.CoveredHours}"), "9 lines reach the minimum count 9");
    }

    /// <summary>
    /// A template absent from every covered hour has the usual level 0 and is reported once it reaches the minimum count.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesReportsATemplateAbsentFromTheBaseline()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "omega event", 0, 24, 1).Concat(Lines("syslog", "sshd", "november event", 24, 10)).ToArray());

        // Act
        var spikes = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 24, 3, 10, 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["november event|10|0|24"], spikes.Select(spike => $"{spike.Template.Template}|{spike.Count}|{spike.UsualLevel.ToString(CultureInfo.InvariantCulture)}|{spike.CoveredHours}"), "the new template has the usual level 0");
    }

    /// <summary>
    /// Results are ordered by hour, then by count (highest first), then by template identity, and the limit applies afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesOrdersAndLimitsResults()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "base event", 0, 24, 1).Concat(Lines("syslog", "sshd", "papa event", 24, 5)).Concat(Lines("syslog", "sshd", "papa event", 25, 9)).ToArray());
        await WriteAsync(store, Lines("syslog", "sshd", "quebec event", 24, 8).Concat(Lines("syslog", "sshd", "quebec event", 25, 6)).ToArray());
        await WriteAsync(store, Lines("syslog", "sshd", "romeo event", 24, 5));

        // Act
        var all = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(26), 24, 24, 3, 1, 100), TestContext.CancellationToken);
        var limited = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(26), 24, 24, 3, 1, 2), TestContext.CancellationToken);

        // Assert
        string[] expected = ["quebec event|24|8", "papa event|24|5", "romeo event|24|5", "papa event|25|9", "quebec event|25|6"];

        Assert.AreSequenceEqual(expected, all.Select(spike => $"{spike.Template.Template}|{(spike.Hour - Hour(0)).TotalHours}|{spike.Count}"), "ordered by hour, count and identity");
        Assert.AreSequenceEqual(expected.Take(2), limited.Select(spike => $"{spike.Template.Template}|{(spike.Hour - Hour(0)).TotalHours}|{spike.Count}"), "the limit keeps the first results");
    }

    /// <summary>
    /// A baseline that reaches before the start of the storable range is clipped: no exception, and the covered hours are those of the clipped window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesClipsTheBaselineAtTheStorableRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var secondHour = new DateTimeOffset(1677, 9, 21, 1, 0, 0, TimeSpan.Zero);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", StorableTime.Min.AddMinutes(1), "clip alpha"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", StorableTime.Min.AddMinutes(2), "clip alpha"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", secondHour, "clip beta"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", secondHour.AddSeconds(1), "clip beta"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", secondHour.AddSeconds(2), "clip beta"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", secondHour.AddSeconds(3), "clip beta"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", secondHour.AddSeconds(4), "clip beta"));

        // Act
        var spikes = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, StorableTime.Min, secondHour.AddHours(2), 168, 1, 1, 1, 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["clip beta|5|0|1"], spikes.Select(spike => $"{spike.Template.Template}|{spike.Count}|{spike.UsualLevel.ToString(CultureInfo.InvariantCulture)}|{spike.CoveredHours}"), "the first hour has no covered hour before it; the second has the first");
        Assert.AreEqual(secondHour, spikes[0].Hour, "hour");
    }

    /// <summary>
    /// The covered hours of each result are those of its own source and hour, also when templates of several sources share an hour.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreTemplateSpikesSharesCoveredHoursPerSourceAndHour()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Lines("syslog", "sshd", "filler one", 14, 10, 1).Concat(Lines("journal", "sshd", "filler two", 4, 20, 1))
                                                                         .Concat(Lines("syslog", "sshd", "first syslog", 24, 5))
                                                                         .Concat(Lines("syslog", "sshd", "second syslog", 24, 6))
                                                                         .Concat(Lines("journal", "sshd", "third journal", 24, 7))
                                                                         .ToArray());

        // Act
        var spikes = await store.TemplateSpikesAsync(SpikeQuery(string.Empty, Hour(24), Hour(25), 24, 1, 3, 1, 100), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["third journal|journal|7|20", "second syslog|syslog|6|10", "first syslog|syslog|5|10"], spikes.Select(spike => $"{spike.Template.Template}|{spike.Template.Source}|{spike.Count}|{spike.CoveredHours}"), "each result carries the covered hours of its own source");
    }

    /// <summary>
    /// A find message of exactly 16,384 UTF-8 bytes is accepted.
    /// </summary>
    /// <param name="message">The text that is repeated to make the message</param>
    /// <param name="repeat">How often the text is repeated</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("a", 16384)]
    [DataRow("\u00E9", 8192)]
    public async Task SqliteStoreFindTemplateAcceptsAMessageAtTheByteLimit(string message, int repeat)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var longest = string.Concat(Enumerable.Repeat(message, repeat));

        // Act
        var found = await store.FindTemplateAsync("syslog", "sshd", longest, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(longest), "the message is 16,384 bytes");
        Assert.IsNull(found, "nothing is stored, and nothing is refused");
    }

    /// <summary>
    /// A query that breaks a rule is refused with the text of the rule, and a find message is refused above 16,384 UTF-8 bytes.
    /// </summary>
    /// <param name="query">The query kind</param>
    /// <param name="change">What is wrong with the query</param>
    /// <param name="rule">The expected text after the prefix</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("hours", "from missing", "from and to are required")]
    [DataRow("hours", "to missing", "from and to are required")]
    [DataRow("hours", "from before range", "from and to must be within the storable range")]
    [DataRow("hours", "to after range", "from and to must be within the storable range")]
    [DataRow("hours", "from equals to", "from must be before to")]
    [DataRow("hours", "from after to", "from must be before to")]
    [DataRow("hours", "limit zero", "limit must be between 1 and 10000")]
    [DataRow("hours", "limit too large", "limit must be between 1 and 10000")]
    [DataRow("hours", "template zero", "template ID must be positive")]
    [DataRow("hours", "template negative", "template ID must be positive")]
    [DataRow("new", "from missing", "from and to are required")]
    [DataRow("new", "to missing", "from and to are required")]
    [DataRow("new", "from before range", "from and to must be within the storable range")]
    [DataRow("new", "to after range", "from and to must be within the storable range")]
    [DataRow("new", "from equals to", "from must be before to")]
    [DataRow("new", "from after to", "from must be before to")]
    [DataRow("new", "limit zero", "limit must be between 1 and 10000")]
    [DataRow("new", "limit too large", "limit must be between 1 and 10000")]
    [DataRow("spikes", "from missing", "from and to are required")]
    [DataRow("spikes", "to missing", "from and to are required")]
    [DataRow("spikes", "from before range", "from and to must be within the storable range")]
    [DataRow("spikes", "to after range", "from and to must be within the storable range")]
    [DataRow("spikes", "from equals to", "from must be before to")]
    [DataRow("spikes", "from after to", "from must be before to")]
    [DataRow("spikes", "limit zero", "limit must be between 1 and 10000")]
    [DataRow("spikes", "limit too large", "limit must be between 1 and 10000")]
    [DataRow("spikes", "range too long", "the range must not exceed 744 hours")]
    [DataRow("spikes", "baseline zero", "baseline hours must be between 1 and 2160")]
    [DataRow("spikes", "baseline too large", "baseline hours must be between 1 and 2160")]
    [DataRow("spikes", "minimum baseline zero", "minimum baseline hours must be between 1 and the baseline hours")]
    [DataRow("spikes", "minimum baseline above baseline", "minimum baseline hours must be between 1 and the baseline hours")]
    [DataRow("spikes", "factor below one", "factor must be between 1 and 1000")]
    [DataRow("spikes", "factor above 1000", "factor must be between 1 and 1000")]
    [DataRow("spikes", "factor not a number", "factor must be between 1 and 1000")]
    [DataRow("spikes", "minimum count zero", "minimum count must be at least 1")]
    [DataRow("spikes", "minimum count negative", "minimum count must be at least 1")]
    [DataRow("find", "16385 ASCII bytes", "message must be at most 16384 UTF-8 bytes")]
    [DataRow("find", "16386 bytes of two-byte characters", "message must be at most 16384 UTF-8 bytes")]
    public async Task SqliteStoreTemplateQueriesRefuseInvalidQueries(string query, string change, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => RunInvalidAsync(store, query, change), $"{query} query with {change}");

        // Assert
        Assert.AreEqual(Prefix + rule, exception.Message, $"message of {query} query with {change}");
    }

    /// <summary>
    /// Every stored log line is counted under its template: count, first seen, last seen, the template's hour and the source's hour.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchCountsTemplatesPerHour()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "connect from unknown[203.0.113.7]"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(30), "connect from unknown[198.51.100.2]"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(10), "disk full"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1).AddMinutes(30), "connect from unknown[192.0.2.9]"));

        // Assert
        Assert.AreSequenceEqual([$"syslog|sshd|connect from unknown[<ip>]|{Ns(Hour(0))}|{Ns(Hour(1).AddMinutes(30))}|3", $"syslog|sshd|disk full|{Ns(Hour(0).AddMinutes(10))}|{Ns(Hour(0).AddMinutes(10))}|1"], await RowsAsync(directory.Path, "SELECT source, program, template, first_seen, last_seen, count FROM log_templates ORDER BY template"), "template rows");
        Assert.AreSequenceEqual([$"connect from unknown[<ip>]|{Ns(Hour(0))}|2", $"connect from unknown[<ip>]|{Ns(Hour(1))}|1", $"disk full|{Ns(Hour(0))}|1"], await RowsAsync(directory.Path, "SELECT t.template, h.hour, h.count FROM log_template_hours h JOIN log_templates t ON t.id = h.template_id ORDER BY t.template, h.hour"), "template hour rows");
        Assert.AreSequenceEqual([$"syslog|{Ns(Hour(0))}|3", $"syslog|{Ns(Hour(1))}|1"], await RowsAsync(directory.Path, "SELECT source, hour, count FROM log_source_hours ORDER BY hour"), "source hour rows");
    }

    /// <summary>
    /// A batch that fails counts nothing: the three template tables stay as they were.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchCountsNothingWhenTheBatchFails()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "kept line"));

        var before = await TableRowsAsync(directory.Path);
        var failing = Samples.Batch(Samples.LogAt(RecordOrigin.Import, 0, "syslog", "sshd", Hour(1), "kept line"), Samples.LogAt(RecordOrigin.Import, 0, "syslog", "sshd", Hour(2), "lost line"));

        failing.Import = new ImportStep
                         {
                             FileId = 1,
                             Done = 0
                         };

        // Act
        await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.WriteBatchAsync(failing, TestContext.CancellationToken), "import step of an unknown file");

        var after = await TableRowsAsync(directory.Path);

        // Assert
        Assert.IsNotEmpty(before, "the first batch wrote template rows");
        Assert.AreSequenceEqual(before, after, "the template tables are unchanged");
    }

    /// <summary>
    /// The same text from two programs or two sources is two templates.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchKeysTemplatesBySourceAndProgram()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "session closed"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "cron", Hour(0), "session closed"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(0), "session closed"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(0), "session closed"));

        // Assert
        Assert.AreSequenceEqual(["journal|sshd|session closed|2", "syslog|cron|session closed|1", "syslog|sshd|session closed|1"], await RowsAsync(directory.Path, "SELECT source, program, template, count FROM log_templates ORDER BY source, program"), "three templates");
        Assert.AreSequenceEqual(["journal|2", "syslog|2"], await RowsAsync(directory.Path, "SELECT source, count FROM log_source_hours ORDER BY source"), "lines per source");
    }

    /// <summary>
    /// Applies the change of the shared range, limit and ID rules.
    /// </summary>
    /// <param name="change">What is wrong with the query</param>
    /// <param name="id">The template ID</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>The values with the change applied</returns>
    private static (long Id, DateTimeOffset From, DateTimeOffset To, int Limit) Break(string change, long id, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        return change switch
               {
                   "from missing" => (id, default, to, limit),
                   "to missing" => (id, from, default, limit),
                   "from before range" => (id, StorableTime.Min.AddTicks(-1), to, limit),
                   "to after range" => (id, from, StorableTime.Max.AddTicks(1), limit),
                   "from equals to" => (id, to, to, limit),
                   "from after to" => (id, to, from, limit),
                   "limit zero" => (id, from, to, 0),
                   "limit too large" => (id, from, to, 10001),
                   "template zero" => (0, from, to, limit),
                   "template negative" => (-1, from, to, limit),
                   _ => (id, from, to, limit)
               };
    }

    /// <summary>
    /// Applies the change of the rules that only the usual-level query has.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="change">What is wrong with the query</param>
    private static void BreakSpikes(TemplateSpikeQuery query, string change)
    {
        switch (change)
        {
            case "range too long":
                {
                    query.To = query.From.AddHours(745);
                }
                break;

            case "baseline zero":
                {
                    query.BaselineHours = 0;
                    query.MinBaselineHours = 1;
                }
                break;

            case "baseline too large":
                {
                    query.BaselineHours = 2161;
                }
                break;

            case "minimum baseline zero":
                {
                    query.MinBaselineHours = 0;
                }
                break;

            case "minimum baseline above baseline":
                {
                    query.BaselineHours = 24;
                    query.MinBaselineHours = 25;
                }
                break;

            case "factor below one":
                {
                    query.Factor = 0.5;
                }
                break;

            case "factor above 1000":
                {
                    query.Factor = 1000.5;
                }
                break;

            case "factor not a number":
                {
                    query.Factor = double.NaN;
                }
                break;

            case "minimum count zero":
                {
                    query.MinCount = 0;
                }
                break;

            case "minimum count negative":
                {
                    query.MinCount = -5;
                }
                break;
        }
    }

    /// <summary>
    /// Gets an hour relative to <see cref="Samples.Base"/>, which starts on a full hour.
    /// </summary>
    /// <param name="offset">The hours after the base</param>
    /// <returns>The start of the hour</returns>
    private static DateTimeOffset Hour(int offset)
    {
        return Samples.Base.AddHours(offset);
    }

    /// <summary>
    /// Converts an instant to Unix nanoseconds.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The nanoseconds</returns>
    private static long Ns(DateTimeOffset instant)
    {
        return StorageTime.ToNanoseconds(instant);
    }

    /// <summary>
    /// Creates log lines of one template in one hour, one second apart from the start of the hour.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="hour">The hour relative to <see cref="Samples.Base"/></param>
    /// <param name="count">The number of lines</param>
    /// <returns>The records</returns>
    private static DataRecord[] Lines(string source, string program, string message, int hour, int count)
    {
        return Enumerable.Range(0, count).Select(index => Samples.LogAt(RecordOrigin.Backend, 0, source, program, Hour(hour).AddSeconds(index), message)).ToArray();
    }

    /// <summary>
    /// Creates log lines of one template in a run of hours, the same number in each.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="firstHour">The first hour relative to <see cref="Samples.Base"/></param>
    /// <param name="hours">The number of hours</param>
    /// <param name="perHour">The number of lines in each hour</param>
    /// <returns>The records</returns>
    private static DataRecord[] Lines(string source, string program, string message, int firstHour, int hours, int perHour)
    {
        return Enumerable.Range(firstHour, hours).SelectMany(hour => Lines(source, program, message, hour, perHour)).ToArray();
    }

    /// <summary>
    /// Creates a query of the hourly counts of a template.
    /// </summary>
    /// <param name="templateId">The template ID</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>The query</returns>
    private static TemplateHourQuery HoursQuery(long templateId, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        return new TemplateHourQuery
               {
                   TemplateId = templateId,
                   From = from,
                   To = to,
                   Limit = limit
               };
    }

    /// <summary>
    /// Creates a query of the new templates.
    /// </summary>
    /// <param name="source">The source, or an empty text for every source</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>The query</returns>
    private static NewTemplateQuery NewQuery(string source, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        return new NewTemplateQuery
               {
                   Source = source,
                   From = from,
                   To = to,
                   Limit = limit
               };
    }

    /// <summary>
    /// Creates a query of the templates above their usual level.
    /// </summary>
    /// <param name="source">The source, or an empty text for every source</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="baselineHours">The baseline hours</param>
    /// <param name="minBaselineHours">The minimum baseline hours</param>
    /// <param name="factor">The factor</param>
    /// <param name="minCount">The minimum count</param>
    /// <param name="limit">The limit</param>
    /// <returns>The query</returns>
    private static TemplateSpikeQuery SpikeQuery(string source, DateTimeOffset from, DateTimeOffset to, int baselineHours, int minBaselineHours, double factor, long minCount, int limit)
    {
        return new TemplateSpikeQuery
               {
                   Source = source,
                   From = from,
                   To = to,
                   BaselineHours = baselineHours,
                   MinBaselineHours = minBaselineHours,
                   Factor = factor,
                   MinCount = minCount,
                   Limit = limit
               };
    }

    /// <summary>
    /// Runs the query of a kind with the change that breaks one rule.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="query">The query kind</param>
    /// <param name="change">What is wrong with the query</param>
    /// <returns>A task that completes when the query ran</returns>
    private Task RunInvalidAsync(SqliteStore store, string query, string change)
    {
        switch (query)
        {
            case "hours":
                {
                    var hours = HoursQuery(1, Hour(0), Hour(1), 10);

                    (hours.TemplateId, hours.From, hours.To, hours.Limit) = Break(change, hours.TemplateId, hours.From, hours.To, hours.Limit);

                    return store.TemplateHoursAsync(hours, TestContext.CancellationToken);
                }

            case "new":
                {
                    var created = NewQuery(string.Empty, Hour(0), Hour(1), 10);

                    (_, created.From, created.To, created.Limit) = Break(change, 1, created.From, created.To, created.Limit);

                    return store.NewTemplatesAsync(created, TestContext.CancellationToken);
                }

            case "spikes":
                {
                    var spikes = SpikeQuery(string.Empty, Hour(0), Hour(24), 168, 24, 3, 10, 10);

                    (_, spikes.From, spikes.To, spikes.Limit) = Break(change, 1, spikes.From, spikes.To, spikes.Limit);
                    BreakSpikes(spikes, change);

                    return store.TemplateSpikesAsync(spikes, TestContext.CancellationToken);
                }

            default:
                {
                    return store.FindTemplateAsync("syslog", "sshd", change.StartsWith("16385", StringComparison.Ordinal) ? new string('a', 16385) : new string('\u00E9', 8193), TestContext.CancellationToken);
                }
        }
    }

    /// <summary>
    /// Writes the records as one batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the batch is stored</returns>
    private async Task WriteAsync(SqliteStore store, params DataRecord[] records)
    {
        var result = await store.WriteBatchAsync(Samples.Batch(records), TestContext.CancellationToken);

        Assert.AreEqual(new WriteResult(records.Length, 0), result, "all records of the arranged batch are stored");
    }

    /// <summary>
    /// Reads every row of the three template tables.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<IReadOnlyList<string>> TableRowsAsync(string directory)
    {
        var rows = new List<string>();

        rows.AddRange(await RowsAsync(directory, "SELECT 'templates', id, source, program, template, first_seen, last_seen, count FROM log_templates ORDER BY id"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'template hours', template_id, hour, count FROM log_template_hours ORDER BY template_id, hour"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'source hours', source, hour, count FROM log_source_hours ORDER BY source, hour"));

        return rows;
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<IReadOnlyList<string>> RowsAsync(string directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(column => Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    #endregion // Methods
}