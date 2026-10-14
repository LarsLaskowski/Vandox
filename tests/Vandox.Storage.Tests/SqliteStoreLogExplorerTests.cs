using Microsoft.Extensions.Time.Testing;

using Vandox.Core.Model;
using Vandox.Core.Templates;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the log exploration of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreLogExplorerTests
{
    #region Constants

    private const string Prefix = "store: invalid query: ";

    private const int Scan = 1;

    private const int Index = 1_000_000;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _start = Samples.Base;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region AC-1 Filters

    /// <summary>
    /// A line is returned when it matches every filter: all words, the exact source, the exact program, the priority bound and the half-open time range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsAppliesEveryFilter()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var lines = new[]
                    {
                        Line("syslog", "sshd", Sec(10), "disk error on sda", 3),
                        Line("syslog", "sshd", Sec(20), "error: disk full", 6),
                        Line("syslog", "cron", Sec(30), "disk ok", 3),
                        Line("journal", "sshd", Sec(40), "disk error again", 2),
                        Line("syslog", "sshd", Sec(50), "network error", null),
                        Line("syslog", "SSHD", Sec(60), "disk error uppercase", 3),
                        Line("syslog", "sshd", Sec(70), "disk error late", 4),
                        Line("syslogd", "sshd", Sec(80), "disk error source prefix", 3),
                        Line("syslog", "sshd", _start.AddHours(5), "disk error out of window", 3)
                    };

        await WriteAsync(store, lines.Reverse().ToArray());

        var window = Filter(filter => filter.To = _start.AddHours(2));

        // Act
        var text = await store.FindLogsAsync(Change(window, filter => filter.Text = "disk error"), TestContext.CancellationToken);
        var source = await store.FindLogsAsync(Change(window, filter => (filter.Text, filter.Source) = ("disk error", "syslog")), TestContext.CancellationToken);
        var program = await store.FindLogsAsync(Change(window, filter => (filter.Text, filter.Source, filter.Program) = ("disk error", "syslog", "sshd")), TestContext.CancellationToken);
        var priority = await store.FindLogsAsync(Change(window, filter => (filter.Text, filter.Source, filter.Program, filter.MaxPriority) = ("disk error", "syslog", "sshd", (byte?)4)), TestContext.CancellationToken);
        var range = await store.FindLogsAsync(Change(window, filter => (filter.Text, filter.Source, filter.Program, filter.From, filter.To) = ("disk error", "syslog", "sshd", Sec(20), Sec(70))), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["disk error on sda", "error: disk full", "disk error again", "disk error uppercase", "disk error late", "disk error source prefix"], Messages(text), "all words of the text, in time order, inside the window");
        Assert.AreSequenceEqual(["disk error on sda", "error: disk full", "disk error uppercase", "disk error late"], Messages(source), "the source must be exact");
        Assert.AreSequenceEqual(["disk error on sda", "error: disk full", "disk error late"], Messages(program), "the program must be exact and ordinal");
        Assert.AreSequenceEqual(["disk error on sda", "disk error late"], Messages(priority), "the priority must be at most 4");
        Assert.AreSequenceEqual(["error: disk full"], Messages(range), "from is included and to is excluded");
    }

    /// <summary>
    /// With a severity bound, lines without a priority are not returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithSeverityExcludesLinesWithoutPriority()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Sec(1), "no priority", null),
                         Line("syslog", "sshd", Sec(2), "emergency", 0),
                         Line("syslog", "sshd", Sec(3), "debugging", 7));

        // Act
        var all = await store.FindLogsAsync(Change(Filter(), filter => filter.MaxPriority = 7), TestContext.CancellationToken);
        var emergency = await store.FindLogsAsync(Change(Filter(), filter => filter.MaxPriority = 0), TestContext.CancellationToken);
        var withText = await store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.MaxPriority) = ("priority", (byte?)7)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["emergency", "debugging"], Messages(all), "priority 0 to 7, without the line that has none");
        Assert.AreSequenceEqual(["emergency"], Messages(emergency), "priority 0 only");
        Assert.IsEmpty(withText.Lines, "the only line with the word has no priority");
    }

    /// <summary>
    /// Without a text, only the other filters apply, and other kinds of records are not returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithoutTextReturnsFilteredLinesInTimeOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Sec(50), "fifth", null),
                         Line("journal", "sshd", Sec(15), "other source", null),
                         Line("syslog", "sshd", Sec(20), "second", null),
                         Line("syslog", "cron", Sec(40), "fourth", null),
                         Samples.Metric(RecordOrigin.Backend, 0, 25, "cpu"),
                         Line("syslog", "sshd", Sec(10), "first", null),
                         Line("syslog", "sshd", Sec(30), "third", null));

        // Act
        var page = await store.FindLogsAsync(Change(Filter(), filter => filter.Source = "syslog"), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["first", "second", "third", "fourth", "fifth"], Messages(page), "the lines of the source in time order, without the metric");
        Assert.IsNull(page.Next, "the range is exhausted");
    }

    #endregion // AC-1 Filters

    #region AC-2 Pages

    /// <summary>
    /// Pages continue without a gap or a repeat, also across lines that share a capture time.
    /// </summary>
    /// <param name="text">The search text, or an empty text for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("")]
    [DataRow("alpha")]
    public async Task FindLogsPagesThroughLinesSharingACaptureTime(string text)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var times = new[] { 0, 0, 0, 1, 1, 2, 2 };

        await WriteAsync(store, times.Select((second, index) => Line("syslog", "sshd", Sec(second), $"alpha {index}", null)).ToArray());

        // Act
        var pages = await PagesAsync(store, Change(Filter(), filter => (filter.Text, filter.Limit) = (text, 2)), null);

        // Assert
        var messages = pages.SelectMany(Messages).ToArray();

        Assert.AreSequenceEqual(["alpha 0", "alpha 1", "alpha 2", "alpha 3", "alpha 4", "alpha 5", "alpha 6"], messages, "every line once, in order");
        Assert.AreSequenceEqual([2, 2, 2, 1], pages.Select(page => page.Lines.Count).ToArray(), "pages of two lines");

        foreach (var page in pages.Take(3))
        {
            var last = page.Lines[^1];

            Assert.IsNotNull(page.Next, "a full page has a position");
            Assert.AreEqual(last.Record.CapturedAt, page.Next.CapturedAt, "the position has the capture time of the last line");
            Assert.AreEqual(last.Id, page.Next.Id, "the position has the ID of the last line");
            Assert.IsFalse(page.ScanLimitReached, "the scan bound was not reached");
        }

        Assert.IsNull(pages[^1].Next, "the last page has no position");
    }

    /// <summary>
    /// A page that ends with the range has no position, and a full page that ends with the range is followed by an empty one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsReturnsNoNextAtTheEnd()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "one", null), Line("syslog", "sshd", Sec(2), "two", null), Line("syslog", "sshd", Sec(3), "three", null));

        // Act
        var wide = await store.FindLogsAsync(Change(Filter(), filter => filter.Limit = 10), TestContext.CancellationToken);
        var exact = await store.FindLogsAsync(Change(Filter(), filter => filter.Limit = 3), TestContext.CancellationToken);
        var after = await store.FindLogsAsync(Change(Filter(), filter => (filter.Limit, filter.After) = (3, exact.Next)), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(3, wide.Lines, "all lines");
        Assert.IsNull(wide.Next, "a page that is not full has no position");
        Assert.IsFalse(wide.ScanLimitReached, "no bound reached");
        Assert.HasCount(3, exact.Lines, "a full page");
        Assert.IsNotNull(exact.Next, "a full page has a position");
        Assert.IsEmpty(after.Lines, "nothing after the last line");
        Assert.IsNull(after.Next, "the range is exhausted");
    }

    #endregion // AC-2 Pages

    #region AC-3 Strategies

    /// <summary>
    /// The pages do not depend on whether a text search scans or runs from the index.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="source">The source, or an empty text</param>
    /// <param name="program">The program, or an empty text</param>
    /// <param name="priority">The highest priority, or -1 for none</param>
    /// <param name="limit">The page size</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("common", "", "", -1, 7)]
    [DataRow("rare", "", "", -1, 7)]
    [DataRow("alpha beta", "", "", -1, 5)]
    [DataRow("common", "syslog", "sshd", -1, 9)]
    [DataRow("common", "", "", 3, 9)]
    [DataRow("alpha", "journal", "cron", 4, 6)]
    [DataRow("rare common", "syslog", "", -1, 7)]
    public async Task FindLogsReturnsTheSameLinesWithEitherStrategy(string text, string source, string program, int priority, int limit)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(0, 240).Select(Common).ToArray());

        var filter = Change(Filter(), change => (change.Text, change.Source, change.Program, change.MaxPriority, change.Limit) = (text, source, program, priority < 0 ? null : (byte?)priority, limit));

        // Act
        var scanned = await PagesAsync(store, filter, Scan);
        var indexed = await PagesAsync(store, filter, Index);

        // Assert
        Assert.IsNotEmpty(scanned.SelectMany(page => page.Lines).ToArray(), "the filter set finds lines, so the comparison is not empty");
        Assert.AreSequenceEqual(Signatures(scanned), Signatures(indexed), "the same lines, positions and flags page by page");
    }

    #endregion // AC-3 Strategies

    #region AC-4 Scan bound

    /// <summary>
    /// A scan that ends at the bound returns the position of the last examined line, also with an empty page, and continuing returns every match once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsStopsAtTheScanLimitAndContinues()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var needles = new[] { 5, 37, 38, 90 };

        await WriteAsync(store, Enumerable.Range(0, 95).Select(index => Line("syslog", "sshd", Sec(index), needles.Contains(index) ? $"needle {index}" : $"hay {index}", null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var filter = Change(Filter(), change => (change.Text, change.Limit, change.ScanLimit) = ("needle", 100, 10));

        // Act
        var pages = await PagesAsync(store, filter, Scan);

        // Assert
        Assert.AreSequenceEqual(["needle 5", "needle 37", "needle 38", "needle 90"], pages.SelectMany(Messages).ToArray(), "every matching line exactly once");
        Assert.HasCount(10, pages, "ten calls of at most ten examined lines");

        for (var index = 0; index < 9; index++)
        {
            Assert.IsTrue(pages[index].ScanLimitReached, $"the call {index} ended at the scan bound");
            Assert.IsNotNull(pages[index].Next, $"the call {index} has a position");
            Assert.AreEqual(ids[(index * 10) + 9], pages[index].Next!.Id, $"the call {index} ends at the last examined line");
        }

        Assert.IsEmpty(pages[1].Lines, "a call can end at the bound with an empty page");
        Assert.IsFalse(pages[9].ScanLimitReached, "the last call examined fewer lines than the bound");
        Assert.IsNull(pages[9].Next, "the last call has no position");
    }

    /// <summary>
    /// Without a text, the position is the last examined line, not the last returned one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithoutTextStopsAtTheScanLimit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(0, 45).Select(index => Line("syslog", index % 2 == 0 ? "sshd" : "cron", Sec(index), $"line {index}", null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var filter = Change(Filter(), change => (change.Program, change.Limit, change.ScanLimit) = ("sshd", 100, 10));

        // Act
        var pages = await PagesAsync(store, filter, Index);

        // Assert
        Assert.HasCount(5, pages, "five calls");
        Assert.HasCount(5, pages[0].Lines, "five of the first ten lines have the program");
        Assert.IsTrue(pages[0].ScanLimitReached, "the first call ended at the bound");
        Assert.AreEqual(ids[9], pages[0].Next!.Id, "the position is the last examined line, not the last returned one");
        Assert.HasCount(23, pages.SelectMany(page => page.Lines).Select(line => line.Id).Distinct().ToArray(), "every line with the program exactly once");
        Assert.IsNull(pages[^1].Next, "the last call reaches the end");
    }

    #endregion // AC-4 Scan bound

    #region AC-5 Template lines

    /// <summary>
    /// For each template of the corpus, the lines are exactly those whose message normalizes to it, with either strategy and combined with another filter.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTemplateReturnsExactlyItsLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var corpus = Corpus();

        await WriteAsync(store, corpus.Select((entry, index) => Line("syslog", entry.Program, Sec(index), entry.Message, null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var groups = corpus.Select((entry, index) => (entry.Program, entry.Message, Id: ids[index], Time: Sec(index)))
                           .GroupBy(entry => (entry.Program, Text: LogTemplateNormalizer.Normalize(entry.Message)))
                           .ToArray();
        var problems = new List<string>();

        // Act
        foreach (var group in groups)
        {
            var template = await store.FindTemplateAsync("syslog", group.Key.Program, group.First().Message, TestContext.CancellationToken);

            Assert.IsNotNull(template, $"the template of {group.Key.Text} exists");

            var expected = group.Select(entry => entry.Id).Order().ToArray();

            foreach (var strategy in new[] { Scan, Index })
            {
                var found = await AllLinesAsync(store, Change(Filter(), filter => (filter.TemplateId, filter.Limit) = (template.Id, 1000)), strategy);

                if (found.Select(line => line.Id).Order().SequenceEqual(expected))
                {
                    continue;
                }

                problems.Add($"{group.Key.Program}|{group.Key.Text} with selectiveMatches {strategy}: found {found.Count} lines, expected {expected.Length}");
            }
        }

        var largest = groups.OrderByDescending(group => group.Count()).First();
        var largestTemplate = await store.FindTemplateAsync("syslog", largest.Key.Program, largest.First().Message, TestContext.CancellationToken);
        var middle = largest.OrderBy(entry => entry.Time).ElementAt(largest.Count() / 2).Time;
        var narrowed = await AllLinesAsync(store, Change(Filter(), filter => (filter.TemplateId, filter.From, filter.Limit) = (largestTemplate!.Id, middle, 1000)), Index);
        var withPriority = await AllLinesAsync(store, Change(Filter(), filter => (filter.TemplateId, filter.MaxPriority, filter.Limit) = (largestTemplate!.Id, (byte?)7, 1000)), Scan);

        // Assert
        Assert.IsGreaterThan(30, groups.Length, "the corpus holds many templates");
        Assert.IsEmpty(problems, $"templates whose lines differ: {string.Join("; ", problems)}");
        Assert.AreSequenceEqual(largest.Where(entry => entry.Time >= middle).Select(entry => entry.Id).Order().ToArray(), narrowed.Select(line => line.Id).Order().ToArray(), "the template and the time range are combined with AND");
        Assert.IsEmpty(withPriority, "the template and a severity bound are combined with AND: the lines have no priority");
    }

    /// <summary>
    /// A template without a literal word is found by scanning and checking every line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTemplateWithoutWordsScans()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "num", Sec(1), "12345 67890", null),
                         Line("syslog", "num", Sec(2), "alpha 12345 67890", null),
                         Line("syslog", "num", Sec(3), "99 88", null),
                         Line("syslog", "num", Sec(4), "text line", null),
                         Line("syslog", "num", Sec(5), "1 2", null),
                         Line("syslog", "other", Sec(6), "7 8", null));

        var template = await store.FindTemplateAsync("syslog", "num", "12345 67890", TestContext.CancellationToken);

        // Act
        var scanned = await AllLinesAsync(store, Change(Filter(), filter => filter.TemplateId = template!.Id), Scan);
        var indexed = await AllLinesAsync(store, Change(Filter(), filter => filter.TemplateId = template!.Id), Index);

        // Assert
        Assert.AreEqual("<num> <num>", template!.Template, "the template has no literal word");
        Assert.AreSequenceEqual(["12345 67890", "99 88", "1 2"], scanned.Select(line => Message(line)).ToArray(), "the lines of the template, by scanning");
        Assert.AreSequenceEqual(["12345 67890", "99 88", "1 2"], indexed.Select(line => Message(line)).ToArray(), "the same with the other strategy");
    }

    /// <summary>
    /// The lines of a template that was cut at 1,024 bytes are found, and a line that only shares its words is not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithACutTemplateFindsItsLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var first = LongMessage();
        var second = first[..3000] + " tail words that differ";

        await WriteAsync(store,
                         Line("syslog", "long", Sec(1), first, null),
                         Line("syslog", "long", Sec(2), "lorem ipsum dolor", null),
                         Line("syslog", "long", Sec(3), second, null),
                         Line("syslog", "long", Sec(4), "lorem ipsum dolor sit amet", null),
                         Line("syslog", "long", Sec(5), "other prefix " + first[..2000], null));

        var template = await store.FindTemplateAsync("syslog", "long", first, TestContext.CancellationToken);
        var ids = await IdsAsync(directory.Path);

        // Act
        var scanned = await AllLinesAsync(store, Change(Filter(), filter => filter.TemplateId = template!.Id), Scan);
        var indexed = await AllLinesAsync(store, Change(Filter(), filter => filter.TemplateId = template!.Id), Index);

        // Assert
        Assert.AreEqual(16384, System.Text.Encoding.UTF8.GetByteCount(first), "the message has 16384 bytes");
        Assert.AreEqual(LogTemplateNormalizer.MaxTemplateBytes, System.Text.Encoding.UTF8.GetByteCount(template!.Template), "the template was cut at 1024 bytes");
        Assert.AreSequenceEqual([ids[0], ids[2]], scanned.Select(line => line.Id).Order().ToArray(), "both long lines, by scanning");
        Assert.AreSequenceEqual([ids[0], ids[2]], indexed.Select(line => line.Id).Order().ToArray(), "both long lines from the index");
    }

    /// <summary>
    /// An unknown template number is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithUnknownTemplateIsRefused()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "one", null));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.FindLogsAsync(Change(Filter(), filter => filter.TemplateId = 9999), TestContext.CancellationToken));

        // Assert
        Assert.AreEqual($"{Prefix}unknown template", exception.Message, "the message");
    }

    #endregion // AC-5 Template lines

    #region AC-7 Filter rules

    /// <summary>
    /// Each broken rule is refused with its exact message.
    /// </summary>
    /// <param name="change">The name of the change to a valid filter</param>
    /// <param name="message">The expected message after the prefix</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("from missing", "from and to are required")]
    [DataRow("to missing", "from and to are required")]
    [DataRow("from outside", "from and to must be within the storable range")]
    [DataRow("to outside", "from and to must be within the storable range")]
    [DataRow("from equals to", "from must be before to")]
    [DataRow("from after to", "from must be before to")]
    [DataRow("limit zero", "limit must be between 1 and 1000")]
    [DataRow("limit 1001", "limit must be between 1 and 1000")]
    [DataRow("scan limit zero", "scan limit must be between 1 and 100000")]
    [DataRow("scan limit 100001", "scan limit must be between 1 and 100000")]
    [DataRow("source too long", "source is longer than 1024 bytes")]
    [DataRow("program too long", "program is longer than 1024 bytes")]
    [DataRow("priority 8", "priority must be between 0 and 7")]
    [DataRow("template negative", "template ID must not be negative")]
    [DataRow("after outside", "after must be within the storable range")]
    [DataRow("after id zero", "after id must be positive")]
    [DataRow("text control character", "search text contains a control or format character")]
    [DataRow("text without letter", "every search term needs a letter or a digit")]
    [DataRow("text too many terms", "search text has more than 16 terms")]
    [DataRow("text too long", "search text is longer than 1024 bytes")]
    [DataRow("text lone surrogate", "search text is not valid UTF-8")]
    public async Task FindLogsRefusesInvalidFilters(string change, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var filter = Break(change);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.FindLogsAsync(filter, TestContext.CancellationToken));

        // Assert
        Assert.AreEqual($"{Prefix}{message}", exception.Message, $"the message of '{change}'");
    }

    /// <summary>
    /// A null text, source or program is an argument error.
    /// </summary>
    /// <param name="change">The name of the change to a valid filter</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("text null")]
    [DataRow("source null")]
    [DataRow("program null")]
    public async Task FindLogsRefusesNullStrings(string change)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var filter = Break(change);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => store.FindLogsAsync(filter, TestContext.CancellationToken));

        // Assert
        Assert.IsNotNull(exception, $"'{change}' is refused");
    }

    #endregion // AC-7 Filter rules

    #region AC-8 Context

    /// <summary>
    /// The context holds the neighbours of the same source, log and host, whatever their other fields, in order, including lines that share the capture time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogContextReturnsNeighboursOfTheSameLog()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Entry("syslog", "auth", "web-1", "sshd", Sec(0), "a0", 3),
                         Entry("syslog", "mail", "web-1", "sshd", Sec(1), "m1", 3),
                         Entry("syslog", "auth", "web-1", "sshd", Sec(2), "a2", 3),
                         Entry("syslog", "auth", "web-2", "sshd", Sec(3), "h3", 3),
                         Entry("syslog", "auth", "web-1", "cron", Sec(4), "a4", 7),
                         Entry("syslog", "auth", "web-1", "sshd", Sec(4), "target", 3),
                         Entry("syslog", "auth", "web-1", "other", Sec(4), "a6", null),
                         Entry("journal", "auth", "web-1", "sshd", Sec(5), "j7", 3),
                         Entry("syslog", "auth", "web-1", "sshd", Sec(6), "a8", 3),
                         Entry("syslog", "auth", "web-1", "sshd", Sec(7), "a9", 3),
                         Entry("syslog", "auth", "web-1", "sshd", Sec(8), "a10", 3));

        var ids = await IdsAsync(directory.Path);
        var query = new LogContextQuery
                    {
                        RecordId = ids[5],
                        Before = 2,
                        After = 2
                    };

        // Act
        var context = await store.LogContextAsync(query, TestContext.CancellationToken);
        var wide = await store.LogContextAsync(Change(query, change => (change.Before, change.After) = (10, 10)), TestContext.CancellationToken);
        var none = await store.LogContextAsync(Change(query, change => (change.Before, change.After) = (0, 0)), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("target", Message(context.Line), "the line itself");
        Assert.AreSequenceEqual(["a2", "a4"], context.Before.Select(line => Message(line)).ToArray(), "two lines before, in order, with the line of equal time and lower ID");
        Assert.AreSequenceEqual(["a6", "a8"], context.After.Select(line => Message(line)).ToArray(), "two lines after, in order, with the line of equal time and higher ID");
        Assert.AreSequenceEqual(["a0", "a2", "a4"], wide.Before.Select(line => Message(line)).ToArray(), "fewer lines when the data ends");
        Assert.AreSequenceEqual(["a6", "a8", "a9", "a10"], wide.After.Select(line => Message(line)).ToArray(), "fewer lines after when the data ends");
        Assert.IsEmpty(none.Before, "no line before");
        Assert.IsEmpty(none.After, "no line after");
    }

    /// <summary>
    /// Each side examines at most 2,000 lines of the source.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogContextStopsAtTheExamineBound()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var records = new List<DataRecord>();

        AddSurrounded(records, "near", 1999, 0);
        AddSurrounded(records, "far", 2000, 10000);

        await WriteAsync(store, records.ToArray());

        var near = await store.LogContextAsync(new LogContextQuery
                                               {
                                                   RecordId = await IdOfAsync(directory.Path, "near", "target"),
                                                   Before = 1,
                                                   After = 1
                                               },
                                               TestContext.CancellationToken);
        var far = await store.LogContextAsync(new LogContextQuery
                                              {
                                                  RecordId = await IdOfAsync(directory.Path, "far", "target"),
                                                  Before = 1,
                                                  After = 1
                                              },
                                              TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["match before"], near.Before.Select(line => Message(line)).ToArray(), "the 2000th examined line is found before");
        Assert.AreSequenceEqual(["match after"], near.After.Select(line => Message(line)).ToArray(), "the 2000th examined line is found after");
        Assert.IsEmpty(far.Before, "the 2001st examined line is not examined before");
        Assert.IsEmpty(far.After, "the 2001st examined line is not examined after");
    }

    /// <summary>
    /// A broken context query is refused with its exact message.
    /// </summary>
    /// <param name="change">The name of the change to a valid query</param>
    /// <param name="message">The expected message after the prefix</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("record zero", "record ID must be positive")]
    [DataRow("record negative", "record ID must be positive")]
    [DataRow("before negative", "context lines must be between 0 and 20")]
    [DataRow("before 21", "context lines must be between 0 and 20")]
    [DataRow("after negative", "context lines must be between 0 and 20")]
    [DataRow("after 21", "context lines must be between 0 and 20")]
    [DataRow("unknown line", "unknown log line")]
    [DataRow("not a log line", "unknown log line")]
    public async Task LogContextRefusesInvalidQueries(string change, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "one", null), Samples.Metric(RecordOrigin.Backend, 0, 2, "cpu"));

        var ids = await RowsAsync(directory.Path, "SELECT id FROM records ORDER BY id");
        var query = new LogContextQuery
                    {
                        RecordId = long.Parse(ids[0]),
                        Before = 1,
                        After = 1
                    };

        switch (change)
        {
            case "record zero":
                {
                    query.RecordId = 0;
                }
                break;

            case "record negative":
                {
                    query.RecordId = -4;
                }
                break;

            case "before negative":
                {
                    query.Before = -1;
                }
                break;

            case "before 21":
                {
                    query.Before = 21;
                }
                break;

            case "after negative":
                {
                    query.After = -1;
                }
                break;

            case "after 21":
                {
                    query.After = 21;
                }
                break;

            case "unknown line":
                {
                    query.RecordId = 9999;
                }
                break;

            case "not a log line":
                {
                    query.RecordId = long.Parse(ids[1]);
                }
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.LogContextAsync(query, TestContext.CancellationToken));

        // Assert
        Assert.AreEqual($"{Prefix}{message}", exception.Message, $"the message of '{change}'");
    }

    #endregion // AC-8 Context

    #region AC-9 Sources

    /// <summary>
    /// The sources of the stored log lines are listed once each in ordinal order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogSourcesReturnsDistinctSourcesInOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Sec(1), "one", null),
                         Line("Zeta", "sshd", Sec(2), "two", null),
                         Line("alpha", "sshd", Sec(3), "three", null),
                         Line("syslog", "sshd", Sec(4), "four", null),
                         Line("Beta", "sshd", Sec(5), "five", null),
                         Samples.Metric(RecordOrigin.Backend, 0, 6, "cpu"));

        // Act
        var sources = await store.LogSourcesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["Beta", "Zeta", "alpha", "syslog"], sources.ToArray(), "distinct log sources in ordinal order, without the source of the metric");
    }

    /// <summary>
    /// At most sixty-four sources are listed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogSourcesReturnsAtMostSixtyFour()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(0, 70).Select(index => Line($"src-{index:00}", "sshd", Sec(index), "line", null)).ToArray());

        // Act
        var sources = await store.LogSourcesAsync(TestContext.CancellationToken);

        // Assert
        Assert.HasCount(64, sources, "at most 64 sources");
        Assert.AreEqual("src-00", sources[0], "the first source in ordinal order");
        Assert.AreEqual("src-63", sources[^1], "the 64th source in ordinal order");
    }

    #endregion // AC-9 Sources

    #region AC-10 Template list

    /// <summary>
    /// The templates are counted over the range and ordered by that count, then by ID, and limited.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCountsLinesInTheRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "alpha started", 1, 3));
        await WriteAsync(store, Lines("syslog", "sshd", "alpha started", 3, 2));
        await WriteAsync(store, Lines("syslog", "sshd", "beta started", 2, 5));
        await WriteAsync(store, Lines("syslog", "sshd", "gamma started", 20, 1));
        await WriteAsync(store, Lines("syslog", "sshd", "delta started", 1, 7));

        var query = TemplateQuery(Hour(0), Hour(5), 1000);

        // Act
        var all = await store.TemplatesAsync(query, TestContext.CancellationToken);
        var limited = await store.TemplatesAsync(Change(query, change => change.Limit = 2), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["delta started", "alpha started", "beta started"], all.Select(entry => entry.Template.Template).ToArray(), "highest count first, equal counts by template ID, nothing outside the range");
        Assert.AreSequenceEqual([7L, 5L, 5L], all.Select(entry => entry.CountInRange).ToArray(), "the counts in the range");
        Assert.AreEqual("syslog", all[0].Template.Source, "source of the template");
        Assert.AreEqual("sshd", all[0].Template.Program, "program of the template");
        Assert.AreEqual(7L, all[0].Template.Count, "count over all time");
        Assert.AreSequenceEqual(["delta started", "alpha started"], limited.Select(entry => entry.Template.Template).ToArray(), "limited to two");
    }

    /// <summary>
    /// A range inside an hour counts that whole hour.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCountsWholeHoursOfAMidHourRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", _start.AddMinutes(5), "alpha started", null),
                         Line("syslog", "sshd", _start.AddMinutes(50), "alpha started", null),
                         Line("syslog", "sshd", _start.AddHours(2).AddMinutes(10), "alpha started", null));

        // Act
        var templates = await store.TemplatesAsync(TemplateQuery(_start.AddMinutes(30), _start.AddHours(2).AddMinutes(5), 10), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, templates, "one template");
        Assert.AreEqual(3L, templates[0].CountInRange, "both lines of the first hour and the line of the hour that starts before the end");
    }

    /// <summary>
    /// The list can be limited to a source and a program.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesFiltersBySourceAndProgram()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Hour(1), "alpha started", null),
                         Line("syslog", "cron", Hour(1), "beta started", null),
                         Line("journal", "sshd", Hour(1), "gamma started", null));

        var query = TemplateQuery(Hour(0), Hour(5), 100);

        // Act
        var syslog = await store.TemplatesAsync(Change(query, change => change.Source = "syslog"), TestContext.CancellationToken);
        var syslogSshd = await store.TemplatesAsync(Change(query, change => (change.Source, change.Program) = ("syslog", "sshd")), TestContext.CancellationToken);
        var sshd = await store.TemplatesAsync(Change(query, change => change.Program = "sshd"), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["alpha started", "beta started"], syslog.Select(entry => entry.Template.Template).ToArray(), "the source");
        Assert.AreSequenceEqual(["alpha started"], syslogSshd.Select(entry => entry.Template.Template).ToArray(), "the source and the program");
        Assert.AreSequenceEqual(["alpha started", "gamma started"], sshd.Select(entry => entry.Template.Template).ToArray(), "the program of every source");
    }

    /// <summary>
    /// A broken template list query is refused with its exact message.
    /// </summary>
    /// <param name="change">The name of the change to a valid query</param>
    /// <param name="message">The expected message after the prefix</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("from missing", "from and to are required")]
    [DataRow("to missing", "from and to are required")]
    [DataRow("from outside", "from and to must be within the storable range")]
    [DataRow("to outside", "from and to must be within the storable range")]
    [DataRow("from equals to", "from must be before to")]
    [DataRow("from after to", "from must be before to")]
    [DataRow("limit zero", "limit must be between 1 and 1000")]
    [DataRow("limit 1001", "limit must be between 1 and 1000")]
    [DataRow("source too long", "source is longer than 1024 bytes")]
    [DataRow("program too long", "program is longer than 1024 bytes")]
    public async Task TemplatesRefusesInvalidQueries(string change, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var query = TemplateQuery(Hour(0), Hour(5), 10);

        switch (change)
        {
            case "from missing":
                {
                    query.From = default;
                }
                break;

            case "to missing":
                {
                    query.To = default;
                }
                break;

            case "from outside":
                {
                    query.From = StorableTime.Min.AddTicks(-1);
                }
                break;

            case "to outside":
                {
                    query.To = StorableTime.Max.AddTicks(1);
                }
                break;

            case "from equals to":
                {
                    query.From = query.To;
                }
                break;

            case "from after to":
                {
                    query.From = query.To.AddHours(1);
                }
                break;

            case "limit zero":
                {
                    query.Limit = 0;
                }
                break;

            case "limit 1001":
                {
                    query.Limit = 1001;
                }
                break;

            case "source too long":
                {
                    query.Source = new string('s', 1025);
                }
                break;

            case "program too long":
                {
                    query.Program = new string('p', 1025);
                }
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.TemplatesAsync(query, TestContext.CancellationToken));

        // Assert
        Assert.AreEqual($"{Prefix}{message}", exception.Message, $"the message of '{change}'");
    }

    /// <summary>
    /// A template is returned by its ID, an unknown ID gives nothing, and an ID below one is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplateReturnsTheTemplateOrNull()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "alpha started", 1, 3));

        var known = await store.FindTemplateAsync("syslog", "sshd", "alpha started", TestContext.CancellationToken);

        // Act
        var found = await store.TemplateAsync(known!.Id, TestContext.CancellationToken);
        var unknown = await store.TemplateAsync(known.Id + 1000, TestContext.CancellationToken);
        var zero = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.TemplateAsync(0, TestContext.CancellationToken));
        var negative = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.TemplateAsync(-1, TestContext.CancellationToken));

        // Assert
        Assert.IsNotNull(found, "the template");
        Assert.AreEqual(known.Id, found.Id, "ID");
        Assert.AreEqual("syslog", found.Source, "source");
        Assert.AreEqual("sshd", found.Program, "program");
        Assert.AreEqual("alpha started", found.Template, "text");
        Assert.AreEqual(3L, found.Count, "count");
        Assert.AreEqual(Hour(1), found.FirstSeen, "first seen");
        Assert.AreEqual(Hour(1).AddSeconds(2), found.LastSeen, "last seen");
        Assert.IsNull(unknown, "an unknown ID gives null");
        Assert.AreEqual($"{Prefix}template ID must be positive", zero.Message, "zero is refused");
        Assert.AreEqual($"{Prefix}template ID must be positive", negative.Message, "a negative ID is refused");
    }

    /// <summary>
    /// The hours of a source with lines are listed in ascending order, and a broken query is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SourceHoursReturnsCoveredHours()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Hour(6).AddMinutes(5), "one", null),
                         Line("syslog", "sshd", Hour(1).AddMinutes(5), "two", null),
                         Line("syslog", "sshd", Hour(1).AddMinutes(9), "three", null),
                         Line("journal", "sshd", Hour(2), "four", null),
                         Line("syslog", "sshd", Hour(3), "five", null),
                         Line("syslog", "sshd", Hour(30), "six", null));

        var query = new SourceHourQuery
                    {
                        Source = "syslog",
                        From = Hour(0),
                        To = Hour(10),
                        Limit = 100
                    };

        // Act
        var hours = await store.SourceHoursAsync(query, TestContext.CancellationToken);
        var noSource = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SourceHoursAsync(Change(query, change => change.Source = string.Empty), TestContext.CancellationToken));
        var limitZero = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SourceHoursAsync(Change(query, change => change.Limit = 0), TestContext.CancellationToken));
        var limitTooHigh = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SourceHoursAsync(Change(query, change => change.Limit = 10001), TestContext.CancellationToken));

        // Assert
        Assert.AreSequenceEqual([Hour(1), Hour(3), Hour(6)], hours.ToArray(), "the starts of the hours with lines of the source, ascending");
        Assert.AreEqual($"{Prefix}source is required", noSource.Message, "the source is required");
        Assert.AreEqual($"{Prefix}limit must be between 1 and 10000", limitZero.Message, "limit zero");
        Assert.AreEqual($"{Prefix}limit must be between 1 and 10000", limitTooHigh.Message, "limit 10001");
    }

    #endregion // AC-10 Template list

    #region AC-11 Interruption

    /// <summary>
    /// A cancelled token ends each method with an OperationCanceledException.
    /// </summary>
    /// <param name="method">The method and strategy</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("FindLogs index")]
    [DataRow("FindLogs scan")]
    [DataRow("FindLogs scan without text")]
    [DataRow("LogContext")]
    [DataRow("Templates")]
    public async Task FindLogsIsInterruptedByACancelledToken(string method)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "sshd", "alpha started", 1, 5));

        var id = long.Parse((await RowsAsync(directory.Path, "SELECT id FROM records ORDER BY id LIMIT 1"))[0]);
        var cancelled = new CancellationToken(true);
        var filter = Change(Filter(), change => change.Text = "alpha");

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => method switch
                                                                                   {
                                                                                       "FindLogs index" => store.FindLogsAsync(filter, Index, cancelled),
                                                                                       "FindLogs scan" => store.FindLogsAsync(filter, Scan, cancelled),
                                                                                       "FindLogs scan without text" => store.FindLogsAsync(Filter(), Scan, cancelled),
                                                                                       "LogContext" => store.LogContextAsync(new LogContextQuery
                                                                                                                             {
                                                                                                                                 RecordId = id,
                                                                                                                                 Before = 1,
                                                                                                                                 After = 1
                                                                                                                             },
                                                                                                                             cancelled),
                                                                                       _ => store.TemplatesAsync(TemplateQuery(Hour(0), Hour(5), 10), cancelled)
                                                                                   });

        // Assert
        Assert.IsNotNull(exception, $"{method} ends with the cancellation");
    }

    #endregion // AC-11 Interruption

    #region AC-22 and AC-23 Query plans

    /// <summary>
    /// Index mode reads the lines by the record IDs of the probe: no step reads the full-text table, and records is searched by its integer primary key.
    /// </summary>
    /// <param name="filterSet">The name of the filter set</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no filter")]
    [DataRow("source program priority after")]
    [DataRow("template")]
    public async Task FindLogsIndexModeReadsLinesByRecordId(string filterSet)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var filter = Filter();
        LogTemplate? template = null;

        switch (filterSet)
        {
            case "source program priority after":
                {
                    filter.Source = "syslog";
                    filter.Program = "sshd";
                    filter.MaxPriority = 4;
                    filter.After = new LogPosition
                                   {
                                       CapturedAt = Sec(5),
                                       Id = 3
                                   };
                }
                break;

            case "template":
                {
                    filter.TemplateId = 1;
                    template = new LogTemplate
                               {
                                   Id = 1,
                                   Source = "syslog",
                                   Program = "sshd",
                                   Template = "alpha started"
                               };
                }
                break;
        }

        var criteria = LogCriteria.Create(filter, template);

        Assert.IsNotNull(criteria, "the criteria of the filter set");

        var (sql, parameters) = LogFinderQueries.BuildIndex(criteria, [1L, 2L, 3L]);

        // Act
        var plan = await PlanAsync(directory.Path, sql, parameters);

        // Assert
        var details = string.Join("; ", plan.Select(step => step.Detail));

        Assert.DoesNotContain(step => step.Detail.Contains("log_fts", StringComparison.Ordinal), plan, $"no step reads the full-text table: {details}");
        Assert.Contains(step => step.Detail.StartsWith("SEARCH r USING INTEGER PRIMARY KEY", StringComparison.Ordinal), plan, $"records is searched by its primary key: {details}");
    }

    /// <summary>
    /// The template list sums the hours in key order: the hour index is not read and no temporary tree groups the rows.
    /// </summary>
    /// <param name="filterSet">The name of the filter set</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("no filter")]
    [DataRow("source and program")]
    public async Task TemplatesSumsTemplateHoursInKeyOrder(string filterSet)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var query = TemplateQuery(Hour(0), Hour(5), 200);

        if (filterSet == "source and program")
        {
            query.Source = "syslog";
            query.Program = "sshd";
        }

        var (sql, parameters) = LogExplorerQueries.Build(query);

        // Act
        var plan = await PlanAsync(directory.Path, sql, parameters);

        // Assert
        var details = string.Join("; ", plan.Select(step => step.Detail));

        Assert.DoesNotContain(step => step.Detail.Contains("log_template_hours_hour", StringComparison.Ordinal), plan, $"the hour index is not read: {details}");
        Assert.DoesNotContain(step => step.Detail == "USE TEMP B-TREE FOR GROUP BY", plan, $"no temporary tree for the grouping: {details}");
    }

    #endregion // AC-22 and AC-23 Query plans

    #region AC-25 Scan bound by word parts

    /// <summary>
    /// A scan examines the bound for no or one word part and the bound divided by the parts otherwise, and at least one line.
    /// </summary>
    /// <param name="scanLimit">The bound of the call</param>
    /// <param name="tokens">The number of word parts</param>
    /// <param name="expected">The number of examined lines</param>
    [TestMethod]
    [DataRow(8000, 0, 8000)]
    [DataRow(8000, 1, 8000)]
    [DataRow(8000, 2, 4000)]
    [DataRow(8000, 16, 500)]
    [DataRow(5, 16, 1)]
    [DataRow(1, 512, 1)]
    public void LogFinderExaminedLinesDividesTheBoundByTheParts(int scanLimit, int tokens, int expected)
    {
        // Arrange and Act
        var examined = LogFinder.ExaminedLines(scanLimit, tokens);

        // Assert
        Assert.AreEqual(expected, examined, $"lines examined for bound {scanLimit} and {tokens} parts");
    }

    /// <summary>
    /// A scan with a text of two word parts examines half of the bound, and reports the bound reached with the position of the last examined line.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="message">The start of the message of every line</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("alpha beta", "alpha beta")]
    [DataRow("alpha.beta", "alpha.beta")]
    public async Task FindLogsScanDividesTheBoundByTheParts(string text, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(0, 100).Select(index => Line("syslog", "app", Sec(index), $"{message} line {index}", null)).ToArray());

        var ids = await IdsAsync(directory.Path);

        // Act
        var twoParts = await store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.ScanLimit) = (text, 10)), Scan, TestContext.CancellationToken);
        var oneWord = await store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.ScanLimit) = ("line", 10)), Scan, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(5, twoParts.Lines, "half of the bound is examined, and every examined line matches");
        Assert.IsTrue(twoParts.ScanLimitReached, "the divided bound is reached");
        Assert.AreEqual(ids[4], twoParts.Next?.Id, "Next is the last examined line");
        Assert.AreEqual(Sec(4), twoParts.Next?.CapturedAt, "Next has the time of the last examined line");
        Assert.HasCount(10, oneWord.Lines, "one word part examines the whole bound");
        Assert.AreEqual(ids[9], oneWord.Next?.Id, "Next is the last examined line of the whole bound");
    }

    /// <summary>
    /// A scan of a template's lines without a text examines the whole bound, whatever the number of words in the template, and returns exactly the template's lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTemplateScanUsesOnlyTheUserText()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(0, 40).Select(index => Line("syslog", "app", Sec(index), index % 2 == 0 ? $"alpha beta gamma delta {index}" : $"alpha beta gamma delta epsilon {index}", null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var template = await store.FindTemplateAsync("syslog", "app", "alpha beta gamma delta 0", TestContext.CancellationToken);

        // Act
        var page = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.ScanLimit) = (template!.Id, 24)), Scan, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(Enumerable.Range(0, 24).Where(index => index % 2 == 0).Select(index => $"alpha beta gamma delta {index}").ToArray(), Messages(page), "the template's lines among the 24 examined lines, and only those");
        Assert.IsTrue(page.ScanLimitReached, "the whole bound is examined");
        Assert.AreEqual(ids[23], page.Next?.Id, "Next is the 24th line: the template words do not divide the bound");
    }

    #endregion // AC-25 Scan bound by word parts

    #region AC-26 Probe budget

    /// <summary>
    /// With a budget of zero the store does not probe: the page is a scan page that says the index check was stopped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsScansWhenTheProbeBudgetIsUsedUp()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, NeedleLines());

        var ids = await IdsAsync(directory.Path);
        var clock = new FakeTimeProvider();

        // Act
        var page = await store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.ScanLimit) = ("needle", 5)), Index, TimeSpan.Zero, clock, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(page.Lines, "the word is in line 15 only, beyond the bound");
        Assert.IsTrue(page.ScanLimitReached, "the scan bound ends the page");
        Assert.AreEqual(ids[4], page.Next?.Id, "Next is line 5");
        Assert.IsTrue(page.IndexCheckStopped, "the page says the index check was stopped");
    }

    /// <summary>
    /// Within the budget the index check ends and the page comes from the index.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsUsesTheIndexWithinTheProbeBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, NeedleLines());

        var clock = new FakeTimeProvider();

        // Act
        var page = await store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.ScanLimit) = ("needle", 5)), Index, TimeSpan.FromMinutes(1), clock, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["line 15 needle"], Messages(page), "line 15 is read in index mode, beyond the scan bound");
        Assert.IsFalse(page.IndexCheckStopped, "the index check was not stopped");
        Assert.IsFalse(page.ScanLimitReached, "no scan bound was reached");
        Assert.IsNull(page.Next, "the range is exhausted");
    }

    /// <summary>
    /// A probe that is running when the budget ends is stopped, the request goes on in scan mode on the same connection, and the connection works normally afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsBudgetStopsARunningProbe()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "app", "common word", 1, 4000));

        var ids = await IdsAsync(directory.Path);
        var clock = new FakeTimeProvider
                    {
                        AutoAdvanceAmount = TimeSpan.FromSeconds(2)
                    };
        var filter = Change(Filter(), change => (change.Text, change.ScanLimit) = ("common", 5));

        // Act
        var stopped = await store.FindLogsAsync(filter, Index, TimeSpan.FromSeconds(1), clock, TestContext.CancellationToken);
        var scanned = await store.FindLogsAsync(filter, Scan, CancellationToken.None);
        var indexed = await store.FindLogsAsync(Change(filter, change => change.ScanLimit = 100000), Index, CancellationToken.None);

        // Assert
        Assert.IsTrue(stopped.IndexCheckStopped, "the probe was stopped by the budget");
        Assert.HasCount(5, stopped.Lines, "the scan page follows on the same connection");
        Assert.IsTrue(stopped.ScanLimitReached, "the scan bound ends the page");
        Assert.AreEqual(ids[4], stopped.Next?.Id, "Next is line 5");
        Assert.HasCount(5, scanned.Lines, "a later scan works: the ordinary handler is back or the handler is cleared");
        Assert.IsFalse(scanned.IndexCheckStopped, "the ordinary scan page does not claim a stopped index check");
        Assert.HasCount(1000, indexed.Lines, "a later index search works too");
    }

    /// <summary>
    /// A deadline that ends during the probe ends the call with a cancellation, not with a scan page, even when the budget ran out at the same time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsCancelledDuringTheProbeThrows()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "app", "common word", 1, 4000));

        var clock = new FakeTimeProvider
                    {
                        AutoAdvanceAmount = TimeSpan.FromSeconds(2)
                    };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => store.FindLogsAsync(Change(Filter(), filter => (filter.Text, filter.ScanLimit) = ("common", 5)), Index, TimeSpan.FromSeconds(1), clock, deadline.Token));

        // Assert
        Assert.IsNotNull(exception, "the deadline wins over the budget");
        Assert.IsTrue(deadline.IsCancellationRequested, "the deadline ended");
    }

    /// <summary>
    /// A database error during the probe is a store error, not a scan page: only the stop by the budget turns into a scan.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithBrokenIndexDuringTheProbeStillThrowsStoreException()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, NeedleLines());
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "DROP TABLE log_fts");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.FindLogsAsync(Change(Filter(), filter => filter.Text = "needle"), Index, TimeSpan.FromMinutes(1), new FakeTimeProvider(), TestContext.CancellationToken));

        // Assert
        Assert.StartsWith("store: reading logs: ", exception.Message, "the operation is named");
        Assert.IsInstanceOfType<Microsoft.Data.Sqlite.SqliteException>(exception.InnerException, "the cause is the database error");
    }

    #endregion // AC-26 Probe budget

    #region AC-27 Scan after a stopped index check

    /// <summary>
    /// One-part terms become scan words, the other terms are joined into the phrases; a template adds neither.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="withTemplate">Whether the criteria are made for a template</param>
    /// <param name="words">The expected words, joined with a bar</param>
    /// <param name="phrases">The expected phrases, or <c>null</c></param>
    [TestMethod]
    [DataRow("mail 10.0.0.1 smtpd", false, "\"mail\"|\"smtpd\"", "\"10.0.0.1\"")]
    [DataRow("mail", false, "\"mail\"", null)]
    [DataRow("", false, "", null)]
    [DataRow("", true, "", null)]
    public void LogCriteriaSplitsTheScanTextIntoWordsAndPhrases(string text, bool withTemplate, string words, string? phrases)
    {
        // Arrange
        var template = withTemplate
                           ? new LogTemplate
                             {
                                 Id = 1,
                                 Source = "syslog",
                                 Program = "app",
                                 Template = "alpha beta gamma <num>"
                             }
                           : null;

        // Act
        var criteria = LogCriteria.Create(Change(Filter(), filter => filter.Text = text), template);

        // Assert
        Assert.IsNotNull(criteria, "the criteria are resolved");
        Assert.AreEqual(words, string.Join('|', criteria.ScanWords), "the one-part terms, quoted");
        Assert.AreEqual(phrases, criteria.ScanPhrases, "the other terms, quoted and joined");
    }

    /// <summary>
    /// The scan statement tests every word on its own and has no single match parameter.
    /// </summary>
    [TestMethod]
    public void LogFinderQueriesBuildScanTestsEachWordOnItsOwn()
    {
        // Arrange
        var criteria = LogCriteria.Create(Change(Filter(), filter => filter.Text = "a b"), null);

        // Act
        var (sql, parameters) = LogFinderQueries.BuildScan(criteria!);

        // Assert
        Assert.Contains("MATCH $word0", sql, "the first word has its own test");
        Assert.Contains("MATCH $word1", sql, "the second word has its own test");
        Assert.DoesNotContain("$match", sql, "no test of the whole text");
        Assert.AreEqual("\"a\"", parameters["$word0"], "the first word is bound quoted");
        Assert.AreEqual("\"b\"", parameters["$word1"], "the second word is bound quoted");
        Assert.IsFalse(parameters.ContainsKey("$match"), "no match parameter");
    }

    /// <summary>
    /// Terms of several parts find the same lines in scan mode and in index mode, with case and diacritics ignored.
    /// </summary>
    /// <param name="text">The search text; <c>{E9}</c> stands for U+00E9</param>
    /// <param name="expected">The numbers of the expected lines, joined with a comma</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("ww.ww", "2,5")]
    [DataRow("CAFE-bar", "3,4")]
    [DataRow("mail ww.ww", "5")]
    [DataRow("caf{E9}-BAR", "3,4")]
    public async Task FindLogsScanFindsThePhraseLinesTheIndexFinds(string text, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var accent = char.ConvertFromUtf32(0xE9);

        string[] messages = ["ww x ww", "ww ww", $"Caf{accent}-Bar open", "cafe bar", "mail ww.ww"];

        await WriteAsync(store, messages.Select((message, index) => Line("syslog", "app", Sec(index + 1), message, null)).ToArray());

        var search = text.Replace("{E9}", accent, StringComparison.Ordinal);
        var filter = Change(Filter(), change => change.Text = search);
        var wanted = expected.Split(',').Select(number => messages[int.Parse(number, System.Globalization.CultureInfo.InvariantCulture) - 1]).ToArray();

        // Act
        var scanned = await store.FindLogsAsync(filter, Index, TimeSpan.Zero, Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);
        var indexed = await store.FindLogsAsync(filter, Index, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(scanned.IndexCheckStopped, "the first call ran in scan mode");
        Assert.IsFalse(indexed.IndexCheckStopped, "the second call ran in index mode");
        Assert.AreSequenceEqual(wanted, Messages(scanned), "the lines of the scan");
        Assert.AreSequenceEqual(wanted, Messages(indexed), "the lines of the index");
    }

    /// <summary>
    /// A scan checks the phrase in chunks: the lines across several chunks are found, in pages of any size.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsScanChecksPhrasesInChunks()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        int[] hits = [10, 70, 140];

        await WriteAsync(store, Enumerable.Range(1, 150).Select(number => Line("syslog", "app", Sec(number), hits.Contains(number) ? $"line {number} ww.ww" : $"line {number} x", null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var filter = Change(Filter(), change => (change.Text, change.ScanLimit) = ("ww.ww", 1000));

        // Act
        var all = await store.FindLogsAsync(filter, Index, TimeSpan.Zero, Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);
        var first = await store.FindLogsAsync(Change(filter, change => change.Limit = 2), Index, TimeSpan.Zero, Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);
        var second = await store.FindLogsAsync(Change(filter, change => (change.Limit, change.After) = (2, first.Next)), Index, TimeSpan.Zero, Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["line 10 ww.ww", "line 70 ww.ww", "line 140 ww.ww"], Messages(all), "the three lines in three chunks");
        Assert.IsNull(all.Next, "the range is exhausted");
        Assert.IsFalse(all.ScanLimitReached, "the bound is not reached");
        Assert.AreSequenceEqual(["line 10 ww.ww", "line 70 ww.ww"], Messages(first), "two lines on the first page");
        Assert.AreEqual(ids[69], first.Next?.Id, "Next is the last line of the page");
        Assert.AreSequenceEqual(["line 140 ww.ww"], Messages(second), "the page from Next returns the last line");
    }

    /// <summary>
    /// When the page budget runs out the scan ends early with the position of the last examined line, and the next page starts after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsScanStopsAtThePageBudget()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(1, 20).Select(number => Line("syslog", "app", Sec(number), number == 19 ? "line 19 needle" : $"line {number} hay", null)).ToArray());

        var ids = await IdsAsync(directory.Path);
        var clock = new FakeTimeProvider
                    {
                        AutoAdvanceAmount = TimeSpan.FromMilliseconds(300)
                    };
        var filter = Change(Filter(), change => (change.Text, change.ScanLimit) = ("needle", 20));

        // Act
        var page = await store.FindLogsAsync(filter, Index, TimeSpan.Zero, TimeSpan.FromSeconds(1), clock, TestContext.CancellationToken);
        var rest = await store.FindLogsAsync(Change(filter, change => change.After = page.Next), Index, TimeSpan.Zero, Timeout.InfiniteTimeSpan, new FakeTimeProvider(), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(page.Lines, "the page ended before line 19");
        Assert.IsTrue(page.ScanLimitReached, "the page budget ends the page like the bound");
        Assert.IsNotNull(page.Next, "the page has a position");
        Assert.IsGreaterThanOrEqualTo(ids[0], page.Next.Id, "at least one line was examined");
        Assert.IsLessThan(ids[18], page.Next.Id, "the page ended before line 19");
        Assert.AreSequenceEqual(["line 19 needle"], Messages(rest), "the page from Next continues after it and finds the line");
    }

    /// <summary>
    /// With a page budget that is used up from the start, the scan examines exactly one line and returns it when it matches.
    /// </summary>
    /// <param name="firstMatches">Whether line 1 holds the word</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FindLogsScanExaminesOneLineWhenThePageBudgetIsUsedUp(bool firstMatches)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Enumerable.Range(1, 5).Select(number => Line("syslog", "app", Sec(number), firstMatches || number != 1 ? $"line {number} needle" : "line 1 hay", null)).ToArray());

        var ids = await IdsAsync(directory.Path);

        // Act
        var page = await store.FindLogsAsync(Change(Filter(), change => (change.Text, change.ScanLimit) = ("needle", 5)), Index, TimeSpan.Zero, TimeSpan.Zero, new FakeTimeProvider(), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ids[0], page.Next?.Id, "Next is line 1");
        Assert.IsTrue(page.ScanLimitReached, "the page budget ends the page");
        Assert.AreSequenceEqual(firstMatches ? ["line 1 needle"] : [], Messages(page), "line 1 is returned when it matches");
    }

    /// <summary>
    /// A deadline that ends during the scan after a probe that the budget stopped ends the call with a cancellation, also when it falls into the line index.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("common")]
    [DataRow("common.word")]
    public async Task FindLogsCancelledDuringTheScanAfterAStoppedProbeThrows(string text)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Lines("syslog", "app", "common.word here", 1, 4000));

        var filter = Change(Filter(), change => (change.Text, change.ScanLimit) = (text, 10));
        var counting = new CountingClock(TimeSpan.FromSeconds(1));

        var calibration = await store.FindLogsAsync(filter, Index, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1), counting, TestContext.CancellationToken);
        var lastRead = counting.Reads;

        using var deadline = new CancellationTokenSource();
        var cancelling = new CountingClock(TimeSpan.FromSeconds(1))
                         {
                             OnRead = read =>
                                      {
                                          if (read == lastRead)
                                          {
                                              deadline.Cancel();
                                          }
                                      }
                         };

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => store.FindLogsAsync(filter, Index, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1), cancelling, deadline.Token));

        // Assert
        Assert.IsTrue(calibration.IndexCheckStopped, "the probe was stopped by its budget");
        Assert.IsNotEmpty(calibration.Lines, "the uncancelled call returned lines, so work is left after the last clock read");
        Assert.IsNotNull(exception, "the deadline ends the call with a cancellation");
        Assert.IsTrue(deadline.IsCancellationRequested, "the deadline fired during the call");
    }

    #endregion // AC-27 Scan after a stopped index check

    #region Edge cases

    /// <summary>
    /// A template that belongs to another source or program than the filter names matches no line, with either strategy.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTemplateAndContradictingFilterReturnsEmptyPage()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Line("syslog", "sshd", Sec(1), "disk error on sda", null),
                         Line("journal", "cron", Sec(2), "disk error on sdb", null));

        var template = await store.FindTemplateAsync("syslog", "sshd", "disk error on sda", TestContext.CancellationToken);

        // Act
        var matching = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.Source, filter.Program) = (template!.Id, "syslog", "sshd")), Scan, TestContext.CancellationToken);
        var otherSource = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.Source) = (template!.Id, "journal")), Scan, TestContext.CancellationToken);
        var otherProgram = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.Program) = (template!.Id, "cron")), Index, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["disk error on sda"], Messages(matching), "a filter that agrees with the template keeps its line");
        Assert.IsEmpty(otherSource.Lines, "another source than the template's");
        Assert.IsNull(otherSource.Next, "no next page for another source");
        Assert.IsFalse(otherSource.ScanLimitReached, "nothing was scanned for another source");
        Assert.IsEmpty(otherProgram.Lines, "another program than the template's");
        Assert.IsNull(otherProgram.Next, "no next page for another program");
    }

    /// <summary>
    /// A text that no line contains returns an empty page without a next position.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTextOfNoLineReturnsEmptyPage()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "disk error", null));

        // Act
        var scanned = await store.FindLogsAsync(Change(Filter(), filter => filter.Text = "nosuchword"), Scan, TestContext.CancellationToken);
        var indexed = await store.FindLogsAsync(Change(Filter(), filter => filter.Text = "nosuchword"), Index, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(scanned.Lines, "no line with the word, strategy scan");
        Assert.IsNull(scanned.Next, "no next page, strategy scan");
        Assert.IsFalse(scanned.ScanLimitReached, "no scan limit, strategy scan");
        Assert.IsEmpty(indexed.Lines, "no line with the word, strategy index");
        Assert.IsNull(indexed.Next, "no next page, strategy index");
    }

    /// <summary>
    /// When the words of the user already fill the term limit, the words of the template are not added, but the template still decides which lines are returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithTemplateAndFullTermLimitStillChecksTheTemplate()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var words = string.Join(' ', Enumerable.Range(1, 16).Select(number => $"term{number:00}"));

        await WriteAsync(store,
                         Line("syslog", "app", Sec(1), $"{words} failed 5", null),
                         Line("syslog", "app", Sec(2), $"{words} failed 77", null),
                         Line("syslog", "app", Sec(3), $"{words} other 5", null));

        var template = await store.FindTemplateAsync("syslog", "app", $"{words} failed 5", TestContext.CancellationToken);

        // Act
        var scanned = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.Text) = (template!.Id, words)), Scan, TestContext.CancellationToken);
        var indexed = await store.FindLogsAsync(Change(Filter(), filter => (filter.TemplateId, filter.Text) = (template!.Id, words)), Index, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([$"{words} failed 5", $"{words} failed 77"], Messages(scanned), "the lines of the template, by scanning");
        Assert.AreSequenceEqual([$"{words} failed 5", $"{words} failed 77"], Messages(indexed), "the lines of the template, from the index");
    }

    /// <summary>
    /// A database error while reading logs becomes a store error with the database error as the cause.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithBrokenDatabaseThrowsStoreException()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "disk error", null));

        var template = await store.FindTemplateAsync("syslog", "sshd", "disk error", TestContext.CancellationToken);

        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "DROP TABLE log_source_hours; DROP TABLE log_template_hours; DROP TABLE log_templates");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.FindLogsAsync(Change(Filter(), filter => filter.TemplateId = template!.Id), TestContext.CancellationToken));

        // Assert
        Assert.StartsWith("store: reading templates: ", exception.Message, "the operation of the failing query is named");
        Assert.IsInstanceOfType<Microsoft.Data.Sqlite.SqliteException>(exception.InnerException, "the cause");
    }

    /// <summary>
    /// A database error inside the search itself becomes a store error that names the reading of logs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task FindLogsWithBrokenSearchIndexThrowsStoreException()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, Line("syslog", "sshd", Sec(1), "disk error", null));
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "DROP TABLE log_fts");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.FindLogsAsync(Change(Filter(), filter => filter.Text = "disk"), TestContext.CancellationToken));

        // Assert
        Assert.StartsWith("store: reading logs: ", exception.Message, "the operation is named");
        Assert.IsInstanceOfType<Microsoft.Data.Sqlite.SqliteException>(exception.InnerException, "the cause");
    }

    #endregion // Edge cases

    #region Helpers

    /// <summary>
    /// Gets an instant relative to <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="seconds">The seconds after the base</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset Sec(int seconds)
    {
        return _start.AddSeconds(seconds);
    }

    /// <summary>
    /// Gets the start of an hour relative to <see cref="Samples.Base"/>, which starts on a full hour.
    /// </summary>
    /// <param name="offset">The hours after the base</param>
    /// <returns>The start of the hour</returns>
    private static DateTimeOffset Hour(int offset)
    {
        return _start.AddHours(offset);
    }

    /// <summary>
    /// Creates a log line record of the log main on the host web-1.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The priority, or <c>null</c></param>
    /// <returns>The record</returns>
    private static DataRecord Line(string source, string program, DateTimeOffset capturedAt, string message, byte? priority)
    {
        return Entry(source, "main", "web-1", program, capturedAt, message, priority);
    }

    /// <summary>
    /// Creates a log line record.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="log">The log name</param>
    /// <param name="host">The host</param>
    /// <param name="program">The program</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The priority, or <c>null</c></param>
    /// <returns>The record</returns>
    private static DataRecord Entry(string source, string log, string host, string program, DateTimeOffset capturedAt, string message, byte? priority)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = source,
                   CapturedAt = capturedAt,
                   Data = new LogLine
                          {
                              Log = log,
                              Host = host,
                              Program = program,
                              Priority = priority,
                              Message = message
                          }
               };
    }

    /// <summary>
    /// Creates 20 lines one second apart of which only line 15 (the 15th, number 14 counted from zero) holds the word needle.
    /// </summary>
    /// <returns>The records</returns>
    private static DataRecord[] NeedleLines()
    {
        return Enumerable.Range(1, 20).Select(number => Line("syslog", "app", Sec(number), number == 15 ? $"line {number} needle" : $"line {number} hay", null)).ToArray();
    }

    /// <summary>
    /// Creates lines of one template in one hour, one second apart.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="hour">The hour relative to <see cref="Samples.Base"/></param>
    /// <param name="count">The number of lines</param>
    /// <returns>The records</returns>
    private static DataRecord[] Lines(string source, string program, string message, int hour, int count)
    {
        return Enumerable.Range(0, count).Select(index => Line(source, program, Hour(hour).AddSeconds(index), message, null)).ToArray();
    }

    /// <summary>
    /// Creates line number <paramref name="index"/> of the data of the strategy test: lines share capture times in threes, and the words rare, alpha and beta occur in every 47th, 5th and 3rd line.
    /// </summary>
    /// <param name="index">The number of the line</param>
    /// <returns>The record</returns>
    private static DataRecord Common(int index)
    {
        var rare = index % 47 == 0 ? " rare" : string.Empty;
        var alpha = index % 5 == 0 ? " alpha" : string.Empty;
        var beta = index % 3 == 0 ? " beta" : string.Empty;

        return Line(index % 2 == 0 ? "syslog" : "journal", index % 3 == 0 ? "sshd" : "cron", Sec(index / 3 * 7), $"common line {index}{rare}{alpha}{beta}", index % 7 == 0 ? null : (byte)(index % 6));
    }

    /// <summary>
    /// Creates a filter that covers all test data.
    /// </summary>
    /// <param name="change">A change to the filter, or <c>null</c></param>
    /// <returns>The filter</returns>
    private static LogFilter Filter(Action<LogFilter>? change = null)
    {
        var filter = new LogFilter
                     {
                         From = _start.AddDays(-1),
                         To = _start.AddDays(40),
                         Limit = 1000,
                         ScanLimit = 100000
                     };

        change?.Invoke(filter);

        return filter;
    }

    /// <summary>
    /// Copies a filter and changes the copy.
    /// </summary>
    /// <param name="filter">The filter</param>
    /// <param name="change">The change</param>
    /// <returns>The changed copy</returns>
    private static LogFilter Change(LogFilter filter, Action<LogFilter> change)
    {
        var copy = new LogFilter
                   {
                       Text = filter.Text,
                       Source = filter.Source,
                       Program = filter.Program,
                       MaxPriority = filter.MaxPriority,
                       TemplateId = filter.TemplateId,
                       From = filter.From,
                       To = filter.To,
                       After = filter.After,
                       Limit = filter.Limit,
                       ScanLimit = filter.ScanLimit
                   };

        change(copy);

        return copy;
    }

    /// <summary>
    /// Copies a context query and changes the copy.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="change">The change</param>
    /// <returns>The changed copy</returns>
    private static LogContextQuery Change(LogContextQuery query, Action<LogContextQuery> change)
    {
        var copy = new LogContextQuery
                   {
                       RecordId = query.RecordId,
                       Before = query.Before,
                       After = query.After
                   };

        change(copy);

        return copy;
    }

    /// <summary>
    /// Copies a template list query and changes the copy.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="change">The change</param>
    /// <returns>The changed copy</returns>
    private static TemplateListQuery Change(TemplateListQuery query, Action<TemplateListQuery> change)
    {
        var copy = new TemplateListQuery
                   {
                       Source = query.Source,
                       Program = query.Program,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit
                   };

        change(copy);

        return copy;
    }

    /// <summary>
    /// Copies a source hours query and changes the copy.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="change">The change</param>
    /// <returns>The changed copy</returns>
    private static SourceHourQuery Change(SourceHourQuery query, Action<SourceHourQuery> change)
    {
        var copy = new SourceHourQuery
                   {
                       Source = query.Source,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit
                   };

        change(copy);

        return copy;
    }

    /// <summary>
    /// Creates a template list query.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>The query</returns>
    private static TemplateListQuery TemplateQuery(DateTimeOffset from, DateTimeOffset to, int limit)
    {
        return new TemplateListQuery
               {
                   From = from,
                   To = to,
                   Limit = limit
               };
    }

    /// <summary>
    /// Creates a filter with one rule broken.
    /// </summary>
    /// <param name="change">The name of the change</param>
    /// <returns>The filter</returns>
    private static LogFilter Break(string change)
    {
        var filter = Filter(valid => valid.Limit = 10);

        switch (change)
        {
            case "from missing":
                {
                    filter.From = default;
                }
                break;

            case "to missing":
                {
                    filter.To = default;
                }
                break;

            case "from outside":
                {
                    filter.From = StorableTime.Min.AddTicks(-1);
                }
                break;

            case "to outside":
                {
                    filter.To = StorableTime.Max.AddTicks(1);
                }
                break;

            case "from equals to":
                {
                    filter.From = filter.To;
                }
                break;

            case "from after to":
                {
                    filter.From = filter.To.AddHours(1);
                }
                break;

            case "limit zero":
                {
                    filter.Limit = 0;
                }
                break;

            case "limit 1001":
                {
                    filter.Limit = 1001;
                }
                break;

            case "scan limit zero":
                {
                    filter.ScanLimit = 0;
                }
                break;

            case "scan limit 100001":
                {
                    filter.ScanLimit = 100001;
                }
                break;

            case "source too long":
                {
                    filter.Source = new string('s', 1025);
                }
                break;

            case "program too long":
                {
                    filter.Program = new string('p', 1025);
                }
                break;

            case "priority 8":
                {
                    filter.MaxPriority = 8;
                }
                break;

            case "template negative":
                {
                    filter.TemplateId = -1;
                }
                break;

            case "after outside":
                {
                    filter.After = new LogPosition
                                   {
                                       CapturedAt = StorableTime.Max.AddTicks(1),
                                       Id = 1
                                   };
                }
                break;

            case "after id zero":
                {
                    filter.After = new LogPosition
                                   {
                                       CapturedAt = _start,
                                       Id = 0
                                   };
                }
                break;

            default:
                {
                    BreakText(filter, change);
                }
                break;
        }

        return filter;
    }

    /// <summary>
    /// Breaks the search text, the source or the program of a filter.
    /// </summary>
    /// <param name="filter">The filter</param>
    /// <param name="change">The name of the change</param>
    private static void BreakText(LogFilter filter, string change)
    {
        switch (change)
        {
            case "text control character":
                {
                    filter.Text = "a\u0001b";
                }
                break;

            case "text without letter":
                {
                    filter.Text = "--";
                }
                break;

            case "text too many terms":
                {
                    filter.Text = string.Join(' ', Enumerable.Range(0, 17).Select(index => $"t{index}"));
                }
                break;

            case "text too long":
                {
                    filter.Text = new string('a', 1025);
                }
                break;

            case "text lone surrogate":
                {
                    filter.Text = "a" + (char)0xD800 + "b";
                }
                break;

            case "text null":
                {
                    filter.Text = null!;
                }
                break;

            case "source null":
                {
                    filter.Source = null!;
                }
                break;

            case "program null":
                {
                    filter.Program = null!;
                }
                break;
        }
    }

    /// <summary>
    /// Creates the corpus of messages of the template tests: every line of six fixture files with the program of the file, a message of 16,384 bytes and a message without
    /// a literal word.
    /// </summary>
    /// <returns>The programs and messages</returns>
    private static (string Program, string Message)[] Corpus()
    {
        var files = new[] { "mail.log", "kern.log-oom", "mariadb-error.log", "nginx-error.log", "dovecot.log", "plesk-panel.log" };
        var corpus = new List<(string Program, string Message)>();

        foreach (var file in files)
        {
            var program = file.Split('.')[0];

            corpus.AddRange(File.ReadAllLines(RepositoryFiles.Path($"testdata/logs/{file}")).Where(line => line.Length > 0).Select(line => (program, line)));
        }

        corpus.Add(("long", LongMessage()));
        corpus.Add(("long", LongMessage()[..8000] + " different ending"));
        corpus.Add(("numbers", "12345 67890"));
        corpus.Add(("numbers", "7 8"));

        return corpus.ToArray();
    }

    /// <summary>
    /// Creates a message of 16,384 ASCII letters and spaces whose template is cut at 1,024 bytes.
    /// </summary>
    /// <returns>The message</returns>
    private static string LongMessage()
    {
        return string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 700))[..16384];
    }

    /// <summary>
    /// Adds a target line with the same number of other-log lines of the same source on each side, and a matching line behind them.
    /// </summary>
    /// <param name="records">Receives the records</param>
    /// <param name="source">The source</param>
    /// <param name="others">The number of lines of another log on each side</param>
    /// <param name="offset">The seconds after the base at which the first line is captured</param>
    private static void AddSurrounded(List<DataRecord> records, string source, int others, int offset)
    {
        var second = offset;

        records.Add(Entry(source, "auth", "web-1", "sshd", Sec(second++), "match before", null));
        records.AddRange(Enumerable.Range(0, others).Select(_ => Entry(source, "mail", "web-1", "sshd", Sec(second++), "other", null)));
        records.Add(Entry(source, "auth", "web-1", "sshd", Sec(second++), "target", null));
        records.AddRange(Enumerable.Range(0, others).Select(_ => Entry(source, "mail", "web-1", "sshd", Sec(second++), "other", null)));
        records.Add(Entry(source, "auth", "web-1", "sshd", Sec(second), "match after", null));
    }

    /// <summary>
    /// Gets the messages of a page.
    /// </summary>
    /// <param name="page">The page</param>
    /// <returns>The messages in page order</returns>
    private static string[] Messages(LogPage page)
    {
        return page.Lines.Select(line => Message(line)).ToArray();
    }

    /// <summary>
    /// Gets the message of a stored line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The message</returns>
    private static string Message(StoredRecord line)
    {
        return ((LogLine)line.Record.Data!).Message;
    }

    /// <summary>
    /// Describes pages for a comparison: the lines, the position and the flag of each.
    /// </summary>
    /// <param name="pages">The pages</param>
    /// <returns>One text per page</returns>
    private static string[] Signatures(IReadOnlyList<LogPage> pages)
    {
        return pages.Select(page => $"{string.Join(',', page.Lines.Select(line => line.Id))}|{page.Next?.Id}|{page.Next?.CapturedAt.UtcTicks}|{page.ScanLimitReached}").ToArray();
    }

    /// <summary>
    /// Writes records as one batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the records are written</returns>
    private async Task WriteAsync(SqliteStore store, params DataRecord[] records)
    {
        var result = await store.WriteBatchAsync(Samples.Batch(records), TestContext.CancellationToken);

        Assert.AreEqual(new WriteResult(records.Length, 0), result, "all records of the arranged batch are stored");
    }

    /// <summary>
    /// Reads every page of a search, from a position on, with a strategy.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="filter">The filter; its position is the start</param>
    /// <param name="selectiveMatches">The strategy bound, or <c>null</c> for the public method</param>
    /// <returns>A task that returns the pages in order</returns>
    private async Task<List<LogPage>> PagesAsync(SqliteStore store, LogFilter filter, int? selectiveMatches)
    {
        var pages = new List<LogPage>();
        var current = Change(filter,
                             _ =>
                             {
                             });

        do
        {
            var page = selectiveMatches is null ? await store.FindLogsAsync(current, TestContext.CancellationToken) : await store.FindLogsAsync(current, selectiveMatches.Value, TestContext.CancellationToken);

            pages.Add(page);

            current = Change(current, change => change.After = page.Next);
        }
        while (current.After is not null && pages.Count < 2000);

        return pages;
    }

    /// <summary>
    /// Reads all lines of a search with a strategy.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="filter">The filter</param>
    /// <param name="selectiveMatches">The strategy bound</param>
    /// <returns>A task that returns the lines in order</returns>
    private async Task<List<StoredRecord>> AllLinesAsync(SqliteStore store, LogFilter filter, int selectiveMatches)
    {
        var pages = await PagesAsync(store, filter, selectiveMatches);

        return pages.SelectMany(page => page.Lines).ToList();
    }

    /// <summary>
    /// Reads the query plan of a statement.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters of the statement</param>
    /// <returns>A task that returns the steps of the plan</returns>
    private async Task<List<(int Id, int Parent, string Detail)>> PlanAsync(string directory, string sql, Dictionary<string, object> parameters)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
#pragma warning restore CA2100

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var plan = new List<(int Id, int Parent, string Detail)>();

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            plan.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(3)));
        }

        return plan;
    }

    /// <summary>
    /// Reads the IDs of all log lines in capture order.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that returns the IDs</returns>
    private async Task<IReadOnlyList<long>> IdsAsync(string directory)
    {
        var rows = await RowsAsync(directory, "SELECT id FROM records WHERE kind = 'log_line' ORDER BY captured_at, id");

        return rows.Select(long.Parse).ToArray();
    }

    /// <summary>
    /// Reads the ID of the line with a source and a message.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="source">The source</param>
    /// <param name="message">The message</param>
    /// <returns>A task that returns the ID</returns>
    private async Task<long> IdOfAsync(string directory, string source, string message)
    {
        var rows = await RowsAsync(directory, $"SELECT r.id FROM records r JOIN log_lines l ON l.record_id = r.id WHERE r.source = '{source}' AND l.message = '{message}'");

        return long.Parse(rows.Single());
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<IReadOnlyList<string>> RowsAsync(string directory, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(column => Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>
    /// Runs a statement on the database file through its own connection.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100

        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Helpers
}