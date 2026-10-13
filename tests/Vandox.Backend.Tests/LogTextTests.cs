using System.Text.RegularExpressions;

using Vandox.Backend.Logs;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogText"/>
/// </summary>
[TestClass]
public partial class LogTextTests
{
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

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("^(\\\\u[0-9a-f]{4}|\\\\U[0-9a-f]{8})+$")]
    private static partial Regex WholeEscapes();

    #endregion // Patterns
}