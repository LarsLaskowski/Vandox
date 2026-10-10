using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

using Vandox.Core.Model;
using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BatchDecoder"/>
/// </summary>
[TestClass]
public class BatchDecoderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A valid batch yields its header and its records in order, then ends.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsHeaderAndRecords()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(5));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var second = await decoder.NextAsync(TestContext.CancellationToken);
        var end = await decoder.NextAsync(TestContext.CancellationToken);
        var again = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("agent-1", decoder.Header.AgentId, "agent ID");
        Assert.AreEqual(1UL, first?.Seq, "first seq");
        Assert.AreEqual(RecordOrigin.Agent, first?.Origin, "records have origin agent");
        Assert.AreEqual(5UL, second?.Seq, "second seq");
        Assert.IsNull(end, "end of the batch");
        Assert.IsNull(again, "the end is sticky");
    }

    /// <summary>
    /// Carriage returns before the line feed and an unterminated last line are accepted.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderAcceptsCrLfAndUnterminatedLastLine()
    {
        // Arrange
        using var input = BatchBuilder.Gzip($"{BatchBuilder.Header}\r\n{BatchBuilder.Metric(1)}\r\n{BatchBuilder.Metric(2)}");
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var second = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1UL, first?.Seq, "first seq");
        Assert.AreEqual(2UL, second?.Seq, "second seq");
    }

    /// <summary>
    /// A bad header is refused when the batch is opened.
    /// </summary>
    /// <param name="header">The header line</param>
    /// <param name="kind">The expected error class</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("", WireErrorKind.Malformed)]
    [DataRow("not json", WireErrorKind.Malformed)]
    [DataRow("{}", WireErrorKind.UnsupportedVersion)]
    [DataRow("null", WireErrorKind.UnsupportedVersion)]
    [DataRow("""{"format_major":null}""", WireErrorKind.UnsupportedVersion)]
    [DataRow("[1]", WireErrorKind.UnsupportedVersion)]
    [DataRow("""{"format_major":"1"}""", WireErrorKind.UnsupportedVersion)]
    [DataRow("""{"format_major":2}""", WireErrorKind.UnsupportedVersion)]
    [DataRow("""{"format_major":99999999999}""", WireErrorKind.UnsupportedVersion)]
    [DataRow("""{"format_major":1,"format_minor":"x"}""", WireErrorKind.Malformed)]
    [DataRow("""{"format_major":1,"format_minor":-1,"agent_id":"a","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}""", WireErrorKind.Invalid)]
    [DataRow("""{"format_major":1,"agent_id":"-a","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}""", WireErrorKind.Invalid)]
    [DataRow("""{"format_major":1,"agent_id":"a","boot_id":"nope","mode":"live"}""", WireErrorKind.Invalid)]
    [DataRow("""{"format_major":1,"agent_id":"a","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"x"}""", WireErrorKind.Invalid)]
    public async Task BatchDecoderRefusesBadHeader(string header, WireErrorKind kind)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(header, BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "bad header");

        // Assert
        Assert.AreEqual(kind, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
    }

    /// <summary>
    /// A batch without any line has no header.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesEmptyStream()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(string.Empty);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "no header");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
    }

    /// <summary>
    /// A stream that is not gzip data is malformed.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesPlainText()
    {
        // Arrange
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(BatchBuilder.Header + "\n"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "plain text");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
    }

    /// <summary>
    /// A header without records is an empty batch.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesBatchWithoutRecords()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header);
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "empty batch");

        // Assert
        Assert.AreEqual(WireErrorKind.EmptyBatch, exception.Kind, "error class");
    }

    /// <summary>
    /// A bad record line is refused with its class and line number, and the error is sticky.
    /// </summary>
    /// <param name="record">The record line</param>
    /// <param name="kind">The expected error class</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("", WireErrorKind.Malformed)]
    [DataRow("nope", WireErrorKind.Malformed)]
    [DataRow("[]", WireErrorKind.Malformed)]
    [DataRow("null", WireErrorKind.UnknownKind)]
    [DataRow("""{"kind":null,"source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"x"}}""", WireErrorKind.UnknownKind)]
    [DataRow("""{"kind":"null","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"x"}}""", WireErrorKind.UnknownKind)]
    [DataRow("NULL", WireErrorKind.Malformed)]
    [DataRow("""{"kind":"x","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{}}""", WireErrorKind.UnknownKind)]
    [DataRow("""{"kind":"metric","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z"}""", WireErrorKind.Invalid)]
    [DataRow("""{"kind":"metric","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":null}""", WireErrorKind.Invalid)]
    [DataRow("""{"kind":"metric","source":"h","seq":-1,"captured_at":"2026-10-01T10:00:00Z","data":{}}""", WireErrorKind.Malformed)]
    [DataRow("""{"kind":"metric","source":"h","seq":1,"captured_at":"yesterday","data":{}}""", WireErrorKind.Malformed)]
    [DataRow("""{"kind":"metric","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":5}}""", WireErrorKind.Malformed)]
    [DataRow("""{"kind":"metric","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":""}}""", WireErrorKind.Invalid)]
    [DataRow("""{"kind":"metric","source":"h","seq":0,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"x"}}""", WireErrorKind.Invalid)]
    public async Task BatchDecoderRefusesBadRecord(string record, WireErrorKind kind)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, record);
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "bad record");
        var again = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "sticky error");

        // Assert
        Assert.AreEqual(kind, exception.Kind, "error class");
        Assert.AreEqual(2, exception.Line, "line");
        Assert.AreSame(exception, again, "the same error is reported again");
    }

    /// <summary>
    /// A model error is carried with the exception, and the message holds no payload text.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReportsFieldOfInvalidRecord()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, """{"kind":"metric","source":"h","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"secret value"}}""");
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "invalid record");

        // Assert
        Assert.AreEqual("data.name", exception.FieldError?.Field, "field");
        Assert.AreEqual("wire: line 2: model: data.name: invalid characters", exception.Message, "message");
        Assert.DoesNotContain("secret", exception.Message, "payload text stays out of the message");
    }

    /// <summary>
    /// Sequence numbers must strictly increase.
    /// </summary>
    /// <param name="second">The sequence number of the second record</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(0)]
    public async Task BatchDecoderRefusesSequenceThatDoesNotIncrease(int second)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(second));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        await decoder.NextAsync(TestContext.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "sequence");

        // Assert
        Assert.IsTrue(exception.Kind is WireErrorKind.Sequence or WireErrorKind.Invalid, "error class");
    }

    /// <summary>
    /// A line that exceeds the line limit makes the read fail with a limit error.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderLimitsLineLength()
    {
        // Arrange
        var header = BatchBuilder.Header;
        var limits = new WireLimits
                     {
                         MaxLineBytes = header.Length + 10
                     };
        using var input = BatchBuilder.Gzip(header, BatchBuilder.Metric(1, new string('a', 100)));
        using var decoder = await BatchDecoder.OpenAsync(input, limits, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "long record line");

        // Assert
        Assert.AreEqual(WireErrorKind.LimitExceeded, exception.Kind, "error class");
        Assert.AreEqual(2, exception.Line, "line");
    }

    /// <summary>
    /// An unterminated last line that exceeds the line limit is refused as well.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderLimitsUnterminatedLastLine()
    {
        // Arrange
        var limits = new WireLimits
                     {
                         MaxLineBytes = BatchBuilder.Header.Length + 10
                     };
        using var input = BatchBuilder.Gzip($"{BatchBuilder.Header}\n{BatchBuilder.Metric(1, new string('a', 100))}");
        using var decoder = await BatchDecoder.OpenAsync(input, limits, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "long last line");

        // Assert
        Assert.AreEqual(WireErrorKind.LimitExceeded, exception.Kind, "error class");
    }

    /// <summary>
    /// The number of records and the decompressed size are limited.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderLimitsRecordsAndBytes()
    {
        // Arrange
        using var tooMany = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(2));
        using var tooBig = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(2));
        using var recordLimited = await BatchDecoder.OpenAsync(tooMany,
                                                               new WireLimits
                                                               {
                                                                   MaxRecords = 1
                                                               },
                                                               TestContext.CancellationToken);
        using var sizeLimited = await BatchDecoder.OpenAsync(tooBig,
                                                             new WireLimits
                                                             {
                                                                 MaxBatchBytes = BatchBuilder.Header.Length + BatchBuilder.Metric(1).Length + 5
                                                             },
                                                             TestContext.CancellationToken);

        // Act
        await recordLimited.NextAsync(TestContext.CancellationToken);

        var records = await Assert.ThrowsExactlyAsync<WireException>(() => recordLimited.NextAsync(TestContext.CancellationToken), "record limit");

        await sizeLimited.NextAsync(TestContext.CancellationToken);

        var bytes = await Assert.ThrowsExactlyAsync<WireException>(() => sizeLimited.NextAsync(TestContext.CancellationToken), "size limit");

        // Assert
        Assert.AreEqual(WireErrorKind.LimitExceeded, records.Kind, "record limit class");
        Assert.AreEqual(WireErrorKind.LimitExceeded, bytes.Kind, "size limit class");
    }

    /// <summary>
    /// A truncated gzip stream never yields its cut last line as a record.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesTruncatedStream()
    {
        // Arrange
        using var whole = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(2));
        var bytes = whole.ToArray();
        using var cut = new MemoryStream(bytes, 0, bytes.Length - 12);
        using var decoder = await BatchDecoder.OpenAsync(cut, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => ReadAllAsync(decoder), "truncated stream");

        // Assert
        Assert.AreEqual(1UL, first?.Seq, "the complete record is read");
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
    }

    /// <summary>
    /// A stream whose last bytes were cut off (the trailer) is refused, and so is one with a damaged checksum.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesStreamWithMissingOrDamagedTrailer()
    {
        // Arrange
        using var whole = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), BatchBuilder.Metric(2));
        var bytes = whole.ToArray();
        var damaged = (byte[])bytes.Clone();

        damaged[^6] ^= 0xff;

        using var noTrailer = new MemoryStream(bytes, 0, bytes.Length - 8);
        using var badCrc = new MemoryStream(damaged);

        // Act
        var cut = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(noTrailer), "stream without trailer");
        var crc = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(badCrc), "stream with a damaged checksum");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, cut.Kind, "cut class");
        Assert.AreEqual(WireErrorKind.Malformed, crc.Kind, "checksum class");
    }

    /// <summary>
    /// A <c>null</c> at any key of any line of the golden batch ends in a record or a <see cref="WireException"/>, never in another exception.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderThrowsOnlyWireExceptionForNullAtAnyKey()
    {
        // Arrange
        var lines = GoldenLines();
        var failures = new List<string>();

        // Act
        foreach (var (index, path) in MemberPaths(lines))
        {
            try
            {
                await OutcomeAsync(index, Apply(lines[index], path, "null"));
            }
            catch (Exception exception)
            {
                failures.Add($"line {index + 1} {Format(path)}: {exception.GetType().Name}");
            }
        }

        // Assert
        Assert.IsEmpty(failures, $"null escapes as another exception: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// A <c>null</c> at a key reads exactly as the key left out, for every key of the golden batch outside a map and for the model keys it lacks.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullLikeAbsentKey()
    {
        // Arrange
        var lines = GoldenLines();
        var mismatches = new List<string>();

        // Act
        foreach (var (index, path) in MemberPaths(lines).Where(variant => IsOutsideMap(variant.Path)))
        {
            var withNull = await OutcomeAsync(index, Apply(lines[index], path, "null"));
            var without = await OutcomeAsync(index, Apply(lines[index], path, null));

            if (withNull != without)
            {
                mismatches.Add($"line {index + 1} {Format(path)}: null gives [{withNull}], absent gives [{without}]");
            }
        }

        // Assert
        Assert.IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    /// <summary>
    /// A <c>null</c> as the first element of a list reads exactly as an empty object, for every list of the golden batch.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullListElementLikeEmptyObject()
    {
        // Arrange
        var lines = GoldenLines();
        var mismatches = new List<string>();
        var checkedLists = 0;

        // Act
        foreach (var (index, path) in ElementPaths(lines))
        {
            var withNull = await OutcomeAsync(index, Apply(lines[index], path, "null"));
            var withObject = await OutcomeAsync(index, Apply(lines[index], path, "{}"));

            checkedLists++;

            if (withNull != withObject)
            {
                mismatches.Add($"line {index + 1} {Format(path)}: null gives [{withNull}], {{}} gives [{withObject}]");
            }
        }

        // Assert
        Assert.AreEqual(8, checkedLists, "lists in the golden batch");
        Assert.IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    /// <summary>
    /// A <c>null</c> as the value of a map entry reads exactly as the zero value of the map's value type, for every map of the golden batch.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullMapValueLikeZeroValue()
    {
        // Arrange
        var lines = GoldenLines();
        var mismatches = new List<string>();
        var checkedEntries = 0;

        // Act
        foreach (var (index, path) in MemberPaths(lines).Where(variant => IsMapEntry(variant.Path)))
        {
            var zero = path[1] is "status" ? "0" : "\"\"";
            var withNull = await OutcomeAsync(index, Apply(lines[index], path, "null"));
            var withZero = await OutcomeAsync(index, Apply(lines[index], path, zero));

            checkedEntries++;

            if (withNull != withZero)
            {
                mismatches.Add($"line {index + 1} {Format(path)}: null gives [{withNull}], {zero} gives [{withZero}]");
            }
        }

        // Assert
        Assert.AreEqual(5, checkedEntries, "map entries in the golden batch");
        Assert.IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    /// <summary>
    /// A <c>null</c> element of a list is decoded as an element with every field at its zero value, never as a null reference.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullThreadAsEmptyThread()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, RecordLine(RecordKind.MariaDbStatus, """{"availability":"up","threads":[null],"complete":true}"""));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var record = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        var status = record?.Data as MariaDbStatus;

        Assert.IsNotNull(status, "a MariaDB status is decoded");
        Assert.IsNotNull(status.Threads, "the thread list is decoded");
        Assert.HasCount(1, status.Threads, "one thread");

        var thread = status.Threads[0];

        Assert.IsNotNull(thread, "the thread is not a null reference");
        Assert.AreEqual(0UL, thread.Id, "thread id");
        Assert.AreEqual(0UL, thread.TimeSeconds, "thread time");
        Assert.AreEqual(string.Empty, thread.User, "thread user");
        Assert.AreEqual(string.Empty, thread.Host, "thread host");
        Assert.AreEqual(string.Empty, thread.Db, "thread db");
        Assert.AreEqual(string.Empty, thread.Command, "thread command");
        Assert.AreEqual(string.Empty, thread.State, "thread state");
        Assert.AreEqual(string.Empty, thread.Info, "thread info");
    }

    /// <summary>
    /// A <c>null</c> label value is decoded as an empty value and keeps its key.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullLabelAsEmptyLabel()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, RecordLine(RecordKind.Metric, """{"name":"free","value":1,"labels":{"device":null,"mount":"/var"}}"""));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var record = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        var metric = record?.Data as MetricPoint;

        Assert.IsNotNull(metric, "a metric is decoded");
        Assert.IsNotNull(metric.Labels, "the labels are decoded");
        Assert.HasCount(2, metric.Labels, "both labels are kept");
        Assert.AreEqual(string.Empty, metric.Labels["device"], "the null label is empty");
        Assert.AreEqual("/var", metric.Labels["mount"], "the other label is read");
    }

    /// <summary>
    /// A header field that is <c>null</c> reads as absent, and the header's validation names the field.
    /// </summary>
    /// <param name="key">The header key that is set to <c>null</c></param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("agent_id", "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit")]
    [DataRow("boot_id", "must be a lower-case UUID")]
    [DataRow("mode", "must be live or backfill")]
    public async Task BatchDecoderRefusesNullHeaderField(string key, string reason)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(Apply(BatchBuilder.Header, [key], "null"), BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "null header field");

        // Assert
        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
        Assert.AreEqual(key, exception.FieldError?.Field, "field");
        Assert.AreEqual(reason, exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// An optional header field that is <c>null</c> reads as absent: the minor version as 0 and the clock offset as none.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReadsNullOptionalHeaderFieldAsAbsent()
    {
        // Arrange
        var header = Apply(Apply(BatchBuilder.Header, ["format_minor"], "3"), ["clock_offset_ns"], "5");
        using var withValues = BatchBuilder.Gzip(header, BatchBuilder.Metric(1));
        using var withNulls = BatchBuilder.Gzip(Apply(Apply(header, ["format_minor"], "null"), ["clock_offset_ns"], "null"), BatchBuilder.Metric(1));
        using var valuesDecoder = await BatchDecoder.OpenAsync(withValues, null, TestContext.CancellationToken);

        // Act
        using var nullsDecoder = await BatchDecoder.OpenAsync(withNulls, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3, valuesDecoder.Header.FormatMinor, "the minor version is read when given");
        Assert.AreEqual(5L, valuesDecoder.Header.ClockOffsetNs, "the clock offset is read when given");
        Assert.AreEqual(0, nullsDecoder.Header.FormatMinor, "a null minor version reads as 0");
        Assert.IsNull(nullsDecoder.Header.ClockOffsetNs, "a null clock offset reads as none");
        Assert.AreEqual("agent-1", nullsDecoder.Header.AgentId, "the other fields are read");
    }

    /// <summary>
    /// A record field of the envelope that is <c>null</c> reads as absent, and the record's validation names the field.
    /// </summary>
    /// <param name="key">The envelope key that is set to <c>null</c></param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("source", "required")]
    [DataRow("seq", "must be greater than 0 for origin agent")]
    [DataRow("captured_at", "required")]
    public async Task BatchDecoderRefusesNullEnvelopeField(string key, string reason)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, Apply(BatchBuilder.Metric(1), [key], "null"));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "null envelope field");

        // Assert
        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual(2, exception.Line, "line");
        Assert.AreEqual(key, exception.FieldError?.Field, "field");
        Assert.AreEqual(reason, exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// A payload field, list element or map value that is <c>null</c> reads as absent, and the payload's validation names the field.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="data">The payload</param>
    /// <param name="field">The expected field path</param>
    /// <param name="reason">The expected reason</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(RecordKind.Metric, """{"name":null,"value":1}""", "data.name", "required")]
    [DataRow(RecordKind.LogLine, """{"log":null,"message":"m"}""", "data.log", "required")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[null]}""", "data.processes[0].pid", "must be greater than 0")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"programs":[null]}""", "data.programs[0].program", "required")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[{"pid":1,"command":null}]}""", "data.processes[0].command", "required")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"states":[{"proto":null,"count":1}]}""", "data.states[0].proto", "unknown value")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"processes":[{"pid":1,"command":null,"count":1}]}""", "data.processes[0].command", "required")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"remotes":[{"addr":null,"count":1}]}""", "data.remotes[0].addr", "invalid address")]
    [DataRow(RecordKind.ServiceState, """{"unit":null,"load_state":"loaded","active_state":"active"}""", "data.unit", "required")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"down","status":{"A":null},"complete":true}""", "data.availability", "status, variables and threads must be empty unless up")]
    [DataRow(RecordKind.KernelEvent, """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":null}}""", "data.oom_kill.victim_command", "required")]
    [DataRow(RecordKind.KernelEvent, """{"type":"boot","boot":{"boot_id":null}}""", "data.boot.boot_id", "required")]
    [DataRow(RecordKind.Gap, """{"from":null,"to":"2026-03-01T10:00:00Z","cause":"unknown"}""", "data.from", "required")]
    public async Task BatchDecoderRefusesNullPayloadField(string kind, string data, string field, string reason)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, RecordLine(kind, data));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "null payload field");

        // Assert
        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual(2, exception.Line, "line");
        Assert.AreEqual(field, exception.FieldError?.Field, "field");
        Assert.AreEqual(reason, exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// A payload field, list element or map value that is <c>null</c> is accepted where the field is optional and reads as its zero value.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="data">The payload</param>
    /// <param name="expected">The JSON the decoded payload is written as</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":null}""", """{"name":"cpu","value":0,"unit":"","Kind":"metric"}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"unit":null}""", """{"name":"cpu","value":1,"unit":"","Kind":"metric"}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"labels":{"device":null,"mount":"/var"}}""", """{"name":"cpu","value":1,"unit":"","labels":{"device":"","mount":"/var"},"Kind":"metric"}""")]
    [DataRow(RecordKind.LogLine, """{"log":"l","host":null,"program":null,"event":null,"message":null}""", """{"log":"l","host":"","program":"","message":"","event":"","Kind":"log_line"}""")]
    [DataRow(RecordKind.LogLine, """{"log":"l","message":"m","pid":null,"truncated":null,"priority":null}""", """{"log":"l","host":"","program":"","message":"m","event":"","Kind":"log_line"}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[{"pid":1,"command":"c","user":null,"cmdline":null,"state":null,"started_at":null}]}""", """{"complete":true,"processes":[{"pid":1,"user":"","command":"c","cmdline":"","state":"","cpu_percent":0,"rss_bytes":0}],"Kind":"process_snapshot"}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[{"pid":1,"command":"c","ppid":null}]}""", """{"complete":true,"processes":[{"pid":1,"user":"","command":"c","cmdline":"","state":"","cpu_percent":0,"rss_bytes":0}],"Kind":"process_snapshot"}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"states":[{"proto":"udp","state":null,"count":1}]}""", """{"complete":true,"states":[{"proto":"udp","state":"","count":1}],"Kind":"connection_snapshot"}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":null}]}""", """{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":""}],"Kind":"connection_snapshot"}""")]
    [DataRow(RecordKind.ServiceState, """{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":null,"active_enter_at":null,"restarts":null}""", """{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":"","restarts":0,"Kind":"service_state"}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","threads":[null],"complete":true}""", """{"availability":"up","threads":[{"id":0,"user":"","host":"","db":"","command":"","time_seconds":0,"state":"","info":""}],"complete":true,"Kind":"mariadb_status"}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","variables":{"A":null},"complete":true}""", """{"availability":"up","variables":{"A":""},"complete":true,"Kind":"mariadb_status"}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","status":{"A":null},"complete":true}""", """{"availability":"up","status":{"A":0},"complete":true,"Kind":"mariadb_status"}""")]
    [DataRow(RecordKind.KernelEvent, """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"},"message":null}""", """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"},"message":"","Kind":"kernel_event"}""")]
    [DataRow(RecordKind.Gap, """{"from":"2026-03-01T09:00:00Z","to":"2026-03-01T10:00:00Z","cause":"unknown","collector":null}""", """{"from":"2026-03-01T09:00:00+00:00","to":"2026-03-01T10:00:00+00:00","cause":"unknown","collector":"","Kind":"gap"}""")]
    public async Task BatchDecoderAcceptsNullOfOptionalPayloadField(string kind, string data, string expected)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, RecordLine(kind, data));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var record = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(record?.Data, "a payload is decoded");
        Assert.AreEqual(expected, PayloadRegistry.Serialize(record.Data), "payload as written");
        Assert.IsNull(FindNull(record.Data, "data"), "no string, list element or map value is a null reference");
    }

    /// <summary>
    /// A value of the wrong type is still malformed, <c>null</c> or not.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="data">The payload</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(RecordKind.Metric, """{"name":5}""")]
    [DataRow(RecordKind.LogLine, """{"log":"l","message":"m","pid":"1"}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":{}}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[5]}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"labels":{"a":5}}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","status":{"A":"x"},"complete":true}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"remotes":[{"addr":"null","count":1}]}""")]
    public async Task BatchDecoderRefusesValueOfWrongType(string kind, string data)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, RecordLine(kind, data));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "value of the wrong type");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(2, exception.Line, "line");
    }

    /// <summary>
    /// Builds a record line of the given kind with a valid envelope.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="data">The payload JSON</param>
    /// <returns>The line</returns>
    private static string RecordLine(string kind, string data)
    {
        return $$"""{"kind":"{{kind}}","data":{{data}},"source":"s","seq":1,"captured_at":"2026-10-01T10:00:00Z"}""";
    }

    /// <summary>
    /// Reads the lines of the golden batch: the header and the nine records.
    /// </summary>
    /// <returns>The lines</returns>
    private static string[] GoldenLines()
    {
        return File.ReadAllLines(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"));
    }

    /// <summary>
    /// Lists the path of every object member at any depth of every line, plus the model keys the golden batch lacks:
    /// <c>clock_offset_ns</c> of the header, <c>ppid</c> of the first process, <c>boot</c> of the OOM kill and <c>oom_kill</c> of the boot.
    /// </summary>
    /// <param name="lines">The lines of the golden batch</param>
    /// <returns>The line index and the path of each member</returns>
    private static List<(int Line, object[] Path)> MemberPaths(string[] lines)
    {
        var result = new List<(int Line, object[] Path)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var members = new List<object[]>();

            CollectPaths(JsonNode.Parse(lines[index]), [], members, []);
            result.AddRange(members.Select(path => (index, path)));
        }

        result.Add((0, ["clock_offset_ns"]));
        result.Add((2, ["data", "processes", 0, "ppid"]));
        result.Add((6, ["data", "boot"]));
        result.Add((7, ["data", "oom_kill"]));

        return result;
    }

    /// <summary>
    /// Lists the path of the first element of every list of objects at any depth of every line.
    /// </summary>
    /// <param name="lines">The lines of the golden batch</param>
    /// <returns>The line index and the path of each element</returns>
    private static List<(int Line, object[] Path)> ElementPaths(string[] lines)
    {
        var result = new List<(int Line, object[] Path)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var elements = new List<object[]>();

            CollectPaths(JsonNode.Parse(lines[index]), [], [], elements);
            result.AddRange(elements.Select(path => (index, path)));
        }

        return result;
    }

    /// <summary>
    /// Collects the paths of the object members and of the first elements of the arrays of objects below a node.
    /// </summary>
    /// <param name="node">The node</param>
    /// <param name="path">The path of the node</param>
    /// <param name="members">Receives the path of each object member</param>
    /// <param name="elements">Receives the path of the first element of each array of objects</param>
    private static void CollectPaths(JsonNode? node, List<object> path, List<object[]> members, List<object[]> elements)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var pair in jsonObject)
            {
                path.Add(pair.Key);
                members.Add(path.ToArray());
                CollectPaths(pair.Value, path, members, elements);
                path.RemoveAt(path.Count - 1);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                path.Add(index);

                if (index == 0 && jsonArray[index] is JsonObject)
                {
                    elements.Add(path.ToArray());
                }

                CollectPaths(jsonArray[index], path, members, elements);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>
    /// Tells whether a path is an entry of one of the maps of a payload: <c>labels</c>, <c>status</c> or <c>variables</c>.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns><c>true</c> for a map entry</returns>
    private static bool IsMapEntry(object[] path)
    {
        return path is ["data", "labels" or "status" or "variables", string];
    }

    /// <summary>
    /// Tells whether a path is not an entry of one of the maps of a payload.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns><c>true</c> for every path but a map entry</returns>
    private static bool IsOutsideMap(object[] path)
    {
        return path is not ["data", "labels" or "status" or "variables", string];
    }

    /// <summary>
    /// Formats a path for a message.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns>The path with dots</returns>
    private static string Format(object[] path)
    {
        return string.Join('.', path);
    }

    /// <summary>
    /// Replaces the value at a path of a JSON line or removes the key.
    /// </summary>
    /// <param name="line">The JSON line</param>
    /// <param name="path">The path of the value: object keys and array indexes</param>
    /// <param name="replacement">The JSON text of the new value, or <c>null</c> to remove the key</param>
    /// <returns>The changed line</returns>
    private static string Apply(string line, object[] path, string? replacement)
    {
        var root = JsonNode.Parse(line) ?? throw new InvalidOperationException("the line is null");
        var container = root;

        foreach (var segment in path[..^1])
        {
            container = (segment is int position ? container.AsArray()[position] : container.AsObject()[(string)segment]) ?? throw new InvalidOperationException($"no value at {Format(path)}");
        }

        if (path[^1] is int last)
        {
            container.AsArray()[last] = JsonNode.Parse(replacement ?? "null");
        }
        else if (replacement is null)
        {
            container.AsObject().Remove((string)path[^1]);
        }
        else
        {
            container.AsObject()[(string)path[^1]] = JsonNode.Parse(replacement);
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// Describes a decoded header as text.
    /// </summary>
    /// <param name="header">The header</param>
    /// <returns>The description</returns>
    private static string DescribeHeader(WireHeader header)
    {
        return $"header {header.FormatMajor} {header.FormatMinor} {header.AgentId} {header.BootId} {header.ClockOffsetNs} {header.Mode}";
    }

    /// <summary>
    /// Describes a decoded record as text; a string, list element or map value that is a null reference is part of the description.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The description</returns>
    private static string DescribeRecord(DataRecord? record)
    {
        if (record?.Data is null)
        {
            return "no record";
        }

        var missing = FindNull(record.Data, "data");

        return missing is null
                   ? $"record {record.Source} {record.Seq} {record.CapturedAt:O} {record.Kind} {PayloadRegistry.Serialize(record.Data)}"
                   : $"null reference at {missing}";
    }

    /// <summary>
    /// Looks for a string, a list element or a map value that is a null reference below an object.
    /// </summary>
    /// <param name="value">The object</param>
    /// <param name="path">The path of the object</param>
    /// <returns>The path of the first null reference, or <c>null</c> when there is none</returns>
    private static string? FindNull(object value, string path)
    {
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(candidate => candidate.GetMethod is not null && candidate.GetIndexParameters().Length == 0))
        {
            var found = FindNullIn(property.GetValue(value), property.PropertyType, $"{path}.{property.Name}");

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for a null reference in the value of a property.
    /// </summary>
    /// <param name="child">The value</param>
    /// <param name="type">The declared type of the property</param>
    /// <param name="path">The path of the value</param>
    /// <returns>The path of the first null reference, or <c>null</c> when there is none</returns>
    private static string? FindNullIn(object? child, Type type, string path)
    {
        switch (child)
        {
            case null:
                {
                    return type == typeof(string) ? path : null;
                }

            case IDictionary dictionary:
                {
                    return dictionary.Keys.Cast<object>().Select(key => dictionary[key] is { } entry ? FindNullIn(entry, entry.GetType(), $"{path}[{key}]") : $"{path}[{key}]").FirstOrDefault(found => found is not null);
                }

            case IList list:
                {
                    return Enumerable.Range(0, list.Count).Select(position => list[position] is { } element ? FindNullIn(element, element.GetType(), $"{path}[{position}]") : $"{path}[{position}]").FirstOrDefault(found => found is not null);
                }

            case string:
            case ValueType:
                {
                    return null;
                }

            default:
                {
                    return child.GetType().Namespace is { } scope && scope.StartsWith("Vandox", StringComparison.Ordinal) ? FindNull(child, path) : null;
                }
        }
    }

    /// <summary>
    /// Decodes a header line (line index 0) or a record line behind a valid header and describes the outcome as text.
    /// A <see cref="WireException"/> is part of the outcome, any other exception is not.
    /// </summary>
    /// <param name="index">The index of the line in the golden batch</param>
    /// <param name="line">The line</param>
    /// <returns>The outcome</returns>
    private async Task<string> OutcomeAsync(int index, string line)
    {
        using var input = index == 0 ? BatchBuilder.Gzip(line, BatchBuilder.Metric(1)) : BatchBuilder.Gzip(BatchBuilder.Header, line);

        try
        {
            using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

            return index == 0 ? DescribeHeader(decoder.Header) : DescribeRecord(await decoder.NextAsync(TestContext.CancellationToken));
        }
        catch (WireException exception)
        {
            return $"fail {exception.Kind} line {exception.Line} field {exception.FieldError?.Field} reason {exception.FieldError?.Reason}";
        }
    }

    /// <summary>
    /// Opens a batch and reads it to the end.
    /// </summary>
    /// <param name="input">The compressed stream</param>
    /// <returns>A task that completes at the end of the batch</returns>
    private async Task OpenAndReadAllAsync(Stream input)
    {
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        await ReadAllAsync(decoder);
    }

    /// <summary>
    /// Reads records until the end.
    /// </summary>
    /// <param name="decoder">The decoder</param>
    /// <returns>A task that completes at the end of the batch</returns>
    private async Task ReadAllAsync(BatchDecoder decoder)
    {
        DataRecord? record;

        do
        {
            record = await decoder.NextAsync(TestContext.CancellationToken);
        }
        while (record is not null);
    }

    #endregion // Methods
}