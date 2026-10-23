using Microsoft.Data.Sqlite;

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
        Assert.HasCount(4, all, "all metrics");
        Assert.AreEqual("mem", ((MetricPoint)all[0].Record.Data!).Name, "ordered by capture time");
        Assert.AreEqual("late", ((MetricPoint)all[3].Record.Data!).Name, "latest last");
        Assert.HasCount(3, host, "only the source host");
        Assert.AreEqual("b,a", string.Join(',', window.Select(row => ((MetricPoint)row.Record.Data!).Name)), "half-open range keeps the start, drops the end, orders by ID");
        Assert.HasCount(1, limited, "limit");
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
        Assert.HasCount(1, rows, "one metric");
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
        Assert.HasCount(2, memory, "both lines with the word memory, case-insensitive");
        Assert.HasCount(1, both, "only the line with both terms");
        Assert.HasCount(1, operators, "AND and NOT are literal terms");
        Assert.HasCount(1, diacritics, "diacritics are folded and quotes are literal");
        Assert.HasCount(1, bySource, "the source mail");
        Assert.IsEmpty(otherSource, "no such line in the source syslog");
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
    /// The word parts of a text are the runs of letters, numbers and private-use characters; everything else separates them.
    /// </summary>
    /// <param name="template">The text; <c>{E9}</c> and <c>{E000}</c> stand for the characters U+00E9 and U+E000, because the format tool turns escapes in the source into characters</param>
    /// <param name="expected">The number of word parts</param>
    [TestMethod]
    [DataRow("error", 1)]
    [DataRow("10.0.0.1", 4)]
    [DataRow("postfix/smtpd connect", 3)]
    [DataRow("a_b", 2)]
    [DataRow("{E9}-x", 2)]
    [DataRow("{E000}", 1)]
    [DataRow("{E000}a{E000}", 1)]
    [DataRow("---", 0)]
    [DataRow("", 0)]
    [DataRow("  one   two  ", 2)]
    public void FtsQueryCountTokensCountsTheTokenizerParts(string template, int expected)
    {
        // Arrange
        var text = template.Replace("{E9}", char.ConvertFromUtf32(0xE9), StringComparison.Ordinal).Replace("{E000}", char.ConvertFromUtf32(0xE000), StringComparison.Ordinal);

        // Act
        var count = FtsQuery.CountTokens(text);

        // Assert
        Assert.AreEqual(expected, count, $"the word parts of '{template}'");
    }

    /// <summary>
    /// Each term is quoted as the whole expression quotes it, and the expression is the terms joined with one space.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="first">The first quoted term</param>
    /// <param name="second">The second quoted term, or an empty text</param>
    [TestMethod]
    [DataRow("error", "\"error\"", "")]
    [DataRow("a \"b", "\"a\"", "\"\"\"b\"")]
    [DataRow("10.0.0.1 smtpd", "\"10.0.0.1\"", "\"smtpd\"")]
    public void FtsQueryBuildTermsQuotesEachTerm(string text, string first, string second)
    {
        // Arrange
        string[] expected = second.Length == 0 ? [first] : [first, second];

        // Act
        var terms = FtsQuery.BuildTerms(text);

        // Assert
        Assert.AreSequenceEqual(expected, terms.ToArray(), "the quoted terms in order");
        Assert.AreEqual(FtsQuery.Build(text), string.Join(' ', terms), "the expression is the terms joined with one space");
    }

    /// <summary>
    /// The terms are validated as the whole expression is, with the same message.
    /// </summary>
    /// <param name="change">The name of the broken text</param>
    [TestMethod]
    [DataRow("17 terms")]
    [DataRow("no terms")]
    [DataRow("term without letter")]
    public void FtsQueryBuildTermsRejectsWhatBuildRejects(string change)
    {
        // Arrange
        var text = change switch
                   {
                       "17 terms" => string.Join(' ', Enumerable.Range(1, 17).Select(number => $"t{number}")),
                       "no terms" => "   ",
                       _ => "alpha --"
                   };

        // Act
        var expected = Assert.ThrowsExactly<InvalidQueryException>(() => FtsQuery.Build(text), $"Build refuses '{change}'");
        var actual = Assert.ThrowsExactly<InvalidQueryException>(() => FtsQuery.BuildTerms(text), $"BuildTerms refuses '{change}'");

        // Assert
        Assert.AreEqual(expected.Message, actual.Message, "the same message");
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

    /// <summary>
    /// Paging with the last capture time as the start and the last ID as AfterId returns every record once, in order, even when many records share one capture time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsPagesAfterId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var records = new List<DataRecord>();

        for (var index = 0; index < 5; index++)
        {
            records.Add(Samples.Log(RecordOrigin.Backend, 0, 10, $"same time {index}"));
            records.Add(Samples.Metric(RecordOrigin.Backend, 0, 10, "cpu"));
        }

        records.Add(Samples.Log(RecordOrigin.Backend, 0, 20, "later 1"));
        records.Add(Samples.Log(RecordOrigin.Backend, 0, 20, "later 2"));
        records.Add(Samples.Metric(RecordOrigin.Backend, 0, 20, "cpu"));
        records.Add(Samples.Metric(RecordOrigin.Backend, 0, 20, "cpu"));
        await store.WriteBatchAsync(Samples.Batch([.. records]), TestContext.CancellationToken);

        var lines = Samples.Query(RecordKind.LogLine);
        var series = Samples.Query(RecordKind.Metric);

        series.Source = "host";
        series.Name = "cpu";

        // Act
        var everyLine = await store.RecordsAsync(lines, TestContext.CancellationToken);
        var everyMetric = await store.RecordsAsync(series, TestContext.CancellationToken);
        var pagedLines = await ReadPagesAsync(store, lines, 2);
        var pagedMetrics = await ReadPagesAsync(store, series, 2);

        // Assert
        Assert.HasCount(7, everyLine, "log lines stored");
        Assert.HasCount(7, everyMetric, "metrics stored");
        Assert.AreSequenceEqual(everyLine.Select(row => row.Id), pagedLines, "pages of two return every log line once, in order");
        Assert.AreSequenceEqual(everyMetric.Select(row => row.Id), pagedMetrics, "pages of two return every metric of the series once, in order");
    }

    /// <summary>
    /// A negative AfterId is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsRefusesNegativeAfterId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = Samples.Query(RecordKind.LogLine);

        query.AfterId = -1;

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.RecordsAsync(query, TestContext.CancellationToken), "negative after id");

        // Assert
        Assert.AreEqual("store: invalid query: after id must not be negative", exception.Message, "message");
    }

    /// <summary>
    /// A process snapshot whose texts hold characters that JSON escapes is read back equal through the UTF-8 bytes of the payload column.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsRoundTripsEscapedPayloadText()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        const string command = "<mariadbd> \u00E4";
        const string cmdline = "/usr/sbin/mariadbd --name=<\u00E4> \u202E \U0001F600";
        var snapshot = new ProcessSnapshot
                       {
                           Complete = true,
                           Processes = [
                                           new ProcessSample
                                           {
                                               Pid = 7,
                                               Command = command,
                                               Cmdline = cmdline
                                           }
                                       ]
                       };

        await store.WriteBatchAsync(Samples.Batch(new DataRecord
                                                  {
                                                      Origin = RecordOrigin.Backend,
                                                      Source = "host",
                                                      CapturedAt = Samples.Base,
                                                      Data = snapshot
                                                  }),
                                    TestContext.CancellationToken);

        // Act
        var rows = await store.RecordsAsync(Samples.Query(RecordKind.ProcessSnapshot), TestContext.CancellationToken);
        var stored = await StoredDataAsync(Path.Combine(directory.Path, StorageLimits.FileName));

        // Assert
        Assert.IsTrue(char.IsSurrogatePair(cmdline, cmdline.IndexOf('\uD83D', StringComparison.Ordinal)), "the command line holds a surrogate pair");
        Assert.Contains("\u202E", cmdline, "the command line holds the right-to-left override");
        Assert.Contains(string.Concat((char)92, "u003C"), stored, "the stored JSON holds the escaped <");
        Assert.HasCount(1, rows, "one process snapshot");

        var read = (ProcessSnapshot)rows[0].Record.Data!;

        Assert.HasCount(1, read.Processes!, "one process");
        Assert.AreEqual(command, read.Processes![0].Command, "command with < and a");
        Assert.AreEqual(cmdline, read.Processes[0].Cmdline, "command line with a right-to-left override and a surrogate pair");
        Assert.AreEqual(7, read.Processes[0].Pid, "process ID");
    }

    /// <summary>
    /// The start and the end of a backup run are stored as JSON documents and read back by their kind and source.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreQueryRecordsAcceptsBackupRun()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        const string bootId = "0123abcd-0123-0123-0123-0123456789ab";
        var started = Samples.Base.AddMinutes(5);
        var lastSeen = started.AddMinutes(20);
        var start = new DataRecord
                    {
                        Origin = RecordOrigin.Backend,
                        Source = "backups",
                        CapturedAt = started,
                        Data = new BackupRun
                               {
                                   Phase = "start",
                                   BootId = bootId,
                                   StartedAt = started,
                                   Pid = 5150,
                                   Command = "pzstd",
                                   Processes = 3
                               }
                    };
        var end = new DataRecord
                  {
                      Origin = RecordOrigin.Backend,
                      Source = "backups",
                      CapturedAt = lastSeen,
                      Data = new BackupRun
                             {
                                 Phase = "end",
                                 BootId = bootId,
                                 StartedAt = started,
                                 LastSeenAt = lastSeen,
                                 GoneBy = lastSeen.AddSeconds(10),
                                 MaxProcesses = 4
                             }
                  };

        await store.WriteBatchAsync(Samples.Batch(end, start, Samples.Metric(RecordOrigin.Backend, 0, 10, "cpu")), TestContext.CancellationToken);

        var query = Samples.Query(RecordKind.BackupRun);

        // Act
        var rows = await store.RecordsAsync(query, TestContext.CancellationToken);

        query.Source = "other";

        var otherSource = await store.RecordsAsync(query, TestContext.CancellationToken);
        var metrics = await store.RecordsAsync(Samples.Query(RecordKind.Metric), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, rows, "the start and the end");
        Assert.AreEqual(RecordKind.BackupRun, rows[0].Record.Data!.Kind, "kind of the first row");
        Assert.AreEqual(started, rows[0].Record.CapturedAt, "ordered by capture time: the start first");

        var readStart = (BackupRun)rows[0].Record.Data!;
        var readEnd = (BackupRun)rows[1].Record.Data!;

        Assert.AreEqual("start", readStart.Phase, "start: phase");
        Assert.AreEqual(bootId, readStart.BootId, "start: boot_id");
        Assert.AreEqual(5150, readStart.Pid, "start: pid");
        Assert.AreEqual("pzstd", readStart.Command, "start: command");
        Assert.AreEqual(3u, readStart.Processes, "start: processes");
        Assert.AreEqual(started, readStart.StartedAt, "start: started_at");
        Assert.AreEqual("end", readEnd.Phase, "end: phase");
        Assert.AreEqual(started, readEnd.StartedAt, "end: started_at");
        Assert.AreEqual(lastSeen, readEnd.LastSeenAt, "end: last_seen_at");
        Assert.AreEqual(lastSeen.AddSeconds(10), readEnd.GoneBy, "end: gone_by");
        Assert.AreEqual(4u, readEnd.MaxProcesses, "end: max_processes");
        Assert.IsEmpty(otherSource, "no backup run of another source");
        Assert.HasCount(1, metrics, "the metric is not a backup run");
    }

    /// <summary>
    /// A stored payload that is SQL NULL or the JSON text null is refused with the no-data message.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRecordsRefusesStoredNullPayload()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(Samples.Batch(Samples.Gap(0)), TestContext.CancellationToken);

        // Act and assert
        foreach (var value in new[] { "NULL", "'null'" })
        {
            await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), $"UPDATE records SET data = {value}");

            var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.RecordsAsync(Samples.Query(RecordKind.Gap), TestContext.CancellationToken), $"stored payload {value}");

            Assert.AreEqual("store: reading records: decoding gap payload: no data", exception.Message, $"message for {value}");
        }
    }

    /// <summary>
    /// A token that is already cancelled makes the log search throw an OperationCanceledException.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSearchLogsThrowsCanceledForCancelledToken()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        using var source = new CancellationTokenSource();
        var search = new LogSearch
                     {
                         Text = "memory",
                         From = Samples.Base.AddHours(-1),
                         To = Samples.Base.AddHours(1),
                         Limit = 10
                     };

        await source.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SearchLogsAsync(search, source.Token), "a cancelled token cancels the search");
    }

    /// <summary>
    /// A reader connection leased by the store interrupts a running statement once the token is cancelled.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreLeaseReaderInterruptsRunningStatement()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        await using var lease = await store.LeaseReaderAsync(source.Token);

        lease.Connection.CreateFunction("cancel_at",
                                        (long value, long at) =>
                                        {
                                            if (value == at)
                                            {
                                                source.Cancel();
                                            }

                                            return 0L;
                                        });

        // Act
        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ReaderQuery.ReadAsync(lease.Connection,
                                                                                                                "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 20000000) SELECT count(*) FROM c WHERE cancel_at(x, 1) = 0",
                                                                                                                new Dictionary<string, object>(),
                                                                                                                reader => reader.GetInt64(0),
                                                                                                                "store: reading records",
                                                                                                                source.Token),
                                                                                    "the leased connection interrupts the statement");

        // Assert
        var inner = Assert.IsInstanceOfType<SqliteException>(exception.InnerException, "inner exception");

        Assert.AreEqual(9, inner.SqliteErrorCode, "SQLITE_INTERRUPT");
    }

    /// <summary>
    /// A reader connection leased by the store stays query-only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreLeaseReaderStaysQueryOnly()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        await using var lease = await store.LeaseReaderAsync(source.Token);
        await using var command = lease.Connection.CreateCommand();

        command.CommandText = "INSERT INTO meta (key, value) VALUES ('probe', 'x')";

        // Act and Assert
        await Assert.ThrowsExactlyAsync<SqliteException>(() => command.ExecuteNonQueryAsync(TestContext.CancellationToken), "a write on a reader connection fails");
    }

    /// <summary>
    /// Reads the stored JSON text of the first record with a JSON payload.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <returns>A task that returns the text</returns>
    private async Task<string> StoredDataAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT data FROM records WHERE data IS NOT NULL LIMIT 1";

        return (string)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    /// <summary>
    /// Runs a statement on the database file through its own connection.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads a query page by page, continuing after the last record of each full page, and stops after a safe number of pages.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="query">The query; its limit is replaced</param>
    /// <param name="limit">The page size</param>
    /// <returns>A task that returns the IDs of all pages in the order they were read</returns>
    private async Task<List<long>> ReadPagesAsync(SqliteStore store, RecordQuery query, int limit)
    {
        var ids = new List<long>();
        var paged = new RecordQuery
                    {
                        Kind = query.Kind,
                        Source = query.Source,
                        Name = query.Name,
                        From = query.From,
                        To = query.To,
                        Limit = limit
                    };

        for (var page = 0; page < 20; page++)
        {
            var rows = await store.RecordsAsync(paged, TestContext.CancellationToken);

            ids.AddRange(rows.Select(row => row.Id));

            if (rows.Count < limit)
            {
                break;
            }

            paged.From = rows[^1].Record.CapturedAt;
            paged.AfterId = rows[^1].Id;
        }

        return ids;
    }

    #endregion // Methods
}