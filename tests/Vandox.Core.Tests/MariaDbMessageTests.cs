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
    /// While the lines fit, <c>TryAdd</c> gives the same message and flag as the raw <c>Add</c>: empty lines held back, inner ones kept, trailing ones dropped.
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
    public void MariaDbMessageTryAddBuildsTheSameMessageAsTheRawAddWhileLinesFit(string lines, string expected)
    {
        // Arrange
        var raw = new MariaDbMessage("h", false);
        var decoded = new MariaDbMessage("h", false);
        var taken = new List<bool>();

        foreach (var line in lines.Split('|'))
        {
            raw.Add(Encoding.UTF8.GetBytes(line), false);
        }

        // Act
        foreach (var line in lines.Split('|'))
        {
            taken.Add(decoded.TryAdd(line, false));
        }

        var built = decoded.Build(out var truncated);
        var rawBuilt = raw.Build(out var rawTruncated);

        // Assert
        Assert.IsTrue(taken.All(value => value), "every line fits");
        Assert.AreEqual(expected.Replace('|', '\n'), built, "message");
        Assert.AreEqual(rawBuilt, built, "the same message as the raw overload");
        Assert.AreEqual(rawTruncated, truncated, "the same flag as the raw overload");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// The limit is exact: a message of 16,384 bytes is taken whole, one of 16,385 is refused and the message stays as it was.
    /// </summary>
    /// <param name="length">The characters of the line</param>
    /// <param name="expected">Whether the line is taken</param>
    [TestMethod]
    [DataRow(16381, true)]
    [DataRow(16382, true)]
    [DataRow(16383, false)]
    [DataRow(16384, false)]
    public void MariaDbMessageTryAddTakesALineOnlyWhileTheMessageStaysWithinTheLimit(int length, bool expected)
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var line = new string('a', length);

        // Act
        var taken = message.TryAdd(line, false);
        var built = message.Build(out var truncated);

        // Assert
        Assert.AreEqual(expected, taken, "taken");
        Assert.AreEqual(expected ? $"h\n{line}" : "h", built, "the message");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 bytes");
        Assert.IsFalse(truncated, "the message has no marker and no flag");
    }

    /// <summary>
    /// The size is counted in UTF-8 bytes of the text; an invalid byte that became U+FFFD counts 3.
    /// </summary>
    /// <param name="character">The character of the line</param>
    /// <param name="count">The number of characters</param>
    /// <param name="expected">Whether the line is taken</param>
    [TestMethod]
    [DataRow('é', 8191, true)]
    [DataRow('é', 8192, false)]
    [DataRow('�', 5460, true)]
    [DataRow('�', 5461, false)]
    public void MariaDbMessageTryAddCountsUtf8Bytes(char character, int count, bool expected)
    {
        // Arrange
        var message = new MariaDbMessage("h", false);

        // Act
        var taken = message.TryAdd(new string(character, count), false);

        // Assert
        Assert.AreEqual(expected, taken, "1 + 1 + 2 or 3 bytes per character against 16,384");
    }

    /// <summary>
    /// The held-back empty lines count: a line that fits alone is refused after two empty lines, and then the message is unchanged, the empty lines still held back.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageTryAddCountsTheHeldBackEmptyLinesAndChangesNothingWhenRefusing()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var first = new string('a', 16000);

        message.TryAdd(first, false);
        message.TryAdd(string.Empty, false);
        message.TryAdd(string.Empty, false);

        // Act
        var refused = message.TryAdd(new string('b', 380), true);
        var afterRefusal = message.Build(out var truncated);
        var fits = message.TryAdd(new string('c', 379), false);
        var built = message.Build(out _);

        // Assert
        Assert.IsFalse(refused, "16,002 + 2 + 1 + 380 is 16,385");
        Assert.AreEqual($"h\n{first}", afterRefusal, "the text is as before the call, without the empty lines");
        Assert.IsFalse(truncated, "the flag of the refused line is not taken over");
        Assert.IsTrue(fits, "16,002 + 2 + 1 + 379 is 16,384, so the held-back empty lines are still counted");
        Assert.AreEqual($"h\n{first}\n\n\n{new string('c', 379)}", built, "both empty lines are kept before the line");
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(built), "exactly the limit");
    }

    /// <summary>
    /// An empty line is always taken, also when the message is full, and trailing empty lines are not built.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageTryAddAlwaysTakesAnEmptyLine()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);
        var full = new string('a', 16382);

        message.TryAdd(full, false);

        // Act
        var first = message.TryAdd(string.Empty, false);
        var second = message.TryAdd(string.Empty, false);
        var built = message.Build(out var truncated);

        // Assert
        Assert.IsTrue(first, "an empty line of a full message");
        Assert.IsTrue(second, "another one");
        Assert.AreEqual($"h\n{full}", built, "the trailing empty lines are dropped");
        Assert.IsFalse(truncated, "nothing was cut");
    }

    /// <summary>
    /// The flag of a taken line is or-ed in, also the flag of an empty line.
    /// </summary>
    /// <param name="headerTruncated">The flag of the header</param>
    /// <param name="line">The text of the line</param>
    /// <param name="lineTruncated">The flag of the line</param>
    /// <param name="expected">The expected flag</param>
    [TestMethod]
    [DataRow(false, "x", false, false)]
    [DataRow(false, "x", true, true)]
    [DataRow(true, "x", false, true)]
    [DataRow(false, "", true, true)]
    [DataRow(true, "", false, true)]
    public void MariaDbMessageTryAddOrsTheTruncatedFlagIn(bool headerTruncated, string line, bool lineTruncated, bool expected)
    {
        // Arrange
        var message = new MariaDbMessage("h", headerTruncated);

        // Act
        var taken = message.TryAdd(line, lineTruncated);

        message.Build(out var truncated);

        // Assert
        Assert.IsTrue(taken, "the line is taken");
        Assert.AreEqual(expected, truncated, "truncated");
    }

    /// <summary>
    /// After the constructor cut a header longer than 16,384 bytes, every non-empty line is refused and an empty line is taken.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageTryAddRefusesEveryLineAfterTheConstructorCutTheHeader()
    {
        // Arrange
        var message = new MariaDbMessage(new string('a', 20000), false);

        // Act
        var shortLine = message.TryAdd("x", false);
        var empty = message.TryAdd(string.Empty, false);
        var built = message.Build(out var truncated);

        // Assert
        Assert.IsFalse(shortLine, "even a short line does not fit after the cut");
        Assert.IsTrue(empty, "an empty line is always taken");
        Assert.AreEqual(new string('a', 16384), built, "the cut header");
        Assert.IsTrue(truncated, "the header was cut");
    }

    /// <summary>
    /// A header cut to 16,382 bytes at a character boundary stays below the limit by itself, but the cut still refuses every non-empty line and changes nothing.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageTryAddAfterHeaderCutBelowLimitReturnsFalse()
    {
        // Arrange
        var message = new MariaDbMessage($"{new string('a', 16382)}\u20ACmore text", false);
        var before = message.Build(out var truncatedBefore);

        // Act
        var taken = message.TryAdd("x", false);
        var after = message.Build(out var truncatedAfter);

        // Assert
        Assert.AreEqual(16382, Encoding.UTF8.GetByteCount(before), "the header is cut before the euro sign");
        Assert.IsTrue(truncatedBefore, "the header was cut");
        Assert.IsFalse(taken, "a line that would fit by size is refused after the cut");
        Assert.AreEqual(before, after, "the message is unchanged");
        Assert.AreEqual(truncatedBefore, truncatedAfter, "the flag is unchanged");
    }

    /// <summary>
    /// A message that is built only through <c>TryAdd</c> never has a marker, however many lines are refused.
    /// </summary>
    [TestMethod]
    public void MariaDbMessageTryAddNeverProducesAMarker()
    {
        // Arrange
        var message = new MariaDbMessage("h", false);

        // Act
        for (var index = 0; index < 100; index++)
        {
            message.TryAdd(new string('a', 1000), false);
        }

        var built = message.Build(out var truncated);

        // Assert
        Assert.DoesNotContain("omitted", built, "no marker");
        Assert.IsLessThanOrEqualTo(ModelLimits.MaxTextBytes, Encoding.UTF8.GetByteCount(built), "at most 16,384 bytes");
        Assert.AreEqual(1 + (16 * 1001), built.Length, "16 lines of 1,000 characters fit after the header");
        Assert.IsFalse(truncated, "the refused lines do not set the flag");
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