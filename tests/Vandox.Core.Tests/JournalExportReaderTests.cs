using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="JournalExportReader"/>
/// </summary>
[TestClass]
public class JournalExportReaderTests
{
    #region Constants

    private const string Timestamp = "__REALTIME_TIMESTAMP=1772368215123456\n";
    private const long Mebibyte = 1024 * 1024;
    private const long SixtyFourMebibytes = 64 * Mebibyte;
    private const long AllocationBound = 8 * Mebibyte;
    private const string Truncated = "truncated entry";
    private const string Malformed = "malformed field";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The eight kept fields of an entry are read, the others are left out, and the input ends with <c>null</c>.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncReadsTheKeptFields()
    {
        // Arrange
        var input = new JournalExportBuilder().Text("__CURSOR", "s=1;i=1")
                                              .Text("__REALTIME_TIMESTAMP", "1772368215123456")
                                              .Text("_BOOT_ID", "0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f")
                                              .Text("PRIORITY", "6")
                                              .Text("SYSLOG_IDENTIFIER", "sshd")
                                              .Text("_COMM", "sshd-session")
                                              .Text("_PID", "1234")
                                              .Text("SYSLOG_PID", "1235")
                                              .Text("_HOSTNAME", "web-1")
                                              .Text("MESSAGE", "Accepted publickey")
                                              .End()
                                              .ToArray();
        using var stream = new MemoryStream(input);
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);
        var end = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.IsNull(entry.Problem, "no problem");
        Assert.AreEqual("1772368215123456", entry.Realtime, "timestamp");
        Assert.AreEqual("web-1", entry.Hostname, "host");
        Assert.AreEqual("sshd", entry.SyslogIdentifier, "identifier");
        Assert.AreEqual("sshd-session", entry.Comm, "command");
        Assert.AreEqual("1234", entry.Pid, "process ID");
        Assert.AreEqual("1235", entry.SyslogPid, "syslog process ID");
        Assert.AreEqual("6", entry.Priority, "priority");
        Assert.AreEqual("Accepted publickey", entry.Message, "message");
        Assert.IsFalse(entry.Truncated, "not truncated");
        Assert.IsNull(end, "the end of the input");
    }

    /// <summary>
    /// Several entries are read one after the other; empty lines between them are ignored and the last needs no closing empty line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncReadsSeveralEntries()
    {
        // Arrange
        var input = $"{Timestamp}MESSAGE=one\n\n\n\n{Timestamp}MESSAGE=two\n\n{Timestamp}MESSAGE=three\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var reader = new JournalExportReader(stream);
        var messages = new List<string?>();

        // Act
        for (var entry = await reader.ReadAsync(TestContext.CancellationToken); entry is not null; entry = await reader.ReadAsync(TestContext.CancellationToken))
        {
            messages.Add(entry.Message);
        }

        // Assert
        Assert.AreSequenceEqual<string?>(["one", "two", "three"], messages, "messages in order");
    }

    /// <summary>
    /// A repeated field keeps its first value.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncKeepsTheFirstValueOfARepeatedField()
    {
        // Arrange
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Timestamp}MESSAGE=first\nMESSAGE=second\n_PID=1\n_PID=2\n\n"));
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.AreEqual("first", entry.Message, "message");
        Assert.AreEqual("1", entry.Pid, "process ID");
    }

    /// <summary>
    /// Text values are kept as they are, a carriage return included.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncKeepsTextValuesUnchanged()
    {
        // Arrange
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Timestamp}MESSAGE= padded\r\n"));
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.AreEqual(" padded\r", entry.Message, "nothing is stripped");
    }

    /// <summary>
    /// A binary field is read with line feeds, NUL and invalid UTF-8 in it, and a binary field that is not kept is skipped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncReadsBinaryFields()
    {
        // Arrange
        byte[] message = [.. "line1\nline2"u8, 0, .. "end"u8, 0xFF, (byte)'x'];
        var input = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456")
                                              .Binary("COREDUMP_PROC_STATUS", [1, 2, 3, 10, 0, 255])
                                              .Binary("MESSAGE", message)
                                              .Text("_HOSTNAME", "web-1")
                                              .End()
                                              .ToArray();
        using var stream = new MemoryStream(input);
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.IsNull(entry.Problem, "no problem");
        Assert.AreEqual("line1\nline2\0end�x", entry.Message, "binary message with invalid bytes replaced");
        Assert.AreEqual("web-1", entry.Hostname, "the field after the skipped binary field is read");
    }

    /// <summary>
    /// A message over its limit is cut to 16,384 UTF-8 bytes and marked as truncated.
    /// </summary>
    /// <param name="binary">Whether the message is a binary field</param>
    /// <param name="length">The length of the message in bytes</param>
    /// <param name="truncated">Whether the entry is expected to be truncated</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false, 16384, false)]
    [DataRow(false, 16385, true)]
    [DataRow(false, 70000, true)]
    [DataRow(true, 16384, false)]
    [DataRow(true, 16385, true)]
    [DataRow(true, 70000, true)]
    public async Task JournalExportReaderReadAsyncCutsLongMessages(bool binary, int length, bool truncated)
    {
        // Arrange
        var value = Enumerable.Repeat((byte)'a', length).ToArray();
        var builder = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456");

        _ = binary ? builder.Binary("MESSAGE", value) : builder.Text("MESSAGE", value);

        using var stream = new MemoryStream(builder.End().ToArray());
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.IsNull(entry.Problem, "no problem");
        Assert.AreEqual(Math.Min(length, 16384), Encoding.UTF8.GetByteCount(entry.Message!), "message length in UTF-8 bytes");
        Assert.AreEqual(truncated, entry.Truncated, "truncated flag");
    }

    /// <summary>
    /// A message cut by the bound inside a multi-byte character ends at the last whole character, without a replacement character, and is marked as truncated.
    /// </summary>
    /// <param name="binary">Whether the message is a binary field</param>
    /// <param name="character">The character the bound cuts</param>
    /// <param name="keptBytes">How many bytes of the character are within the bound</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false, "\u20AC", 1)]
    [DataRow(false, "\u20AC", 2)]
    [DataRow(false, "\uD83D\uDE00", 3)]
    [DataRow(true, "\u20AC", 1)]
    [DataRow(true, "\u20AC", 2)]
    [DataRow(true, "\uD83D\uDE00", 3)]
    public async Task JournalExportReaderReadAsyncEndsCutMessageAtTheLastWholeCharacter(bool binary, string character, int keptBytes)
    {
        // Arrange
        var prefix = new string('a', 16384 - keptBytes);
        var value = Encoding.UTF8.GetBytes($"{prefix}{character}tail");
        var builder = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456");

        _ = binary ? builder.Binary("MESSAGE", value) : builder.Text("MESSAGE", value);

        using var stream = new MemoryStream(builder.End().ToArray());
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.IsNull(entry.Problem, "no problem");
        Assert.AreEqual(prefix, entry.Message, "the message ends before the cut character");
        Assert.IsFalse(entry.Message!.Contains('\uFFFD'), "no replacement character");
        Assert.IsTrue(entry.Truncated, "truncated flag");
    }

    /// <summary>
    /// A field name that is empty, too long, starts with a digit or holds other characters than <c>A-Z 0-9 _</c> makes the entry malformed, and the next entry is read.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("Abc")]
    [DataRow("A-B")]
    [DataRow("A B")]
    [DataRow("1ABC")]
    [DataRow("A.B")]
    [DataRow("Ä")]
    [DataRow("AÄ")]
    public async Task JournalExportReaderReadAsyncSkipsEntryWithMalformedFieldName(string name)
    {
        // Arrange
        var input = $"{Timestamp}{name}=x\nMESSAGE=lost\n\n{Timestamp}MESSAGE=next\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var reader = new JournalExportReader(stream);

        // Act
        var bad = await reader.ReadAsync(TestContext.CancellationToken);
        var next = await reader.ReadAsync(TestContext.CancellationToken);
        var end = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(bad, "the malformed entry is reported");
        Assert.AreEqual(Malformed, bad.Problem, "problem");
        Assert.IsNotNull(next, "the next entry");
        Assert.IsNull(next.Problem, "the next entry is fine");
        Assert.AreEqual("next", next.Message, "message of the next entry");
        Assert.IsNull(end, "the end of the input");
    }

    /// <summary>
    /// A name of 64 bytes is accepted and one of 65 bytes is malformed.
    /// </summary>
    /// <param name="length">The length of the name</param>
    /// <param name="problem">The expected problem, or an empty text for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(64, "")]
    [DataRow(65, Malformed)]
    public async Task JournalExportReaderReadAsyncLimitsTheFieldNameLength(int length, string problem)
    {
        // Arrange
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Timestamp}{new string('A', length)}=x\nMESSAGE=m\n\n"));
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "an entry");
        Assert.AreEqual(problem, entry.Problem ?? string.Empty, "problem");
    }

    /// <summary>
    /// A binary length beyond the remaining input, one of 2^63 or more, and a text value without a line feed at the end of the input make the entry cut off and end the input.
    /// </summary>
    /// <param name="kind">The case</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("beyond")]
    [DataRow("huge")]
    [DataRow("maximum")]
    [DataRow("text")]
    [DataRow("textmessage")]
    public async Task JournalExportReaderReadAsyncReportsCutOffEntryAndEnds(string kind)
    {
        // Arrange
        var builder = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456");

        _ = kind switch
            {
                "beyond" => builder.Binary("MESSAGE", 1000, new byte[10]),
                "huge" => builder.Binary("OTHER", 1UL << 63, new byte[10]),
                "maximum" => builder.Binary("MESSAGE", ulong.MaxValue, new byte[10]),
                "text" => builder.Raw("OTHER=no line feed"),
                _ => builder.Raw("MESSAGE=no line feed")
            };

        using var stream = new MemoryStream(builder.ToArray());
        var reader = new JournalExportReader(stream);

        // Act
        var entry = await reader.ReadAsync(TestContext.CancellationToken);
        var end = await reader.ReadAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(entry, "the cut-off entry is reported");
        Assert.AreEqual(Truncated, entry.Problem, "problem");
        Assert.IsNull(end, "the input ends");
    }

    /// <summary>
    /// A 64 MiB binary field of a name that is not kept is skipped in bounded memory and the entry is read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncSkipsLargeBinaryFieldInBoundedMemory()
    {
        // Arrange
        var header = new JournalExportBuilder().Text("__CURSOR", "s=1").BinaryHeader("HUGE_FIELD", (ulong)SixtyFourMebibytes).ToArray();
        var trailer = Encoding.UTF8.GetBytes($"\nMESSAGE=hello\n{Timestamp}\n");
        using var stream = new PatternStream(header, "x"u8.ToArray(), SixtyFourMebibytes, trailer);
        var reader = new JournalExportReader(stream);
        JournalEntry? entry = null;
        JournalEntry? end = null;

        // Act
        var allocated = await Allocated(async () =>
                                        {
                                            entry = await reader.ReadAsync(TestContext.CancellationToken);
                                            end = await reader.ReadAsync(TestContext.CancellationToken);
                                        });

        // Assert
        Assert.IsNotNull(entry, "the entry is read");
        Assert.IsNull(entry.Problem, "no problem");
        Assert.AreEqual("hello", entry.Message, "message");
        Assert.AreEqual("1772368215123456", entry.Realtime, "timestamp");
        Assert.IsNull(end, "the end of the input");
        Assert.IsLessThan(AllocationBound, allocated, "allocated bytes, a reader that buffers the field allocates at least 64 MiB");
    }

    /// <summary>
    /// A 64 MiB text value without a line feed, under a name that is not kept and as the message, ends the entry as cut off in bounded memory.
    /// </summary>
    /// <param name="field">The field name</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("OTHER_FIELD")]
    [DataRow("MESSAGE")]
    public async Task JournalExportReaderReadAsyncReadsLargeTextValueWithoutLineFeedInBoundedMemory(string field)
    {
        // Arrange
        var header = Encoding.UTF8.GetBytes($"{Timestamp}{field}=");
        using var stream = new PatternStream(header, "x"u8.ToArray(), SixtyFourMebibytes, []);
        var reader = new JournalExportReader(stream);
        JournalEntry? entry = null;
        JournalEntry? end = null;

        // Act
        var allocated = await Allocated(async () =>
                                        {
                                            entry = await reader.ReadAsync(TestContext.CancellationToken);
                                            end = await reader.ReadAsync(TestContext.CancellationToken);
                                        });

        // Assert
        Assert.IsNotNull(entry, "the cut-off entry is reported");
        Assert.AreEqual(Truncated, entry.Problem, "problem");
        Assert.IsNull(end, "the input ends");
        Assert.IsLessThan(AllocationBound, allocated, "allocated bytes");
    }

    /// <summary>
    /// A binary message that declares 2^62 bytes is cut off without an allocation of that size.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task JournalExportReaderReadAsyncNeverSizesAnAllocationByTheDeclaredLength()
    {
        // Arrange
        var input = new JournalExportBuilder().Text("__REALTIME_TIMESTAMP", "1772368215123456").Binary("MESSAGE", 1UL << 62, new byte[100]).ToArray();
        using var stream = new MemoryStream(input);
        var reader = new JournalExportReader(stream);
        JournalEntry? entry = null;
        JournalEntry? end = null;

        // Act
        var allocated = await Allocated(async () =>
                                        {
                                            entry = await reader.ReadAsync(TestContext.CancellationToken);
                                            end = await reader.ReadAsync(TestContext.CancellationToken);
                                        });

        // Assert
        Assert.IsNotNull(entry, "the cut-off entry is reported");
        Assert.AreEqual(Truncated, entry.Problem, "problem");
        Assert.IsNull(end, "the input ends without an exception");
        Assert.IsLessThan(AllocationBound, allocated, "allocated bytes");
    }

    /// <summary>
    /// After a malformed field, 64 MiB without an empty line are skipped in bounded memory: one malformed entry, no other record or skip, and the input ends.
    /// </summary>
    /// <param name="lineLength">The length of the lines of the pattern including the line feed, or 0 for no line feed at all</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1024)]
    [DataRow(0)]
    public async Task JournalExportReaderReadAsyncResynchronizesAfterMalformedFieldInBoundedMemory(int lineLength)
    {
        // Arrange
        var pattern = lineLength == 0 ? "a"u8.ToArray() : [.. Enumerable.Repeat((byte)'a', lineLength - 1), (byte)'\n'];
        using var stream = new PatternStream("bad-name=x\n"u8.ToArray(), pattern, SixtyFourMebibytes, []);
        var reader = new JournalExportReader(stream);
        var entries = new List<JournalEntry>();

        // Act
        var allocated = await Allocated(async () =>
                                        {
                                            for (var entry = await reader.ReadAsync(TestContext.CancellationToken); entry is not null; entry = await reader.ReadAsync(TestContext.CancellationToken))
                                            {
                                                entries.Add(entry);
                                            }
                                        });

        // Assert
        Assert.HasCount(1, entries, "exactly one entry is reported");
        Assert.AreEqual(Malformed, entries[0].Problem, "the entry is malformed");
        Assert.IsLessThan(AllocationBound, allocated, "allocated bytes, the skipped bytes are never collected");
    }

    /// <summary>
    /// Measures the bytes an action allocates.
    /// </summary>
    /// <param name="action">The action</param>
    /// <returns>A task that returns the number of allocated bytes</returns>
    private static async Task<long> Allocated(Func<Task> action)
    {
        var before = GC.GetTotalAllocatedBytes(true);

        await action();

        return GC.GetTotalAllocatedBytes(true) - before;
    }

    #endregion // Methods
}