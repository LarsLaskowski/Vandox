using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MariaDbMessage"/>
/// </summary>
[TestClass]
public class MariaDbMessageTests
{
    #region Constants

    private const int Mebibyte = 1024 * 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Inner empty lines of an entry are kept and trailing ones are dropped.
    /// </summary>
    /// <param name="lines">The continuation lines, separated by a bar</param>
    /// <param name="expected">The expected message, with a bar for a line break</param>
    [TestMethod]
    [DataRow("|x||", "h||x")]
    [DataRow("x", "h|x")]
    [DataRow("|x", "h||x")]
    [DataRow("x|||y", "h|x|||y")]
    [DataRow("x|", "h|x")]
    [DataRow("|", "h")]
    [DataRow("||||", "h")]
    public void MariaDbMessageBuildKeepsInnerEmptyLinesAndDropsTrailingOnes(string lines, string expected)
    {
        // Arrange
        var message = new MariaDbMessage("h", false);

        foreach (var line in lines.Split('|'))
        {
            message.Add(Encoding.UTF8.GetBytes(line), false);
        }

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual(expected.Replace('|', '\n'), built, "message");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// A header without continuation lines is its own message.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildOfAHeaderAloneIsTheHeaderMessage()
    {
        // Arrange
        var message = new MariaDbMessage("header text", false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual("header text", built, "message");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// An empty header message followed by lines keeps the line structure.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildKeepsAnEmptyHeaderMessage()
    {
        // Arrange
        var message = new MariaDbMessage(string.Empty, false);

        message.Add("a"u8, false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual("\na", built, "empty first line, then the continuation line");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// A cut of the line reader makes the message truncated, for the header and for a continuation line.
    /// </summary>
    /// <param name="headerCut">Whether the header line was cut</param>
    /// <param name="lineCut">Whether the continuation line was cut</param>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void MariaDbMessageBuildIsTruncatedWhenTheReaderCutALine(bool headerCut, bool lineCut)
    {
        // Arrange
        var message = new MariaDbMessage("h", headerCut);

        message.Add("x"u8, lineCut);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual("h\nx", built, "the text is kept");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A joined message of exactly 16,384 UTF-8 bytes is kept whole.
    /// </summary>
    /// <param name="lineCount">The number of continuation lines</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(16)]
    public void MariaDbMessageBuildKeepsAMessageOfExactlyTheLimit(int lineCount)
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var expected = new StringBuilder("h");
        var rest = ModelLimits.MaxTextBytes - 1 - lineCount;

        for (var index = 0; index < lineCount; index++)
        {
            var length = index == lineCount - 1 ? rest - ((lineCount - 1) * (rest / lineCount)) : rest / lineCount;
            var line = new string('a', length);

            message.Add(Encoding.UTF8.GetBytes(line), false);
            expected.Append('\n').Append(line);
        }

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(expected.ToString()), "the arranged message has the limit size");
        Assert.AreEqual(expected.ToString(), built, "the whole message");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// One byte more than the limit omits the line that does not fit within the kept room.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildOmitsALineWhenTheLimitIsExceededByOneByte()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);

        message.Add(Encoding.UTF8.GetBytes(new string('a', ModelLimits.MaxTextBytes - 1)), false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual("h\n[1 lines omitted]", built, "the line is replaced by the marker");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// Lines beyond the kept room are replaced by a marker with the exact number of omitted lines.
    /// </summary>
    /// <param name="fill">The kind of line: ASCII letters or invalid bytes</param>
    /// <param name="keptLines">The expected number of kept lines</param>
    [TestMethod]
    [DataRow(false, 16)]
    [DataRow(true, 5)]
    public void MariaDbMessageBuildKeepsWholeLinesAndCountsTheOmittedOnes(bool fill, int keptLines)
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var raw = new byte[1000];
        var decoded = fill ? new string('\uFFFD', 1000) : new string('a', 1000);

        Array.Fill(raw, fill ? (byte)0xFF : (byte)'a');

        for (var index = 0; index < 40; index++)
        {
            message.Add(raw, false);
        }

        // Act
        var built = message.Build(out var truncated);

        // Assert
        var expected = $"h{string.Concat(Enumerable.Repeat($"\n{decoded}", keptLines))}\n[{40 - keptLines} lines omitted]";

        Assert.AreEqual(expected, built, "header, whole lines within the kept room, marker");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// Trailing empty lines after omitted lines are not counted in the marker.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildDoesNotCountTrailingEmptyLinesAsOmitted()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var raw = new byte[1000];

        Array.Fill(raw, (byte)'a');

        for (var index = 0; index < 40; index++)
        {
            message.Add(raw, false);
        }

        for (var index = 0; index < 5; index++)
        {
            message.Add([], false);
        }

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.EndsWith("\n[24 lines omitted]", built, "the marker counts the 24 text lines only");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A message of at most 16,384 bytes is kept whole, empty lines included, also beyond the room of the kept prefix.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildKeepsEmptyLinesBeyondTheKeptRoomWhenTheMessageFits()
    {
        // Arrange
        var message = BuildWithEmptyLinesAndLast("x"u8.ToArray());

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual($"h\n{new string('a', 16300)}{new string('\n', 31)}x", built, "16,334 bytes joined are kept whole");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// When the entry exceeds the limit, the kept prefix ends inside the run of empty lines and the rest is counted.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildCutsInsideARunOfEmptyLinesWhenTheEntryExceedsTheLimit()
    {
        // Arrange
        var message = BuildWithEmptyLinesAndLast("x"u8.ToArray());

        message.Add(Encoding.UTF8.GetBytes(new string('b', 100)), false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual($"h\n{new string('a', 16300)}{new string('\n', 18)}\n[14 lines omitted]", built, "18 empty lines fit up to 16,320 bytes, 12 empty lines, x and the b line are omitted");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A last line that overflows the limit after a run of empty lines is counted with the empty lines beyond the kept prefix.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildCountsEmptyLinesBeyondTheKeptRoomWhenTheLastLineOverflows()
    {
        // Arrange
        var message = BuildWithEmptyLinesAndLast(Encoding.UTF8.GetBytes(new string('b', 100)));

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual($"h\n{new string('a', 16300)}{new string('\n', 18)}\n[13 lines omitted]", built, "18 empty lines fit up to 16,320 bytes, 12 empty lines and the b line are omitted");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A header message longer than the limit, alone, is cut at a character boundary without a marker.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildCutsALongHeaderMessageAtACharacterBoundary()
    {
        // Arrange
        var message = new MariaDbMessage(new string('\uFFFD', 16000), false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual(new string('\uFFFD', 5461), built, "5,461 characters of three bytes are 16,383 bytes");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A header message longer than the limit with a continuation line is cut to the kept room and followed by the marker.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildCutsALongFirstLineToTheKeptRoomAndAddsTheMarker()
    {
        // Arrange
        var message = new MariaDbMessage(new string('\uFFFD', 16000), false);

        message.Add("x"u8, false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual($"{new string('\uFFFD', 5440)}\n[1 lines omitted]", built, "5,440 characters are 16,320 bytes");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 UTF-8 bytes");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// A cut never splits a character of four bytes.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildDoesNotSplitACharacterWhenCutting()
    {
        // Arrange
        var message = new MariaDbMessage($"{new string('a', 16383)}\U0001F600tail", false);

        // Act
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual(new string('a', 16383), built, "the character that does not fit is dropped whole");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// Bytes that are not UTF-8 in a kept continuation line become U+FFFD.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageBuildDecodesInvalidBytesOfAKeptLine()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);

        message.Add([(byte)'a', 0xFF, (byte)'b'], false);

        // Act
        var built = message.Build(out _);

        // Assert
        Assert.AreEqual("h\na\uFFFDb", built, "message");
    }

    /// <summary>
    /// A hundred thousand continuation lines do not accumulate in memory, and the marker counts them exactly.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageAddRetainsNoOmittedLines()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var raw = new byte[1000];

        Array.Fill(raw, (byte)'a');

        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 100000; index++)
        {
            message.Add(raw, false);
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(message);

        var built = message.Build(out var truncated);

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, keeping the lines would be about 200 MB of UTF-16");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 UTF-8 bytes");
        Assert.EndsWith("\n[99984 lines omitted]", built, "the marker counts the 99,984 lines left out of the 100,000");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// Millions of held-back empty lines are only counted: nothing is retained per line, and the marker counts them.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageAddRetainsNoEmptyLinesBeforeALine()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 4000000; index++)
        {
            message.Add([], false);
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(message);
        message.Add("x"u8, false);

        var built = message.Build(out var truncated);

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, a reference, a character or a byte per line would be 32 MB, 8 MB or 4 MB");
        Assert.AreEqual($"h{new string('\n', 16319)}\n[3983682 lines omitted]", built, "header, 16,319 kept empty lines, marker");
        Assert.AreEqual(16344, Encoding.UTF8.GetByteCount(built), "UTF-8 bytes");
        Assert.IsTrue(truncated, "truncated");
    }

    /// <summary>
    /// Millions of trailing empty lines are only counted and dropped.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageAddRetainsNoTrailingEmptyLines()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var before = RetainedBytes();

        // Act
        for (var index = 0; index < 4000000; index++)
        {
            message.Add([], false);
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(message);

        var built = message.Build(out var truncated);

        // Assert
        Assert.IsLessThan(Mebibyte, retained, "retained bytes, a reference, a character or a byte per line would be 32 MB, 8 MB or 4 MB");
        Assert.AreEqual("h", built, "the trailing empty lines are dropped");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// Builds a message of the header <c>h</c>, a line of 16,300 <c>a</c>, 30 empty lines and a last line.
    /// </summary>
    /// <param name="last">The bytes of the last line</param>
    /// <returns>The message</returns>
    private static MariaDbMessage BuildWithEmptyLinesAndLast(byte[] last)
    {
        var message = new MariaDbMessage("h", false);
        var raw = new byte[16300];

        Array.Fill(raw, (byte)'a');
        message.Add(raw, false);

        for (var index = 0; index < 30; index++)
        {
            message.Add([], false);
        }

        message.Add(last, false);

        return message;
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