using System.Text.RegularExpressions;

using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogText"/>
/// </summary>
[TestClass]
public partial class LogTextTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Fields

    private static readonly string _bidi = char.ConvertFromUtf32(0x202e);

    private static readonly string _zeroWidth = char.ConvertFromUtf32(0x200b);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Invisible, direction-changing and unassigned characters become visible escapes, everything around them stays.
    /// </summary>
    /// <param name="codePoint">The code point of the character; the format tool turns an escape in the source into the character, so the test builds it from the number</param>
    /// <param name="expected">The escape text</param>
    [TestMethod]
    [DataRow(0x202e, "\\u202e")]
    [DataRow(0x200b, "\\u200b")]
    [DataRow(0x0000, "\\u0000")]
    [DataRow(0x000d, "\\u000d")]
    [DataRow(0x2028, "\\u2028")]
    [DataRow(0xe000, "\\ue000")]
    [DataRow(0x0378, "\\u0378")]
    [DataRow(0xfffd, "\\ufffd")]
    [DataRow(0xe0001, "\\U000e0001")]
    public void RunsEscapeInvisibleCharacters(int codePoint, string expected)
    {
        // Arrange
        var character = char.ConvertFromUtf32(codePoint);
        var text = $"a{character}b";

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.AreEqual(codePoint, char.ConvertToUtf32(character, 0), "the input holds the character, so the escape survived");
        Assert.HasCount(3, runs, "plain, escaped, plain");
        Assert.AreEqual("a", runs[0].Text, "text before");
        Assert.IsFalse(runs[0].Escaped, "text before is plain");
        Assert.AreEqual(expected, runs[1].Text, "the escape");
        Assert.IsTrue(runs[1].Escaped, "the escape is marked");
        Assert.AreEqual("b", runs[2].Text, "text after");
        Assert.IsFalse(runs[2].Escaped, "text after is plain");
    }

    /// <summary>
    /// A lone surrogate, high or low, becomes an escape of the code unit, and a valid pair stays one character.
    /// </summary>
    /// <param name="unit">The code unit of the lone surrogate; the test builds it from the number, as the format tool would turn an escape in the source into a character</param>
    /// <param name="expected">The escape text</param>
    [TestMethod]
    [DataRow(0xd800, "\\ud800")]
    [DataRow(0xdfff, "\\udfff")]
    public void RunsEscapeLoneSurrogates(int unit, string expected)
    {
        // Arrange
        var text = $"a{(char)unit}b{char.ConvertFromUtf32(0x1f600)}";

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.AreEqual(unit, text[1], "the input holds the lone surrogate");
        Assert.HasCount(3, runs, "plain, escaped, plain");
        Assert.AreEqual(expected, runs[1].Text, "the escape of the code unit");
        Assert.IsTrue(runs[1].Escaped, "the escape is marked");
        Assert.AreEqual($"b{char.ConvertFromUtf32(0x1f600)}", runs[2].Text, "the valid pair after it stays");
        Assert.AreEqual(expected, LogText.Escape($"{(char)unit}", LogText.MaxChars), "the string form agrees");
    }

    /// <summary>
    /// Line feed and tab stay, and markup is left to Razor.
    /// </summary>
    [TestMethod]
    public void RunsKeepLineFeedAndTab()
    {
        // Arrange
        const string text = "a\nb\tc <script>";

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.HasCount(1, runs, "one plain run");
        Assert.AreEqual(text, runs[0].Text, "unchanged");
        Assert.IsFalse(runs[0].Escaped, "plain");
    }

    /// <summary>
    /// Consecutive escapes form one run.
    /// </summary>
    [TestMethod]
    public void RunsCoalesceConsecutiveEscapes()
    {
        // Arrange
        var text = $"a{_bidi}{_zeroWidth}b";

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.AreEqual(0x202e, char.ConvertToUtf32(text, 1), "the input holds the character");
        Assert.HasCount(3, runs, "plain, one escaped run, plain");
        Assert.AreEqual("\\u202e\\u200b", runs[1].Text, "both escapes in one run");
        Assert.IsTrue(runs[1].Escaped, "the run is marked");
    }

    /// <summary>
    /// A text with more runs than the limit ends in a run that holds the rest.
    /// </summary>
    [TestMethod]
    public void RunsAreAtMostTwoHundredFiftySix()
    {
        // Arrange
        var text = string.Concat(Enumerable.Repeat($"a{_bidi}", 300));

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.AreEqual(0x202e, char.ConvertToUtf32(text, 1), "the input holds the character");
        Assert.HasCount(LogText.MaxRuns, runs, "at most 256 runs");
        Assert.AreEqual(LogText.Escape(text[(LogText.MaxRuns - 1)..], LogText.MaxChars), runs[^1].Text, "the last run holds the rest as escape text");
        Assert.AreEqual(LogText.Escape(text, LogText.MaxChars), string.Concat(runs.Select(run => run.Text)), "the runs together are the escaped text");
    }

    /// <summary>
    /// A character above U+FFFF is written with eight digits, and a valid supplementary character stays.
    /// </summary>
    [TestMethod]
    public void EscapeWritesSupplementaryCharactersWithEightDigits()
    {
        // Arrange
        const string tag = "a\U000e0001b";
        const string smile = "x\U0001f600y";

        // Act
        var escaped = LogText.Escape(tag, LogText.MaxChars);
        var kept = LogText.Escape(smile, LogText.MaxChars);

        // Assert
        Assert.AreEqual(0xe0001, char.ConvertToUtf32(tag, 1), "the input holds the character, so the escape survived");
        Assert.AreEqual("a\\U000e0001b", escaped, "eight digits, lower-case hex");
        Assert.AreEqual(smile, kept, "an assigned visible character stays");
    }

    /// <summary>
    /// A message of 16,384 NUL characters is cut at the character limit with a visible marker.
    /// </summary>
    [TestMethod]
    public void RunsCutAnAllNulMessageAtTheCharacterLimit()
    {
        // Arrange
        var text = new string('\u0000', LogText.MaxChars);

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.AreEqual(0, text[0], "the input holds the character, so the escape survived");
        Assert.HasCount(2, runs, "the escapes and the marker");
        Assert.AreEqual(string.Concat(Enumerable.Repeat("\\u0000", 2730)), runs[0].Text, "2730 escapes, 16380 characters");
        Assert.IsTrue(runs[0].Escaped, "the escapes are marked");
        Assert.AreEqual("[cut: 13654 more characters]", runs[1].Text, "the marker names the characters not shown");
        Assert.IsTrue(runs[1].Escaped, "the marker is an escaped run");
        Assert.IsLessThanOrEqualTo(LogText.MaxChars + runs[1].Text.Length, runs.Sum(run => run.Text.Length), "the output holds at most the cap plus the marker");
    }

    /// <summary>
    /// A plain message of 16 KiB stays whole.
    /// </summary>
    [TestMethod]
    public void RunsKeepAPlainMessageOfSixteenKibibytesWhole()
    {
        // Arrange
        var text = new string('a', LogText.MaxChars);

        // Act
        var runs = LogText.Runs(text, LogText.MaxChars);

        // Assert
        Assert.HasCount(1, runs, "one run, no marker");
        Assert.AreEqual(text, runs[0].Text, "whole");
        Assert.IsFalse(runs[0].Escaped, "plain");
    }

    /// <summary>
    /// A cap that falls inside an escape or a surrogate pair ends the output before it.
    /// </summary>
    [TestMethod]
    public void RunsNeverSplitAnEscape()
    {
        // Arrange
        const string escapes = "ab\u0000\u0000\u0000";
        const string pair = "ab\U0001f600";

        // Act
        var escapeRuns = LogText.Runs(escapes, 10);
        var pairRuns = LogText.Runs(pair, 3);

        // Assert
        Assert.AreEqual(0, escapes[2], "the input holds the character, so the escape survived");
        Assert.HasCount(3, escapeRuns, "plain, one escape, marker");
        Assert.AreEqual("ab", escapeRuns[0].Text, "the plain text");
        Assert.AreEqual("\\u0000", escapeRuns[1].Text, "one whole escape: the second would pass the cap");
        Assert.AreEqual("[cut: 2 more characters]", escapeRuns[2].Text, "two characters are not shown");
        Assert.IsTrue(escapeRuns.Take(2).Where(run => run.Escaped).All(run => WholeEscapes().IsMatch(run.Text)), "every escaped run holds whole escapes");
        Assert.HasCount(2, pairRuns, "plain and marker");
        Assert.AreEqual("ab", pairRuns[0].Text, "the pair does not fit and is not split");
        Assert.AreEqual("[cut: 2 more characters]", pairRuns[1].Text, "the pair has two UTF-16 characters");
    }

    /// <summary>
    /// A text with more runs than the given number ends in one run that holds the rest as the escaped text writes it.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void RunsAreAtMostTheGivenNumber()
    {
        // Arrange
        const int maxRuns = 4;

        var text = Alternating(40, 'a', (char)1);

        // Act
        var runs = LogText.Runs(text, LogText.MaxShortChars, maxRuns);

        // Assert
        Assert.AreEqual(1, text[1], "the input holds the control character, so the escape survived");
        Assert.HasCount(maxRuns, runs, "at most the given number of runs");
        Assert.AreEqual("a", runs[0].Text, "the first run is plain text");
        Assert.AreEqual("\\u0001", runs[1].Text, "the second run is the escape");
        Assert.AreEqual("a", runs[2].Text, "the third run is plain text");
        Assert.AreEqual(LogText.Escape(text[3..], LogText.MaxShortChars - 8), runs[^1].Text, "the last run holds the rest as escape text");
        Assert.IsTrue(runs[^1].Escaped, "the rest holds escapes, so it is marked");
        Assert.AreEqual(LogText.Escape(text, LogText.MaxShortChars), string.Concat(runs.Select(run => run.Text)), "the runs together are the escaped text");
    }

    /// <summary>
    /// A run cap below one is refused.
    /// </summary>
    /// <param name="maxRuns">The run cap</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(0)]
    [DataRow(-1)]
    public void RunsRefuseFewerThanOneRun(int maxRuns)
    {
        // Arrange
        const string text = "abc";

        // Act
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LogText.Runs(text, LogText.MaxShortChars, maxRuns), "a run cap below one");

        // Assert
        Assert.AreEqual("maxRuns", exception.ParamName, "the exception names the run cap");
    }

    /// <summary>
    /// A rest whose escapes and cut leave it as long as the source text it stands for is still marked as escaped.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void RunsMarkARestOfEqualLengthAsEscaped()
    {
        // Arrange
        var text = EqualLengthRest();

        // Act
        var runs = LogText.Runs(text, LogText.MaxShortChars, ReportsView.MaxTextRuns);

        // Assert
        var rest = LogText.Escape(text[15..], 1998);

        Assert.AreEqual(1, text[15], "the input holds the control character, so the escape survived");
        Assert.AreEqual(text.Length - 15, rest.Length, "the rest is as long as the source text it stands for");
        Assert.EndsWith("[cut: 30 more characters]", rest, "the rest ends with the marker of the cut");
        Assert.HasCount(ReportsView.MaxTextRuns, runs, "16 runs");
        Assert.AreEqual(rest, runs[^1].Text, "the last run holds the rest as escape text");
        Assert.IsTrue(runs[^1].Escaped, "the rest differs from its source text, so it is marked");
        Assert.IsTrue(runs.Take(15).All(run => run.Escaped == (run.Text != "a")), "the runs before the rest are marked by what they hold");
    }

    /// <summary>
    /// A rest equal to the source text it stands for stays plain.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void RunsKeepAPlainRestPlain()
    {
        // Arrange
        var text = Alternating(15, (char)1, 'a') + new string('a', 100);

        // Act
        var runs = LogText.Runs(text, LogText.MaxShortChars, ReportsView.MaxTextRuns);

        // Assert
        Assert.AreEqual(1, text[14], "the input holds the control character as the 15th run, so the escape survived");
        Assert.HasCount(ReportsView.MaxTextRuns, runs, "16 runs");
        Assert.AreEqual(new string('a', 100), runs[^1].Text, "the last run is the plain rest");
        Assert.IsFalse(runs[^1].Escaped, "the rest equals its source text, so it is plain");
    }

    /// <summary>
    /// A command of 1,024 bytes costs at most 8,192 bytes of retained heap with its runs of the report page even when plain and escaped characters alternate, which bounds the commands of the new listeners on the report page; a plain command of the same length costs less.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void LogTextRunsOfAlternatingCommandRetainBoundedHeap()
    {
        // Arrange
        const int count = 2560;
        const int bytes = 1024;
        const long limit = 8192;

        _ = RetainedPerText(1, bytes, true, ReportsView.MaxTextRuns);

        // Act
        var alternating = RetainedPerText(count, bytes, true, ReportsView.MaxTextRuns);
        var plain = RetainedPerText(count, bytes, false, ReportsView.MaxTextRuns);

        // Assert
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained heap of one alternating command of 1,024 bytes with its runs");
        Assert.IsLessThan(alternating, plain, "a plain command costs less than an alternating one, so the measurement sees the runs");
        Assert.IsGreaterThan(0, plain, "the measurement sees the plain texts");
    }

    /// <summary>
    /// A user name of the report cut to 32 bytes costs at most 2,048 bytes of retained heap with its runs of the report page even when plain and escaped characters alternate, which bounds the names of the new listeners on the report page; a plain name of the same length costs less.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void LogTextRunsOfAlternatingShortNameRetainBoundedHeap()
    {
        // Arrange
        const int count = 2560;
        const int bytes = 32;
        const long limit = 2048;

        _ = RetainedPerText(1, bytes, true, ReportsView.MaxTextRuns);

        // Act
        var alternating = RetainedPerText(count, bytes, true, ReportsView.MaxTextRuns);
        var plain = RetainedPerText(count, bytes, false, ReportsView.MaxTextRuns);

        // Assert
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained heap of one alternating name of 32 bytes with its runs");
        Assert.IsLessThan(alternating, plain, "a plain name costs less than an alternating one, so the measurement sees the runs");
        Assert.IsGreaterThan(0, plain, "the measurement sees the plain texts");
    }

    /// <summary>
    /// A text of 1,024 characters retains at most 6,144 bytes with its runs of the incidents view at the 2,048-character cap, and a text of the 512-character timeline cap at most 3,072 bytes, even when plain and escaped characters alternate.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void LogTextRunsOfAlternatingIncidentFieldRetainBoundedHeap()
    {
        // Arrange
        const long limit = 6144;

        _ = RetainedRunsPerText(1, 1024, true, LogText.MaxShortChars, IncidentsView.MaxTextRuns);

        // Act
        var alternating = RetainedRunsPerText(2560, 1024, true, LogText.MaxShortChars, IncidentsView.MaxTextRuns);
        var plain = RetainedRunsPerText(2560, 1024, false, LogText.MaxShortChars, IncidentsView.MaxTextRuns);

        // Assert
        TestContext.WriteLine($"incident field: alternating {alternating} bytes, plain {plain} bytes");
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained runs of one alternating field of 1,024 characters");
        Assert.IsLessThan(alternating, plain, "a plain field costs less than an alternating one, so the measurement sees the runs");
    }

    /// <summary>
    /// A timeline text of 1,024 characters retains at most 3,072 bytes with its runs of the incidents view at the 512-character cap, even when plain and escaped characters alternate.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void LogTextRunsOfAlternatingTimelineTextRetainBoundedHeap()
    {
        // Arrange
        const long limit = 3072;

        _ = RetainedRunsPerText(1, 1024, true, IncidentsView.MaxTimelineTextChars, IncidentsView.MaxTextRuns);

        // Act
        var alternating = RetainedRunsPerText(2560, 1024, true, IncidentsView.MaxTimelineTextChars, IncidentsView.MaxTextRuns);
        var plain = RetainedRunsPerText(2560, 1024, false, IncidentsView.MaxTimelineTextChars, IncidentsView.MaxTextRuns);

        // Assert
        TestContext.WriteLine($"timeline text: alternating {alternating} bytes, plain {plain} bytes");
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained runs of one alternating timeline text of 1,024 characters");
        Assert.IsLessThan(alternating, plain, "a plain text costs less than an alternating one, so the measurement sees the runs");
    }

    /// <summary>
    /// A message of 16,384 characters retains at most 57,344 bytes with its runs of the logs view at 256 runs, even when plain and escaped characters alternate.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void LogTextRunsOfAlternatingLogMessageRetainBoundedHeap()
    {
        // Arrange
        const long limit = 57344;

        _ = RetainedRunsPerText(1, 16384, true, LogText.MaxChars, LogText.MaxRuns);

        // Act
        var alternating = RetainedRunsPerText(256, 16384, true, LogText.MaxChars, LogText.MaxRuns);
        var plain = RetainedRunsPerText(256, 16384, false, LogText.MaxChars, LogText.MaxRuns);

        // Assert
        TestContext.WriteLine($"log message: alternating {alternating} bytes, plain {plain} bytes");
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained runs of one alternating message of 16,384 characters");
        Assert.IsLessThan(alternating, plain, "a plain message costs less than an alternating one, so the measurement sees the runs");
    }

    /// <summary>
    /// A host or program of 1,024 characters retains at most 24,576 bytes with its runs of the logs view at 256 runs and the 2,048-character cap, even when plain and escaped characters alternate.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void LogTextRunsOfAlternatingLogFieldRetainBoundedHeap()
    {
        // Arrange
        const long limit = 24576;

        _ = RetainedRunsPerText(1, 1024, true, LogText.MaxShortChars, LogText.MaxRuns);

        // Act
        var alternating = RetainedRunsPerText(2560, 1024, true, LogText.MaxShortChars, LogText.MaxRuns);
        var plain = RetainedRunsPerText(2560, 1024, false, LogText.MaxShortChars, LogText.MaxRuns);

        // Assert
        TestContext.WriteLine($"log field: alternating {alternating} bytes, plain {plain} bytes");
        Assert.IsLessThanOrEqualTo(limit, alternating, "the retained runs of one alternating field of 1,024 characters");
        Assert.IsLessThan(alternating, plain, "a plain field costs less than an alternating one, so the measurement sees the runs");
    }

    /// <summary>
    /// Builds a text whose characters alternate between two characters.
    /// </summary>
    /// <param name="length">The length in characters</param>
    /// <param name="first">The character at even positions</param>
    /// <param name="second">The character at odd positions</param>
    /// <returns>The text</returns>
    private static string Alternating(int length, char first, char second)
    {
        var characters = new char[length];

        for (var position = 0; position < length; position++)
        {
            characters[position] = position % 2 == 0 ? first : second;
        }

        return new string(characters);
    }

    /// <summary>
    /// Builds a text whose rest after 15 runs, escaped and cut at 2,048 characters, is as long as the source rest: seven pairs of a plain and a control character, a plain
    /// and a control character and 2,022 plain characters.
    /// </summary>
    /// <returns>The text of 2,038 characters</returns>
    private static string EqualLengthRest()
    {
        return Alternating(16, 'a', (char)1) + new string('a', 2022);
    }

    /// <summary>
    /// Measures the heap that texts of the given length retain together with their runs.
    /// </summary>
    /// <param name="count">The number of texts</param>
    /// <param name="length">The length of each text in characters, all of one byte in UTF-8</param>
    /// <param name="alternating">Whether plain and escaped characters alternate, or the text is plain</param>
    /// <param name="maxRuns">The run cap</param>
    /// <returns>The retained bytes of one text</returns>
    private static long RetainedPerText(int count, int length, bool alternating, int maxRuns)
    {
        var before = HeapSampler.RetainedBytes();
        var kept = new List<(string Text, IReadOnlyList<TextRun> Runs)>(count);

        for (var index = 0; index < count; index++)
        {
            var text = alternating ? Alternating(length, 'a', (char)1) : new string('a', length);

            kept.Add((text, LogText.Runs(text, LogText.MaxShortChars, maxRuns)));
        }

        var after = HeapSampler.RetainedBytes();

        GC.KeepAlive(kept);

        return (after - before) / count;
    }

    /// <summary>
    /// Measures the heap that the runs of texts retain, without the source texts: each source is built once, its runs are kept, and the source is released.
    /// </summary>
    /// <param name="count">The number of texts</param>
    /// <param name="length">The length of each text in characters, all of one byte in UTF-8</param>
    /// <param name="alternating">Whether plain and escaped characters alternate, or the text is plain</param>
    /// <param name="maxChars">The output cap</param>
    /// <param name="maxRuns">The run cap</param>
    /// <returns>The retained bytes of the runs of one text</returns>
    private static long RetainedRunsPerText(int count, int length, bool alternating, int maxChars, int maxRuns)
    {
        var text = alternating ? Alternating(length, 'a', (char)1) : new string('a', length);
        var before = HeapSampler.RetainedBytes();
        var kept = new List<IReadOnlyList<TextRun>>(count);

        for (var index = 0; index < count; index++)
        {
            kept.Add(LogText.Runs(text, maxChars, maxRuns));
        }

        var after = HeapSampler.RetainedBytes();

        GC.KeepAlive(kept);
        GC.KeepAlive(text);

        return (after - before) / count;
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("^(\\\\u[0-9a-f]{4}|\\\\U[0-9a-f]{8})+$")]
    private static partial Regex WholeEscapes();

    #endregion // Patterns
}