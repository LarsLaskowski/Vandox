using System.Text;

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
    [DataRow("null", WireErrorKind.Malformed)]
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