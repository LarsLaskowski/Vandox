using System.Globalization;
using System.Text;

using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="MarkdownText"/>
/// </summary>
[TestClass]
public class MarkdownTextTests
{
    #region Methods

    /// <summary>
    /// A text becomes a code span with one space of padding on each side.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="expected">The expected span</param>
    [TestMethod]
    [DataRow("mariadbd", "` mariadbd `")]
    [DataRow("a b", "` a b `")]
    [DataRow(" a ", "`  a  `")]
    public void CodeWrapsTextInASpan(string text, string expected)
    {
        // Act
        var span = MarkdownText.Code(text);

        // Assert
        Assert.AreEqual(expected, span, $"span of {text}");
        AssertNoInvisibleCharacters(span);
    }

    /// <summary>
    /// The fence of the span is one backtick longer than the longest run of backticks in the text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="expected">The expected span</param>
    [TestMethod]
    [DataRow("a`b", "`` a`b ``")]
    [DataRow("`x`", "`` `x` ``")]
    [DataRow("`", "`` ` ``")]
    [DataRow("a``b`c", "``` a``b`c ```")]
    [DataRow("a`b````c``d", "````` a`b````c``d `````")]
    public void CodeUsesAFenceLongerThanTheTextsBackticks(string text, string expected)
    {
        // Act
        var span = MarkdownText.Code(text);

        // Assert
        Assert.AreEqual(expected, span, $"span of {text}");
    }

    /// <summary>
    /// Markup in a name stays literal inside the span.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [DataRow("<img src=x onerror=alert(1)>")]
    [DataRow("[a](http://x)")]
    [DataRow("www.example.com")]
    [DataRow("<http://example.com>")]
    [DataRow("*b*")]
    [DataRow("# heading")]
    [DataRow("&amp;")]
    [DataRow("|")]
    [DataRow("<!-- comment -->")]
    [DataRow("😀")]
    public void CodeKeepsMarkupLiteral(string text)
    {
        // Act
        var span = MarkdownText.Code(text);

        // Assert
        Assert.AreEqual($"` {text} `", span, $"span of {text}");
        AssertNoInvisibleCharacters(span);
    }

    /// <summary>
    /// Control, format, separator, private use, unassigned and invalid characters and backslashes are written as escapes.
    /// </summary>
    /// <param name="codePoint">The code point of the one character of the text</param>
    /// <param name="expected">The expected span</param>
    [TestMethod]
    [DataRow(0x202e, @"` \u202e `")]
    [DataRow(0x2028, @"` \u2028 `")]
    [DataRow(0x2029, @"` \u2029 `")]
    [DataRow(0x200b, @"` \u200b `")]
    [DataRow(0x0000, @"` \u0000 `")]
    [DataRow(0x001b, @"` \u001b `")]
    [DataRow(0x007f, @"` \u007f `")]
    [DataRow(0x0085, @"` \u0085 `")]
    [DataRow(0x000a, @"` \n `")]
    [DataRow(0x000d, @"` \r `")]
    [DataRow(0x0009, @"` \t `")]
    [DataRow(0x10fffd, @"` \U0010fffd `")]
    [DataRow(0xe000, @"` \ue000 `")]
    [DataRow(0x0378, @"` \u0378 `")]
    [DataRow(0xfffd, @"` \ufffd `")]
    [DataRow(0x005c, @"` \\ `")]
    public void CodeEscapesInvisibleCharacters(int codePoint, string expected)
    {
        // Arrange
        var text = char.ConvertFromUtf32(codePoint);

        // Act
        var span = MarkdownText.Code(text);

        // Assert
        Assert.AreEqual(codePoint > 0xffff ? 2 : 1, text.Length, "the text is one character");
        Assert.AreEqual(codePoint != 0x5c, text.IndexOf('\\', StringComparison.Ordinal) < 0, "the text holds no backslash, except for the backslash case");
        Assert.AreEqual(expected, span, $"span of U+{codePoint:x4}");
        AssertNoInvisibleCharacters(span);
    }

    /// <summary>
    /// The right-to-left override is one character of the category Format, and the span shows it as six ASCII characters.
    /// </summary>
    [TestMethod]
    public void CodeEscapesTheRightToLeftOverrideAsSixCharacters()
    {
        // Arrange
        var text = char.ConvertFromUtf32(0x202e);

        // Act
        var span = MarkdownText.Code(text);

        // Assert
        Assert.AreEqual(1, text.Length, "the input is one character");
        Assert.AreEqual(UnicodeCategory.Format, CharUnicodeInfo.GetUnicodeCategory(text, 0), "of the category Format");
        Assert.AreEqual(@"` \u202e `", span, "the output");
        Assert.AreEqual(2 + 6 + 2, span.Length, "two fences, two spaces and the six characters of the escape");
        Assert.IsTrue(span.All(character => character < 128), "the escape is ASCII");
        AssertNoInvisibleCharacters(span);
    }

    /// <summary>
    /// A lone surrogate is an invalid character and is escaped like U+FFFD.
    /// </summary>
    [TestMethod]
    public void CodeEscapesALoneSurrogate()
    {
        // Act
        var span = MarkdownText.Code("a" + (char)0xd800 + "b");

        // Assert
        Assert.AreEqual(@"` a\ufffdb `", span, "span");
        AssertNoInvisibleCharacters(span);
    }

    /// <summary>
    /// A text of spaces only is written with its spaces as escapes, so the padding cannot make it look empty.
    /// </summary>
    [TestMethod]
    public void CodeWritesSpacesOnlyAsEscapes()
    {
        // Act
        var three = MarkdownText.Code("   ");
        var one = MarkdownText.Code(" ");

        // Assert
        Assert.AreEqual(@"` \u0020\u0020\u0020 `", three, "three spaces");
        Assert.AreEqual(@"` \u0020 `", one, "one space");
    }

    /// <summary>
    /// An empty text is a dash and no code span.
    /// </summary>
    [TestMethod]
    public void CodeOfEmptyTextIsADash()
    {
        // Act
        var span = MarkdownText.Code(string.Empty);

        // Assert
        Assert.AreEqual(char.ConvertFromUtf32(0x2014), span, "an em dash");
        Assert.DoesNotContain("`", span, "no code span");
    }

    /// <summary>
    /// Asserts that a text holds no control, format, line separator or paragraph separator character.
    /// </summary>
    /// <param name="text">The text</param>
    private static void AssertNoInvisibleCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            Assert.IsFalse(category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator, $"U+{rune.Value:x4} of the category {category} in the output");
        }
    }

    #endregion // Methods
}