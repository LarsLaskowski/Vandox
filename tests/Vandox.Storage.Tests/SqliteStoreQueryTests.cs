using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the record queries and the log search of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreQueryTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A query filters by kind, source and the half-open time range, and orders by capture time then ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsFiltersAndOrders()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var other = Samples.Metric(RecordOrigin.Backend, 0, 5, "mem");

        other.Source = "other";

        await store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 20, "late"), Samples.Metric(RecordOrigin.Backend, 0, 10, "b"), Samples.Metric(RecordOrigin.Backend, 0, 10, "a"), other, Samples.Log(RecordOrigin.Backend, 0, 10, "x")), TestContext.CancellationToken);

        var query = Samples.Query(RecordKind.Metric);

        // Act
        var all = await store.RecordsAsync(query, TestContext.CancellationToken);

        query.Source = "host";

        var host = await store.RecordsAsync(query, TestContext.CancellationToken);

        query.From = Samples.Base.AddSeconds(10);
        query.To = Samples.Base.AddSeconds(20);

        var window = await store.RecordsAsync(query, TestContext.CancellationToken);

        query.Limit = 1;

        var limited = await store.RecordsAsync(query, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(4, all.Count, "all metrics");
        Assert.AreEqual("mem", ((MetricPoint)all[0].Record.Data!).Name, "ordered by capture time");
        Assert.AreEqual("late", ((MetricPoint)all[3].Record.Data!).Name, "latest last");
        Assert.AreEqual(3, host.Count, "only the source host");
        Assert.AreEqual("b,a", string.Join(',', window.Select(row => ((MetricPoint)row.Record.Data!).Name)), "half-open range keeps the start, drops the end, orders by ID");
        Assert.AreEqual(1, limited.Count, "limit");
    }

    /// <summary>
    /// A metric query by source and name reads the typed metrics table.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsByMetricName()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(Samples.Batch(Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"), Samples.Metric(RecordOrigin.Backend, 0, 1, "mem")), TestContext.CancellationToken);

        var query = Samples.Query(RecordKind.Metric);

        query.Source = "host";
        query.Name = "mem";

        // Act
        var rows = await store.RecordsAsync(query, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, rows.Count, "one metric");
        Assert.AreEqual("mem", ((MetricPoint)rows[0].Record.Data!).Name, "name");
    }

    /// <summary>
    /// Queries that break a rule are refused.
    /// </summary>
    /// <param name="what">The rule that is broken</param>
    /// <param name="rule">The expected rule in the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("from missing", "from and to are required")]
    [DataRow("to missing", "from and to are required")]
    [DataRow("out of range", "from and to must be within the storable range")]
    [DataRow("from after to", "from must be before to")]
    [DataRow("limit zero", "limit must be between 1 and 10000")]
    [DataRow("limit too large", "limit must be between 1 and 10000")]
    [DataRow("unknown kind", "unknown kind")]
    [DataRow("name without metric", "name needs the kind metric")]
    [DataRow("name without source", "name needs a source")]
    public async Task SqliteStoreRecordsRefusesInvalidQuery(string what, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = Samples.Query(RecordKind.Metric);

        switch (what)
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

            case "out of range":
                {
                    query.From = new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero);
                }
                break;

            case "from after to":
                {
                    query.From = query.To;
                }
                break;

            case "limit zero":
                {
                    query.Limit = 0;
                }
                break;

            case "limit too large":
                {
                    query.Limit = StorageLimits.MaxQueryLimit + 1;
                }
                break;

            case "unknown kind":
                {
                    query.Kind = "x";
                }
                break;

            case "name without metric":
                {
                    query.Kind = RecordKind.LogLine;
                    query.Name = "cpu";
                    query.Source = "host";
                }
                break;

            default:
                {
                    query.Name = "cpu";
                }
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.RecordsAsync(query, TestContext.CancellationToken), "invalid query");

        // Assert
        Assert.AreEqual($"store: invalid query: {rule}", exception.Message, "message");
    }

    /// <summary>
    /// The log search finds lines that contain every term, optionally of one source, and treats operators literally.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSearchLogsFindsTermsLiterally()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var mail = Samples.Log(RecordOrigin.Backend, 0, 2, "postfix: connect to host failed");

        mail.Source = "mail";

        await store.WriteBatchAsync(Samples.Batch(Samples.Log(RecordOrigin.Backend, 0, 0, "Out of memory: Killed process 42 (mysqld)"),
                                                  Samples.Log(RecordOrigin.Backend, 0, 1, "Memory pressure rising AND OR NOT"),
                                                  mail,
                                                  Samples.Log(RecordOrigin.Backend, 0, 3, "über-slow response \"quoted\"")),
                                    TestContext.CancellationToken);

        var search = new LogSearch
                     {
                         Text = "memory",
                         From = Samples.Base.AddHours(-1),
                         To = Samples.Base.AddHours(1),
                         Limit = 10
                     };

        // Act
        var memory = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        search.Text = "memory killed";

        var both = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        search.Text = "AND NOT";

        var operators = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        search.Text = "uber \"quoted\"";

        var diacritics = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        search.Text = "failed";
        search.Source = "mail";

        var bySource = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        search.Source = "syslog";

        var otherSource = await store.SearchLogsAsync(search, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, memory.Count, "both lines with the word memory, case-insensitive");
        Assert.AreEqual(1, both.Count, "only the line with both terms");
        Assert.AreEqual(1, operators.Count, "AND and NOT are literal terms");
        Assert.AreEqual(1, diacritics.Count, "diacritics are folded and quotes are literal");
        Assert.AreEqual(1, bySource.Count, "the source mail");
        Assert.AreEqual(0, otherSource.Count, "no such line in the source syslog");
    }

    /// <summary>
    /// Searches that break a rule are refused without echoing the text.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="rule">The expected rule in the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("", "search text has no terms")]
    [DataRow("   ", "search text has no terms")]
    [DataRow("a\u0001b", "search text contains a control or format character")]
    [DataRow("a​b", "search text contains a control or format character")]
    [DataRow("***", "every search term needs a letter or a digit")]
    [DataRow("a b c d e f g h i j k l m n o p q", "search text has more than 16 terms")]
    public async Task SqliteStoreSearchLogsRefusesInvalidText(string text, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var search = new LogSearch
                     {
                         Text = text,
                         From = Samples.Base.AddHours(-1),
                         To = Samples.Base.AddHours(1),
                         Limit = 10
                     };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SearchLogsAsync(search, TestContext.CancellationToken), "invalid search");

        // Assert
        Assert.AreEqual($"store: invalid query: {rule}", exception.Message, "message");
    }

    /// <summary>
    /// A search text with a lone surrogate cannot be encoded as UTF-8 and is refused.
    /// </summary>
    [TestMethod]
    public void FtsQueryBuildRefusesLoneSurrogate()
    {
        // Arrange
        var text = string.Concat("a ", '\ud800'.ToString(), " b");

        // Act
        var exception = Assert.ThrowsExactly<InvalidQueryException>(() => FtsQuery.Build(text), "lone surrogate");

        // Assert
        Assert.AreEqual("store: invalid query: search text is not valid UTF-8", exception.Message, "message");
    }

    /// <summary>
    /// A search text that is too long, and a search window that is invalid, are refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSearchLogsRefusesLongTextAndBadWindow()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var long1 = new LogSearch
                    {
                        Text = new string('a', StorageLimits.MaxSearchBytes + 1),
                        From = Samples.Base.AddHours(-1),
                        To = Samples.Base.AddHours(1),
                        Limit = 10
                    };
        var window = new LogSearch
                     {
                         Text = "a",
                         From = Samples.Base,
                         To = Samples.Base,
                         Limit = 10
                     };

        // Act
        var tooLong = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SearchLogsAsync(long1, TestContext.CancellationToken), "long text");
        var badWindow = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.SearchLogsAsync(window, TestContext.CancellationToken), "bad window");

        // Assert
        Assert.AreEqual("store: invalid query: search text is longer than 1024 bytes", tooLong.Message, "long text message");
        Assert.AreEqual("store: invalid query: from must be before to", badWindow.Message, "window message");
    }

    #endregion // Methods
}