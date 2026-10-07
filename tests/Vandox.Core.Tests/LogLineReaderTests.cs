using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LogLineReader"/>
/// </summary>
[TestClass]
public class LogLineReaderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Lines end with a line feed or a carriage return and line feed; the last line needs no ending.
    /// </summary>
    /// <param name="text">The input</param>
    /// <param name="expected">The lines joined with a bar</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("a\nb\n", "a|b")]
    [DataRow("a\r\nb\r\n", "a|b")]
    [DataRow("a\nb", "a|b")]
    [DataRow("\n\n", "|")]
    [DataRow("a\r\n\r\nb", "a||b")]
    [DataRow("a\r", "a\r")]
    [DataRow("", "")]
    [DataRow("\r\n", "")]
    public async Task LogLineReaderSplitsLines(string text, string expected)
    {
        // Arrange
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var reader = new LogLineReader(input);
        var lines = new List<string>();

        // Act
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            lines.Add(Encoding.UTF8.GetString(reader.Line.Span));
        }

        // Assert
        Assert.AreEqual(expected, string.Join('|', lines), "lines");
        Assert.AreEqual(lines.Count, (int)reader.LineNumber, "line number");
    }

    /// <summary>
    /// A line of exactly the longest length is kept, one byte more is cut, and the rest of the line is discarded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogLineReaderCutsLongLinesAtLimit()
    {
        // Arrange
        var exact = new string('a', LogLineReader.MaxLineBytes);
        var longer = new string('b', LogLineReader.MaxLineBytes + 1);
        var huge = new string('c', LogLineReader.MaxLineBytes * 5);
        var text = $"{exact}\r\n{longer}\n{huge}\nend";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var reader = new LogLineReader(input);

        // Act
        await reader.ReadAsync(TestContext.CancellationToken);

        var first = (reader.Line.Length, reader.Truncated);

        await reader.ReadAsync(TestContext.CancellationToken);

        var second = (reader.Line.Length, reader.Truncated);

        await reader.ReadAsync(TestContext.CancellationToken);

        var third = (reader.Line.Length, reader.Truncated);

        await reader.ReadAsync(TestContext.CancellationToken);

        var fourth = (Encoding.UTF8.GetString(reader.Line.Span), reader.Truncated);

        // Assert
        Assert.AreEqual((LogLineReader.MaxLineBytes, false), first, "exact length with CRLF is not cut");
        Assert.AreEqual((LogLineReader.MaxLineBytes, true), second, "one byte more is cut");
        Assert.AreEqual((LogLineReader.MaxLineBytes, true), third, "a huge line is cut");
        Assert.AreEqual(("end", false), fourth, "the line after a huge one is intact");
    }

    /// <summary>
    /// A cut never splits a UTF-8 sequence.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogLineReaderCutsAtUtf8Boundary()
    {
        // Arrange
        var text = new string('a', LogLineReader.MaxLineBytes - 1) + "€€\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var reader = new LogLineReader(input);

        // Act
        await reader.ReadAsync(TestContext.CancellationToken);

        var decoded = new UTF8Encoding(false, true).GetString(reader.Line.Span);

        // Assert
        Assert.IsTrue(reader.Truncated, "the line is cut");
        Assert.AreEqual(LogLineReader.MaxLineBytes - 1, decoded.Length, "the euro sign that does not fit is dropped whole");
    }

    /// <summary>
    /// A cancelled read stops.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LogLineReaderHonorsCancellation()
    {
        // Arrange
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("a\n"));
        using var cancelled = new CancellationTokenSource();
        var reader = new LogLineReader(input);

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.ReadAsync(cancelled.Token), "cancelled read");
    }

    #endregion // Methods
}