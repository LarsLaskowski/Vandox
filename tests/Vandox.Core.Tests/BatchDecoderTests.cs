using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Vandox.Core.Model;
using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BatchDecoder"/>
/// </summary>
[TestClass]
public class BatchDecoderTests
{
    #region Constants

    /// <summary>
    /// The most bytes the decoder may allocate per byte of the record line, or of the batch for many short lines.
    /// </summary>
    private const int MaxAllocationFactor = 20;

    /// <summary>
    /// The most the decoder may take as a multiple of a plain parse of the same line.
    /// </summary>
    private const double MaxTimeFactor = 50;

    /// <summary>
    /// The text of an escape of a high surrogate, with its backslash.
    /// </summary>
    private const string HighSurrogateEscape = @"\ud800";

    /// <summary>
    /// The character U+00FF, which the ISO-8859-1 helpers write as the single raw byte FF.
    /// </summary>
    private const string InvalidByte = "\u00ff";

    /// <summary>
    /// The character U+00E9, which the ISO-8859-1 helpers write as the single raw byte E9.
    /// </summary>
    private const string LatinLetter = "\u00e9";

    #endregion // Constants

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
    [DataRow("[1]", WireErrorKind.Malformed)]
    [DataRow("""{"format_major":"1"}""", WireErrorKind.Malformed)]
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
        Assert.AreEqual(18, checkedLists, "lists in the golden batch");
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
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":null}""", """{"name":"cpu","value":0,"unit":""}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"unit":null}""", """{"name":"cpu","value":1,"unit":""}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"labels":{"device":null,"mount":"/var"}}""", """{"name":"cpu","value":1,"unit":"","labels":{"device":"","mount":"/var"}}""")]
    [DataRow(RecordKind.LogLine, """{"log":"l","host":null,"program":null,"event":null,"message":null}""", """{"log":"l","host":"","program":"","message":"","event":""}""")]
    [DataRow(RecordKind.LogLine, """{"log":"l","message":"m","pid":null,"truncated":null,"priority":null}""", """{"log":"l","host":"","program":"","message":"m","event":""}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[{"pid":1,"command":"c","user":null,"cmdline":null,"state":null,"started_at":null}]}""", """{"complete":true,"processes":[{"pid":1,"user":"","command":"c","cmdline":"","state":"","cpu_percent":0,"rss_bytes":0}]}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"processes":[{"pid":1,"command":"c","ppid":null}]}""", """{"complete":true,"processes":[{"pid":1,"user":"","command":"c","cmdline":"","state":"","cpu_percent":0,"rss_bytes":0}]}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"states":[{"proto":"udp","state":null,"count":1}]}""", """{"complete":true,"states":[{"proto":"udp","state":"","count":1}]}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":null}]}""", """{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":""}]}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"listener_check":null}""", """{"complete":true}""")]
    [DataRow(RecordKind.ServiceState, """{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":null,"active_enter_at":null,"restarts":null}""", """{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":"","restarts":0}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","threads":[null],"complete":true}""", """{"availability":"up","threads":[{"id":0,"user":"","host":"","db":"","command":"","time_seconds":0,"state":"","info":""}],"complete":true}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","variables":{"A":null},"complete":true}""", """{"availability":"up","variables":{"A":""},"complete":true}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","status":{"A":null},"complete":true}""", """{"availability":"up","status":{"A":0},"complete":true}""")]
    [DataRow(RecordKind.KernelEvent, """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"},"message":null}""", """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"},"message":""}""")]
    [DataRow(RecordKind.Gap, """{"from":"2026-03-01T09:00:00Z","to":"2026-03-01T10:00:00Z","cause":"unknown","collector":null}""", """{"from":"2026-03-01T09:00:00+00:00","to":"2026-03-01T10:00:00+00:00","cause":"unknown","collector":""}""")]
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
    /// The deepest nesting of a line is 64.
    /// </summary>
    [TestMethod]
    public void WireFormatLimitsNestingTo64()
    {
        // Arrange
        var depth = WireFormat.MaxDepth;

        // Act
        var limit = depth;

        // Assert
        Assert.AreEqual(64, limit, "WireFormat.MaxDepth");
    }

    /// <summary>
    /// A header that is no JSON object and no <c>null</c> is malformed, not an unsupported version.
    /// </summary>
    /// <param name="header">The header line</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("[1]")]
    [DataRow("[]")]
    [DataRow("5")]
    [DataRow("\"x\"")]
    [DataRow("true")]
    public async Task BatchDecoderRefusesHeaderThatIsNoObject(string header)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(header, BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "header that is no object");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
        Assert.AreEqual("header is not a JSON object", exception.Reason, "reason");
    }

    /// <summary>
    /// A <c>format_major</c> that is no integer of 64 bits is malformed, not an unsupported version.
    /// </summary>
    /// <param name="major">The JSON text of the value</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("\"1\"")]
    [DataRow("\"2\"")]
    [DataRow("1.0")]
    [DataRow("1e0")]
    [DataRow("1E0")]
    [DataRow("10e-1")]
    [DataRow("2.5")]
    [DataRow("true")]
    [DataRow("{}")]
    [DataRow("[1]")]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("1e400")]
    public async Task BatchDecoderRefusesMajorThatIsNoInteger(string major)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(HeaderWithMajor(major), BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "major that is no integer");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
        Assert.AreEqual("format_major is not an integer", exception.Reason, "reason");
    }

    /// <summary>
    /// A <c>format_major</c> that is an integer of 64 bits other than 1 is an unsupported version, whatever its size.
    /// </summary>
    /// <param name="major">The JSON text of the value</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("null")]
    [DataRow("0")]
    [DataRow("-0")]
    [DataRow("-1")]
    [DataRow("2")]
    [DataRow("3000000000")]
    [DataRow("9223372036854775807")]
    [DataRow("-9223372036854775808")]
    public async Task BatchDecoderRefusesOtherMajorAsUnsupportedVersion(string major)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(HeaderWithMajor(major), BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "other major");

        // Assert
        Assert.AreEqual(WireErrorKind.UnsupportedVersion, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
    }

    /// <summary>
    /// The minor version is a 64-bit integer that is read back exactly.
    /// </summary>
    /// <param name="minor">The JSON text of the value</param>
    /// <param name="expected">The expected value</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("3000000000", 3000000000L)]
    [DataRow("9223372036854775807", long.MaxValue)]
    public async Task BatchDecoderReadsFormatMinorAsLong(string minor, long expected)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(HeaderWithMinor(minor), BatchBuilder.Metric(1));

        // Act
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(expected, decoder.Header.FormatMinor, "minor version");
    }

    /// <summary>
    /// A minor version beyond 64 bits or of another type is malformed.
    /// </summary>
    /// <param name="minor">The JSON text of the value</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("9223372036854775808")]
    [DataRow("\"0\"")]
    public async Task BatchDecoderRefusesMinorThatIsNoInteger(string minor)
    {
        // Arrange
        using var input = BatchBuilder.Gzip(HeaderWithMinor(minor), BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "minor that is no integer");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(1, exception.Line, "line");
    }

    /// <summary>
    /// A negative minor version is invalid and names its field.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesNegativeMinor()
    {
        // Arrange
        using var input = BatchBuilder.Gzip(HeaderWithMinor("-1"), BatchBuilder.Metric(1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken), "negative minor");

        // Assert
        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual("format_minor", exception.FieldError?.Field, "field");
        Assert.AreEqual("must not be negative", exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// The options every wire line is bound with match names ignoring ASCII case.
    /// </summary>
    [TestMethod]
    public void PayloadRegistryOptionsMatchNamesIgnoringCase()
    {
        // Arrange
        var options = PayloadRegistry.Options;

        // Act
        var insensitive = options.PropertyNameCaseInsensitive;

        // Assert
        Assert.IsTrue(insensitive, "property names are matched ignoring case");
    }

    /// <summary>
    /// A key in another ASCII case, also written with escapes, is the field, in the header, the envelope and the payload at any depth.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderBindsKeysIgnoringAsciiCase()
    {
        // Arrange
        const string header = """{"FORMAT_MAJOR":1,"Format_Minor":3,"AGENT_ID":"agent-1","Boot_ID":"0123abcd-0123-0123-0123-0123456789ab","MODE":"live"}""";
        const string metric = """{"KIND":"metric","Source":"host","SEQ":4,"Captured_At":"2026-10-01T10:00:00Z","DATA":{"NAME":"cpu","Value":1,"LABELS":{"Mount":"/var"}}}""";
        const string processes = """{"kind":"process_snapshot","source":"host","seq":5,"captured_at":"2026-10-01T10:00:00Z","data":{"Complete":true,"Processes":[{"PID":7,"Command":"x"}]}}""";
        const string escaped = "{\"\\u006bind\":\"metric\",\"\\u0053OURCE\":\"host\",\"seq\":6,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1}}";
        using var input = BatchBuilder.Gzip(header, metric, processes, escaped);
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var second = await decoder.NextAsync(TestContext.CancellationToken);
        var third = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3L, decoder.Header.FormatMinor, "minor version of the header");
        Assert.AreEqual("agent-1", decoder.Header.AgentId, "agent ID of the header");
        Assert.AreEqual(4UL, first?.Seq, "seq of the envelope");

        var metricPoint = first?.Data as MetricPoint;

        Assert.AreEqual("cpu", metricPoint?.Name, "name of the payload");
        Assert.AreEqual("/var", metricPoint?.Labels?["Mount"], "label of the payload keeps the case of its key");
        Assert.AreEqual(7, (second?.Data as ProcessSnapshot)?.Processes?[0].Pid, "pid of a list element");
        Assert.AreEqual("host", third?.Source, "source with an escaped key");
        Assert.AreEqual(RecordKind.Metric, third?.Kind, "kind with an escaped key");
    }

    /// <summary>
    /// A key that is not ASCII once unescaped is malformed in the record and in the header, with the error naming the rule and not the key.
    /// </summary>
    /// <param name="key">The JSON text of the key, escapes included, without the quotes</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("\\u212aind")]
    [DataRow("\\u017feq")]
    [DataRow("agent_\\u0131d")]
    [DataRow("\\u00e9")]
    [DataRow("\\ud83d\\ude00")]
    [DataRow("\\ud800")]
    [DataRow("a\\udc00")]
    [DataRow("\\ud800\\u0041")]
    [DataRow("\\u0080")]
    public async Task BatchDecoderRefusesNonAsciiKey(string key)
    {
        // Arrange
        using var inRecord = BatchBuilder.Gzip(BatchBuilder.Header, RecordWithKey(key));
        using var inHeader = BatchBuilder.Gzip(HeaderWithKey(key), BatchBuilder.Metric(1));

        // Act
        var record = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(inRecord), "key in the record");
        var header = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(inHeader, null, TestContext.CancellationToken), "key in the header");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, record.Kind, "record error class");
        Assert.AreEqual(2, record.Line, "record line");
        Assert.AreEqual("key not ASCII", record.Reason, "record reason");
        Assert.AreEqual(WireErrorKind.Malformed, header.Kind, "header error class");
        Assert.AreEqual(1, header.Line, "header line");
        Assert.AreEqual("key not ASCII", header.Reason, "header reason");
    }

    /// <summary>
    /// A key with a raw non-ASCII character is malformed in the record and in the header.
    /// </summary>
    /// <param name="codePoint">The code point of the character</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(0x212A)]
    [DataRow(0x017F)]
    [DataRow(0x0131)]
    [DataRow(0x00E9)]
    [DataRow(0x1F600)]
    public async Task BatchDecoderRefusesRawNonAsciiKey(int codePoint)
    {
        // Arrange
        var key = $"k{char.ConvertFromUtf32(codePoint)}y";
        using var inRecord = BatchBuilder.Gzip(BatchBuilder.Header, RecordWithKey(key));
        using var inHeader = BatchBuilder.Gzip(HeaderWithKey(key), BatchBuilder.Metric(1));

        // Act
        var record = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(inRecord), "raw key in the record");
        var header = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(inHeader, null, TestContext.CancellationToken), "raw key in the header");

        // Assert
        Assert.AreEqual("key not ASCII", record.Reason, "record reason");
        Assert.AreEqual(WireErrorKind.Malformed, record.Kind, "record error class");
        Assert.AreEqual("key not ASCII", header.Reason, "header reason");
        Assert.AreEqual(WireErrorKind.Malformed, header.Kind, "header error class");
    }

    /// <summary>
    /// A key with bytes that are no valid UTF-8 is malformed in the record and in the header, and only a <see cref="WireException"/> leaves the decoder.
    /// </summary>
    /// <param name="hex">The bytes of the key in hexadecimal</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("FF")]
    [DataRow("6B696EFF64")]
    [DataRow("C0AF")]
    [DataRow("EDA080")]
    [DataRow("C3")]
    public async Task BatchDecoderRefusesKeyOfInvalidUtf8(string hex)
    {
        // Arrange
        var key = Convert.FromHexString(hex);
        using var inRecord = BatchBuilder.Gzip(RawLines(Encoding.UTF8.GetBytes(BatchBuilder.Header), RecordBytesWithKey(key)));
        using var inHeader = BatchBuilder.Gzip(RawLines(HeaderBytesWithKey(key), Encoding.UTF8.GetBytes(BatchBuilder.Metric(1))));

        // Act
        var record = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(inRecord), "invalid UTF-8 key in the record");
        var header = await Assert.ThrowsExactlyAsync<WireException>(() => BatchDecoder.OpenAsync(inHeader, null, TestContext.CancellationToken), "invalid UTF-8 key in the header");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, record.Kind, "record error class");
        Assert.AreEqual(2, record.Line, "record line");
        Assert.AreEqual(WireErrorKind.Malformed, header.Kind, "header error class");
        Assert.AreEqual(1, header.Line, "header line");
    }

    /// <summary>
    /// The error text names the broken rule and never the key.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to break the header, <c>false</c> to break the record</param>
    /// <param name="duplicate"><c>true</c> to repeat a key, <c>false</c> to use a key that is not ASCII</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task BatchDecoderErrorNamesNoKey(bool inHeader, bool duplicate)
    {
        // Arrange
        var members = duplicate ? "\"SENTINELKEY\":1,\"SENTINELKEY\":2" : $"\"SENTINELK{char.ConvertFromUtf32(0xC9)}Y\":1";
        var reason = duplicate ? "duplicate key" : "key not ASCII";
        var line = inHeader ? 1 : 2;
        using var input = inHeader
                              ? BatchBuilder.Gzip(HeaderWithMembers(members), BatchBuilder.Metric(1))
                              : BatchBuilder.Gzip(BatchBuilder.Header, RecordWithMembers(members));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), "broken key rule");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(reason, exception.Reason, "reason");
        Assert.AreEqual($"wire: line {line}: {reason}", exception.Message, "exact message");
        Assert.DoesNotContain("SENTINEL", exception.Message, "the key stays out of the message");
    }

    /// <summary>
    /// A byte that is no valid UTF-8 anywhere in a line is malformed with the fixed reason, in the header and in the record.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesInvalidUtf8InEveryPlace()
    {
        // Arrange
        var places = TextPlaces(InvalidByte);

        // Act / Assert
        foreach (var place in places)
        {
            using var input = GzipLatin1(place.Header, place.Record);
            var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), place.Name);

            Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, $"{place.Name}: error class");
            Assert.AreEqual(place.Line, exception.Line, $"{place.Name}: line");
            Assert.AreEqual("line not valid UTF-8", exception.Reason, $"{place.Name}: reason");
            Assert.AreEqual($"wire: line {place.Line}: line not valid UTF-8", exception.Message, $"{place.Name}: exact message");
        }
    }

    /// <summary>
    /// An unpaired surrogate escape anywhere in a line is malformed with the fixed reason, in the header and in the record.
    /// </summary>
    /// <param name="escape">The escape text, the digits after the <c>\u</c></param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("d800")]
    [DataRow("dc00")]
    [DataRow("D800")]
    public async Task BatchDecoderRefusesUnpairedSurrogateEscapeInEveryPlace(string escape)
    {
        // Arrange
        var value = $@"\u{escape}";
        var places = TextPlaces(value);

        Assert.Contains(@"\u", value, "the escape keeps its backslash");

        // Act / Assert
        foreach (var place in places)
        {
            using var input = GzipLatin1(place.Header, place.Record);
            var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), place.Name);

            Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, $"{place.Name}: error class");
            Assert.AreEqual(place.Line, exception.Line, $"{place.Name}: line");
            Assert.AreEqual("unpaired surrogate escape", exception.Reason, $"{place.Name}: reason");
            Assert.AreEqual($"wire: line {place.Line}: unpaired surrogate escape", exception.Message, $"{place.Name}: exact message");
        }
    }

    /// <summary>
    /// Valid escapes of a surrogate pair, of the replacement character and of a noncharacter, and an escaped backslash before the text of a surrogate, are accepted.
    /// </summary>
    /// <param name="first">The four hex digits of the first escape</param>
    /// <param name="second">The four hex digits of the second escape, or empty for none</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow("d83d", "de00")]
    [DataRow("D83D", "DE00")]
    [DataRow("D83d", "De00")]
    [DataRow("fffd", "")]
    [DataRow("ffff", "")]
    public async Task BatchDecoderAcceptsValidEscapes(string first, string second)
    {
        // Arrange
        var message = second.Length == 0 ? $@"\u{first}" : $@"\u{first}\u{second}";
        var escapedBackslash = $@"\{HighSurrogateEscape}";
        using var input = GzipLatin1(BatchBuilder.Header, LogLine(message, 1), LogLine(escapedBackslash, 2));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var firstRecord = await decoder.NextAsync(TestContext.CancellationToken);
        var secondRecord = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1UL, firstRecord?.Seq, "the record with the escape is read");
        Assert.AreEqual(2UL, secondRecord?.Seq, "the record with the escaped backslash is read");
    }

    /// <summary>
    /// The records before a line with an invalid byte are returned, then the error follows at its line.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReturnsRecordsBeforeInvalidUtf8ThenFails()
    {
        // Arrange
        using var input = GzipLatin1(BatchBuilder.Header, LogLine("a", 1), LogLine($"a{InvalidByte}b", 2));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "invalid byte");

        // Assert
        Assert.AreEqual(1UL, first?.Seq, "the record before the line is returned");
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(3, exception.Line, "line");
        Assert.AreEqual("line not valid UTF-8", exception.Reason, "reason");
    }

    /// <summary>
    /// The error text of the text rule carries nothing of the line, for both reasons, in the header and in the record.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to break the header, <c>false</c> to break the record</param>
    /// <param name="invalidByte"><c>true</c> to use an invalid byte, <c>false</c> to use an unpaired surrogate escape</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task BatchDecoderTextErrorNamesNoContent(bool inHeader, bool invalidByte)
    {
        // Arrange
        var broken = invalidByte ? $"SENTINELTEXT{InvalidByte}" : $"SENTINELTEXT{HighSurrogateEscape}";
        var reason = invalidByte ? "line not valid UTF-8" : "unpaired surrogate escape";
        var line = inHeader ? 1 : 2;
        using var input = inHeader
                              ? GzipLatin1(HeaderWithMembers($"\"x\":\"{broken}\""), BatchBuilder.Metric(1))
                              : GzipLatin1(BatchBuilder.Header, LogLine(broken, 1));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), "broken text");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual($"wire: line {line}: {reason}", exception.Message, "exact message");
        Assert.DoesNotContain("SENTINEL", exception.Message, "the text of the line stays out of the message");
    }

    /// <summary>
    /// A line that is not one complete JSON value keeps its syntax error, whatever bytes it holds: the text rule is never its reason.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to break the header line, <c>false</c> to break the record line</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchDecoderKeepsSyntaxErrorBeforeTextRule(bool inHeader)
    {
        // Arrange
        var complete = inHeader ? BatchBuilder.Header : BatchBuilder.Metric(1);
        var lines = new Dictionary<string, string>
                    {
                        ["cut short inside a string after FF"] = $"{{\"kind\":\"log_line\",\"x\":\"a{InvalidByte}",
                        ["cut short after a string holding FF"] = $"{{\"kind\":\"log_line\",\"x\":\"a{InvalidByte}\"",
                        ["cut short inside a string after a surrogate escape"] = $"{{\"kind\":\"log_line\",\"x\":\"a{HighSurrogateEscape}",
                        ["FF after the object"] = $"{complete}{InvalidByte}",
                        ["a surrogate escape string after the object"] = $"{complete} \"{HighSurrogateEscape}\""
                    };
        var expected = inHeader ? "header is not valid JSON" : "record is not valid JSON";

        // Act / Assert
        foreach (var (name, line) in lines)
        {
            using var input = inHeader ? GzipLatin1(line, BatchBuilder.Metric(1)) : GzipLatin1(BatchBuilder.Header, line);
            var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), name);

            Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, $"{name}: error class");
            Assert.AreEqual(inHeader ? 1 : 2, exception.Line, $"{name}: line");
            Assert.AreEqual(expected, exception.Reason, $"{name}: reason");
        }
    }

    /// <summary>
    /// The key rules run before the text rule: a bad key is reported as the key, whatever the values hold.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to break the header, <c>false</c> to break the record</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchDecoderReportsKeyRulesBeforeTextRule(bool inHeader)
    {
        // Arrange
        var members = new Dictionary<string, (string Members, string Reason)>
                      {
                          ["non-ASCII key and FF in a value"] = ($"\"k{LatinLetter}y\":1,\"x\":\"{InvalidByte}\"", "key not ASCII"),
                          ["non-ASCII key and a surrogate escape in a value"] = ($"\"k{LatinLetter}y\":1,\"x\":\"{HighSurrogateEscape}\"", "key not ASCII"),
                          ["duplicate key and a surrogate escape in a value"] = ($"\"a\":1,\"a\":2,\"x\":\"{HighSurrogateEscape}\"", "duplicate key"),
                          ["duplicate key and FF in a value"] = ($"\"a\":1,\"a\":2,\"x\":\"{InvalidByte}\"", "duplicate key"),
                          ["key of invalid UTF-8"] = ($"\"k{InvalidByte}y\":1", "key not ASCII"),
                          ["key with a surrogate escape"] = ($"\"k{HighSurrogateEscape}y\":1", "key not ASCII")
                      };

        // Act / Assert
        foreach (var (name, (text, reason)) in members)
        {
            using var input = inHeader
                                  ? GzipLatin1(HeaderWithMembers(text), BatchBuilder.Metric(1))
                                  : GzipLatin1(BatchBuilder.Header, RecordWithMembers(text));
            var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), name);

            Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, $"{name}: error class");
            Assert.AreEqual(reason, exception.Reason, $"{name}: reason");
        }
    }

    /// <summary>
    /// A line with an invalid byte and an unpaired surrogate escape gives the byte reason, in either order.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReportsInvalidUtf8BeforeUnpairedEscape()
    {
        // Arrange
        foreach (var message in new[] { $"{InvalidByte}{HighSurrogateEscape}", $"{HighSurrogateEscape}{InvalidByte}" })
        {
            using var input = GzipLatin1(BatchBuilder.Header, LogLine(message, 1));

            // Act
            var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), "both violations");

            // Assert
            Assert.AreEqual("line not valid UTF-8", exception.Reason, "reason");
        }
    }

    /// <summary>
    /// A line nested 64 deep is accepted, in the record and in the header, also inside an unknown key.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to nest the header, <c>false</c> to nest the record</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchDecoderAcceptsNestingOf64(bool inHeader)
    {
        // Arrange
        var nested = WireShapes.Arrays(WireFormat.MaxDepth - 1);
        using var input = inHeader
                              ? BatchBuilder.Gzip(HeaderWithMembers($"\"x\":{nested}"), BatchBuilder.Metric(1))
                              : BatchBuilder.Gzip(BatchBuilder.Header, WireShapes.Record(1, nested));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var record = await decoder.NextAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1UL, record?.Seq, "the record behind a line nested 64 deep is read");
    }

    /// <summary>
    /// A line nested 65 deep is malformed, in the record and in the header, also inside an unknown key.
    /// </summary>
    /// <param name="inHeader"><c>true</c> to nest the header, <c>false</c> to nest the record</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchDecoderRefusesNestingOf65(bool inHeader)
    {
        // Arrange
        var nested = WireShapes.Arrays(WireFormat.MaxDepth);
        using var input = inHeader
                              ? BatchBuilder.Gzip(HeaderWithMembers($"\"x\":{nested}"), BatchBuilder.Metric(1))
                              : BatchBuilder.Gzip(BatchBuilder.Header, WireShapes.Record(1, nested));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => OpenAndReadAllAsync(input), "nesting of 65");

        // Assert
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(inHeader ? 1 : 2, exception.Line, "line");
        Assert.AreEqual(inHeader ? "header is not valid JSON" : "record is not valid JSON", exception.Reason, "reason");
    }

    /// <summary>
    /// The records before a line with a repeated key are returned, then the error follows at its line and stays.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderReturnsRecordsBeforeDuplicateKeyThenFails()
    {
        // Arrange
        const string repeated = """{"kind":"metric","source":"host","seq":2,"seq":2,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"cpu","value":1.5}}""";
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, BatchBuilder.Metric(1), repeated, BatchBuilder.Metric(3));
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var first = await decoder.NextAsync(TestContext.CancellationToken);
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "repeated key");
        var again = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "sticky error");

        // Assert
        Assert.AreEqual(1UL, first?.Seq, "the record before the line is returned");
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual(3, exception.Line, "line");
        Assert.AreSame(exception, again, "the error is sticky");
    }

    /// <summary>
    /// Two decoders that read two batches with the same nested keys one record at a time in turn both return every record.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecodersKeepTheirOwnKeyStateWhenInterleaved()
    {
        // Arrange
        const string value = """{"a":{"b":1},"c":[{"b":2},{"b":3,"d":4}]}""";
        using var firstInput = BatchBuilder.Gzip(BatchBuilder.Header, WireShapes.Record(1, value), WireShapes.Record(2, value), WireShapes.Record(3, value));
        using var secondInput = BatchBuilder.Gzip(BatchBuilder.Header, WireShapes.Record(1, value), WireShapes.Record(2, value), WireShapes.Record(3, value));
        using var first = await BatchDecoder.OpenAsync(firstInput, null, TestContext.CancellationToken);
        using var second = await BatchDecoder.OpenAsync(secondInput, null, TestContext.CancellationToken);
        var firstSeqs = new List<ulong?>();
        var secondSeqs = new List<ulong?>();

        // Act
        for (var round = 0; round < 4; round++)
        {
            firstSeqs.Add((await first.NextAsync(TestContext.CancellationToken))?.Seq);
            secondSeqs.Add((await second.NextAsync(TestContext.CancellationToken))?.Seq);
        }

        // Assert
        Assert.AreSequenceEqual(new ulong?[] { 1, 2, 3, null }, firstSeqs, "records of the first decoder");
        Assert.AreSequenceEqual(new ulong?[] { 1, 2, 3, null }, secondSeqs, "records of the second decoder");
    }

    /// <summary>
    /// Every public settable property that wire data binds to has a JSON name or is ignored, and no two names of one type are equal ignoring ASCII case.
    /// </summary>
    [TestMethod]
    public void WireTypesNameEveryBoundPropertyOnceIgnoringCase()
    {
        // Arrange
        var kinds = new[]
                    {
                        RecordKind.Metric,
                        RecordKind.ProcessSnapshot,
                        RecordKind.ConnectionSnapshot,
                        RecordKind.ServiceState,
                        RecordKind.MariaDbStatus,
                        RecordKind.KernelEvent,
                        RecordKind.LogLine,
                        RecordKind.Gap
                    };
        var roots = new List<Type>
                    {
                        typeof(WireHeader),
                        typeof(Envelope)
                    };

        roots.AddRange(kinds.Select(kind => PayloadRegistry.TypeOf(kind) ?? throw new InvalidOperationException($"no payload type for {kind}")));

        // Act
        var types = ReachableWireTypes(roots);
        var failures = types.SelectMany(DescribeNameProblems).ToList();

        // Assert
        Assert.IsGreaterThan(roots.Count, types.Count, "types below the roots are reached");
        Assert.IsEmpty(failures, $"properties without a JSON name or with names equal ignoring case: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// A line of distinct keys with a repeat at its very end is malformed.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderRefusesRepeatedKeyAtTheEndOfALongLine()
    {
        // Arrange
        var value = WireShapes.ObjectFillingBytes(WireShapes.LineBytes - 200).TrimEnd('}') + ",\"a\":0}";
        var line = WireShapes.Record(1, value);
        using var input = BatchBuilder.Gzip(BatchBuilder.Header, line);
        using var decoder = await BatchDecoder.OpenAsync(input, null, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(TestContext.CancellationToken), "repeat at the end of the line");

        // Assert
        Assert.IsGreaterThan(WireShapes.LineBytes / 2, line.Length, "the line is long");
        Assert.AreEqual(WireErrorKind.Malformed, exception.Kind, "error class");
        Assert.AreEqual("duplicate key", exception.Reason, "reason");
    }

    /// <summary>
    /// Opening and reading a batch with one record line of a hostile shape allocates at most <see cref="MaxAllocationFactor"/> times the line.
    /// </summary>
    /// <param name="shape">The shape, see <see cref="WireShapes.Value"/></param>
    /// <param name="keys">The number of keys of the objects of shape 6</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(2, 0)]
    [DataRow(3, 0)]
    [DataRow(4, 0)]
    [DataRow(5, 0)]
    [DataRow(6, 9)]
    [DataRow(6, 12)]
    [DataRow(6, 24)]
    [DataRow(6, 48)]
    [DataRow(6, 65)]
    [DataRow(6, 1025)]
    public async Task BatchDecoderAllocationIsBoundedPerLine(int shape, int keys)
    {
        // Arrange
        var line = WireShapes.Record(1, WireShapes.Value(shape, keys));
        var compressed = BatchBuilder.Gzip(BatchBuilder.Header, line).ToArray();

        await ReadCompressedAsync(compressed);

        // Act
        var allocated = await AllocatedAsync(() => ReadCompressedAsync(compressed));

        // Assert
        TestContext.WriteLine($"shape {shape} with {keys} keys: {allocated / (double)line.Length:F1} times the line of {line.Length} bytes");
        Assert.IsGreaterThan(WireShapes.LineBytes * 9 / 10, line.Length, "the line is about 1 MiB");
        Assert.IsLessThan((long)MaxAllocationFactor * line.Length, allocated, "allocated bytes");
    }

    /// <summary>
    /// Opening and reading a batch of many short records nested 64 deep allocates at most <see cref="MaxAllocationFactor"/> times the batch.
    /// </summary>
    /// <param name="variant">0 for 63 arrays, 1 for 61 objects, 2 for 61 objects with 8 more keys each</param>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task BatchDecoderAllocationIsBoundedPerBatchOfDeeplyNestedRecords(int variant)
    {
        // Arrange
        var value = variant switch
                    {
                        0 => WireShapes.Arrays(63),
                        1 => WireShapes.Objects(61, string.Empty),
                        _ => WireShapes.Objects(61, "\"a\":0,\"b\":0,\"c\":0,\"d\":0,\"e\":0,\"f\":0,\"g\":0,\"h\":0,")
                    };
        var lines = WireShapes.Batch(value, 1 << 20);
        var length = lines.Sum(line => line.Length + 1);
        var compressed = BatchBuilder.Gzip(lines.ToArray()).ToArray();

        await ReadCompressedAsync(compressed);

        // Act
        var allocated = await AllocatedAsync(() => ReadCompressedAsync(compressed));

        // Assert
        TestContext.WriteLine($"variant {variant}: {allocated / (double)length:F1} times the batch of {length} bytes in {lines.Count - 1} records");
        Assert.IsLessThan((long)MaxAllocationFactor * length, allocated, "allocated bytes");
    }

    /// <summary>
    /// One big object followed by many small ones in a line of about 4 MiB takes at most <see cref="MaxTimeFactor"/> times a plain parse of the line. The test compares two
    /// durations of its own run and asserts no absolute duration.
    /// </summary>
    /// <returns>A task that completes when the work is done</returns>
    [TestMethod]
    public async Task BatchDecoderTimeIsInProportionToTheLine()
    {
        // Arrange
        const int size = 4 << 20;
        var line = WireShapes.Record(1, WireShapes.ListOf(WireShapes.ObjectFillingBytes(size / 2), """{"a":0}""", size));
        var lineBytes = Encoding.UTF8.GetBytes(line);
        var compressed = BatchBuilder.Gzip(BatchBuilder.Header, line).ToArray();
        var limits = new WireLimits
                     {
                         MaxLineBytes = 8 << 20
                     };
        var options = new JsonDocumentOptions
                      {
                          MaxDepth = WireFormat.MaxDepth
                      };

        // Act
        var decode = await BestOfThreeAsync(async () =>
                                            {
                                                using var input = new MemoryStream(compressed);
                                                using var decoder = await BatchDecoder.OpenAsync(input, limits, TestContext.CancellationToken);

                                                await ReadAllAsync(decoder);
                                            });
        var parse = await BestOfThreeAsync(() =>
                                           {
                                               using var document = JsonDocument.Parse(lineBytes, options);

                                               return Task.CompletedTask;
                                           });

        // Assert
        var ratio = decode.TotalMilliseconds / parse.TotalMilliseconds;

        TestContext.WriteLine($"decoding took {decode.TotalMilliseconds:F1} ms, JsonDocument.Parse {parse.TotalMilliseconds:F1} ms, ratio {ratio:F2}, line of {lineBytes.Length} bytes");
        Assert.IsLessThan(MaxTimeFactor, ratio, "time of the decoder as a multiple of a plain parse");
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
    /// Looks for a string that is declared non-nullable, a list element or a map value that is a null reference below an object.
    /// </summary>
    /// <param name="value">The object</param>
    /// <param name="path">The path of the object</param>
    /// <returns>The path of the first null reference, or <c>null</c> when there is none</returns>
    private static string? FindNull(object value, string path)
    {
        var context = new NullabilityInfoContext();

        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(candidate => candidate.GetMethod is not null && candidate.GetIndexParameters().Length == 0))
        {
            var required = context.Create(property).ReadState != NullabilityState.Nullable;
            var found = FindNullIn(property.GetValue(value), property.PropertyType, $"{path}.{property.Name}", required);

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
    /// <param name="required">A value indicating whether a string may not be a null reference, because it is declared non-nullable</param>
    /// <returns>The path of the first null reference, or <c>null</c> when there is none</returns>
    private static string? FindNullIn(object? child, Type type, string path, bool required)
    {
        switch (child)
        {
            case null:
                {
                    return type == typeof(string) && required ? path : null;
                }

            case IDictionary dictionary:
                {
                    return dictionary.Keys.Cast<object>().Select(key => dictionary[key] is { } entry ? FindNullIn(entry, entry.GetType(), $"{path}[{key}]", true) : $"{path}[{key}]").FirstOrDefault(found => found is not null);
                }

            case IList list:
                {
                    return Enumerable.Range(0, list.Count).Select(position => list[position] is { } element ? FindNullIn(element, element.GetType(), $"{path}[{position}]", true) : $"{path}[{position}]").FirstOrDefault(found => found is not null);
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
    /// Builds a header line with the given <c>format_major</c>.
    /// </summary>
    /// <param name="major">The JSON text of the value</param>
    /// <returns>The line</returns>
    private static string HeaderWithMajor(string major)
    {
        return $$"""{"format_major":{{major}},"format_minor":0,"agent_id":"agent-1","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}""";
    }

    /// <summary>
    /// Builds a header line with the given <c>format_minor</c>.
    /// </summary>
    /// <param name="minor">The JSON text of the value</param>
    /// <returns>The line</returns>
    private static string HeaderWithMinor(string minor)
    {
        return $$"""{"format_major":1,"format_minor":{{minor}},"agent_id":"agent-1","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}""";
    }

    /// <summary>
    /// Compresses the lines as ISO-8859-1, one byte per character, each followed by a line feed, so a character from U+0080 to U+00FF is a single raw byte.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The compressed stream, positioned at its start</returns>
    private static MemoryStream GzipLatin1(params string[] lines)
    {
        return BatchBuilder.Gzip(Encoding.Latin1.GetBytes(string.Concat(lines.Select(line => line + "\n"))));
    }

    /// <summary>
    /// Builds a valid log line record with the text as its message.
    /// </summary>
    /// <param name="message">The JSON text of the message, without the quotes</param>
    /// <param name="seq">The sequence number</param>
    /// <returns>The line</returns>
    private static string LogLine(string message, int seq)
    {
        return $$$"""{"kind":"log_line","source":"journal","seq":{{{seq}}},"captured_at":"2026-10-01T10:00:00Z","data":{"log":"journal","message":"{{{message}}}"}}""";
    }

    /// <summary>
    /// Places the text into every string of a batch that the decoder binds or skips.
    /// </summary>
    /// <param name="value">The JSON text to place</param>
    /// <returns>The places: a name, the header line, the record line and the number of the line that holds the text</returns>
    private static List<(string Name, string Header, string Record, int Line)> TextPlaces(string value)
    {
        var metric = BatchBuilder.Metric(1);

        return
        [
            ("message", BatchBuilder.Header, LogLine($"a{value}b", 1), 2),
            ("kind", BatchBuilder.Header, metric.Replace("\"kind\":\"metric\"", $"\"kind\":\"metric{value}\"", StringComparison.Ordinal), 2),
            ("header agent_id", BatchBuilder.Header.Replace("agent-1", $"agent-1{value}", StringComparison.Ordinal), metric, 1),
            ("label value", BatchBuilder.Header, metric.Replace("\"value\":1.5", $"\"value\":1.5,\"labels\":{{\"a\":\"{value}\"}}", StringComparison.Ordinal), 2),
            ("unit", BatchBuilder.Header, metric.Replace("\"value\":1.5", $"\"value\":1.5,\"unit\":\"{value}\"", StringComparison.Ordinal), 2),
            ("unknown envelope value", BatchBuilder.Header, RecordWithMembers($"\"x\":\"{value}\""), 2),
            ("unknown payload value in a list", BatchBuilder.Header, metric.Replace("\"value\":1.5", $"\"value\":1.5,\"x\":[\"{value}\"]", StringComparison.Ordinal), 2),
            ("unknown header value", HeaderWithMembers($"\"x\":\"{value}\""), metric, 1),
            ("header of major 2", $"{{\"format_major\":2,\"x\":\"{value}\"}}", metric, 1)
        ];
    }

    /// <summary>
    /// Builds a valid header line with further members behind the known ones.
    /// </summary>
    /// <param name="members">The JSON text of the members</param>
    /// <returns>The line</returns>
    private static string HeaderWithMembers(string members)
    {
        return string.Concat("{\"format_major\":1,\"format_minor\":0,\"agent_id\":\"agent-1\",\"boot_id\":\"0123abcd-0123-0123-0123-0123456789ab\",\"mode\":\"live\",", members, "}");
    }

    /// <summary>
    /// Builds a valid header line with one further member named by the key.
    /// </summary>
    /// <param name="key">The JSON text of the key, escapes included, without the quotes</param>
    /// <returns>The line</returns>
    private static string HeaderWithKey(string key)
    {
        return HeaderWithMembers($"\"{key}\":1");
    }

    /// <summary>
    /// Builds a valid metric record line with further members before the payload.
    /// </summary>
    /// <param name="members">The JSON text of the members</param>
    /// <returns>The line</returns>
    private static string RecordWithMembers(string members)
    {
        return string.Concat("{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",", members, ",\"data\":{\"name\":\"cpu\",\"value\":1}}");
    }

    /// <summary>
    /// Builds a valid metric record line with one further member named by the key.
    /// </summary>
    /// <param name="key">The JSON text of the key, escapes included, without the quotes</param>
    /// <returns>The line</returns>
    private static string RecordWithKey(string key)
    {
        return RecordWithMembers($"\"{key}\":1");
    }

    /// <summary>
    /// Builds the bytes of a valid header line with one further member whose key is the given bytes.
    /// </summary>
    /// <param name="key">The bytes of the key, no valid UTF-8 text</param>
    /// <returns>The line</returns>
    private static byte[] HeaderBytesWithKey(byte[] key)
    {
        var prefix = Encoding.UTF8.GetBytes("{\"format_major\":1,\"format_minor\":0,\"agent_id\":\"agent-1\",\"boot_id\":\"0123abcd-0123-0123-0123-0123456789ab\",\"mode\":\"live\",\"");

        return [.. prefix, .. key, .. Encoding.UTF8.GetBytes("\":1}")];
    }

    /// <summary>
    /// Builds the bytes of a valid metric record line with one further member whose key is the given bytes.
    /// </summary>
    /// <param name="key">The bytes of the key, no valid UTF-8 text</param>
    /// <returns>The line</returns>
    private static byte[] RecordBytesWithKey(byte[] key)
    {
        var prefix = Encoding.UTF8.GetBytes("{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"");

        return [.. prefix, .. key, .. Encoding.UTF8.GetBytes("\":1,\"data\":{\"name\":\"cpu\",\"value\":1}}")];
    }

    /// <summary>
    /// Joins two lines as bytes, each followed by a line feed.
    /// </summary>
    /// <param name="first">The first line</param>
    /// <param name="second">The second line</param>
    /// <returns>The bytes</returns>
    private static byte[] RawLines(byte[] first, byte[] second)
    {
        return [.. first, (byte)'\n', .. second, (byte)'\n'];
    }

    /// <summary>
    /// Lists the types that wire data binds to: the roots and, below them, the types of their properties, the elements of their lists and the values of their maps.
    /// </summary>
    /// <param name="roots">The root types</param>
    /// <returns>The types</returns>
    private static List<Type> ReachableWireTypes(List<Type> roots)
    {
        var seen = new List<Type>();
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();

            if (seen.Contains(type))
            {
                continue;
            }

            seen.Add(type);

            foreach (var inner in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).SelectMany(property => Unwrap(property.PropertyType)))
            {
                if (inner.IsClass && inner.Namespace is { } scope && scope.StartsWith("Vandox", StringComparison.Ordinal))
                {
                    queue.Enqueue(inner);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Lists a type together with its element type and its generic arguments, at any depth.
    /// </summary>
    /// <param name="type">The type</param>
    /// <returns>The types</returns>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        var parts = type.GetGenericArguments().Concat(type.GetElementType() is { } element ? [element] : []);

        foreach (var inner in parts.SelectMany(Unwrap))
        {
            yield return inner;
        }
    }

    /// <summary>
    /// Describes what is wrong with the JSON names of the public settable properties of a type.
    /// </summary>
    /// <param name="type">The type</param>
    /// <returns>One text per problem</returns>
    private static IEnumerable<string> DescribeNameProblems(Type type)
    {
        var bound = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.SetMethod is { IsPublic: true }).ToList();
        var unnamed = bound.Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is null && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                           .Select(property => $"{type.Name}.{property.Name} has no JSON name");
        var names = bound.Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name).OfType<string>();
        var clashes = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                           .Where(group => group.Count() > 1)
                           .Select(group => $"{type.Name}: {string.Join(" and ", group)} are equal ignoring case");

        return unnamed.Concat(clashes);
    }

    /// <summary>
    /// Measures the bytes an action allocates.
    /// </summary>
    /// <param name="action">The action</param>
    /// <returns>A task that returns the number of allocated bytes</returns>
    private static async Task<long> AllocatedAsync(Func<Task> action)
    {
        var before = GC.GetTotalAllocatedBytes(true);

        await action();

        return GC.GetTotalAllocatedBytes(true) - before;
    }

    /// <summary>
    /// Runs an action once to warm up and three times more and returns the shortest of the three durations.
    /// </summary>
    /// <param name="action">The action</param>
    /// <returns>A task that returns the shortest duration</returns>
    private static async Task<TimeSpan> BestOfThreeAsync(Func<Task> action)
    {
        var best = TimeSpan.MaxValue;

        await action();

        for (var run = 0; run < 3; run++)
        {
            var start = Stopwatch.GetTimestamp();

            await action();

            var elapsed = Stopwatch.GetElapsedTime(start);

            best = elapsed < best ? elapsed : best;
        }

        return best;
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

    /// <summary>
    /// Opens a batch from its compressed bytes and reads it to the end. A run before the measured one fills the array pools of the
    /// runtime, so the measurement does not depend on which test ran first.
    /// </summary>
    /// <param name="compressed">The compressed batch</param>
    /// <returns>A task that completes at the end of the batch</returns>
    private async Task ReadCompressedAsync(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);

        await OpenAndReadAllAsync(input);
    }

    #endregion // Methods
}