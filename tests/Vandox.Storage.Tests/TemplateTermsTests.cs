using System.Text;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="TemplateTerms"/>
/// </summary>
[TestClass]
public class TemplateTermsTests
{
    #region Methods

    /// <summary>
    /// Words with a placeholder are left out, also when the angle brackets are only a part of the word.
    /// </summary>
    [TestMethod]
    public void SearchTextSkipsWordsWithPlaceholders()
    {
        // Arrange
        const string template = "user <ip> failed login for <email> via ssh2 a<b c>d";

        // Act
        var text = TemplateTerms.SearchText(template);

        // Assert
        Assert.AreEqual("user failed login for via ssh2", text, "the words without angle brackets, in template order");
    }

    /// <summary>
    /// Words without a letter or digit and words with a control or format character are left out.
    /// </summary>
    [TestMethod]
    public void SearchTextSkipsWordsWithoutLetterOrWithFormatCharacters()
    {
        // Arrange
        var format = char.ConvertFromUtf32(0x202e);
        var template = $"-- :: evil{format}name 42 ta\u0001b good == end";

        // Act
        var text = TemplateTerms.SearchText(template);

        // Assert
        Assert.AreEqual(0x202e, char.ConvertToUtf32(format, 0), "the input holds U+202E");
        Assert.Contains(format, template, "the template holds the format character");
        Assert.AreEqual("42 good end", text, "only words with a letter or digit and without control or format characters");
    }

    /// <summary>
    /// At most sixteen words are kept, and the joined text stays within 1,024 UTF-8 bytes.
    /// </summary>
    [TestMethod]
    public void SearchTextKeepsAtMostSixteenWordsWithinTheByteLimit()
    {
        // Arrange
        var manyShort = string.Join(' ', Enumerable.Range(0, 20).Select(index => $"w{(char)('a' + index)}"));
        var wide = string.Join(' ', Enumerable.Range(0, 16).Select(index => new string('é', 50) + (char)('a' + index)));

        // Act
        var sixteen = TemplateTerms.SearchText(manyShort);
        var bounded = TemplateTerms.SearchText(wide);

        // Assert
        Assert.HasCount(16, sixteen.Split(' '), "sixteen words of twenty");
        Assert.StartsWith("wa wb wc", sixteen, "the first words");
        Assert.IsLessThanOrEqualTo(1024, Encoding.UTF8.GetByteCount(bounded), "at most 1024 UTF-8 bytes");
        Assert.IsGreaterThan(900, Encoding.UTF8.GetByteCount(bounded), "the words that fit are kept");
        Assert.IsLessThan(16, bounded.Split(' ').Length, "fewer than sixteen words fit into the byte limit");
        Assert.StartsWith(wide.Split(' ')[0], bounded, StringComparison.Ordinal, "the first word comes first");
    }

    /// <summary>
    /// A template longer than 1,020 bytes may have been cut inside its last word, so that word is never used.
    /// </summary>
    [TestMethod]
    public void SearchTextDropsTheLastWordOfALongTemplate()
    {
        // Arrange
        var body = string.Join(' ', Enumerable.Range(0, 8).Select(index => new string((char)('k' + index), 127)));
        var longTemplate = $"{body} tail";
        var shortBody = string.Join(' ', Enumerable.Range(0, 8).Select(index => new string((char)('k' + index), 100)));
        var shortTemplate = $"{shortBody} tail";

        // Act
        var cut = TemplateTerms.SearchText(longTemplate);
        var whole = TemplateTerms.SearchText(shortTemplate);

        // Assert
        Assert.IsGreaterThan(1020, Encoding.UTF8.GetByteCount(longTemplate), "the template is longer than 1020 bytes");
        Assert.AreEqual(body, cut, "the last word is dropped");
        Assert.AreEqual(shortTemplate, whole, "a shorter template keeps its last word");
    }

    /// <summary>
    /// A template without a literal word gives an empty text.
    /// </summary>
    /// <param name="template">The template</param>
    [TestMethod]
    [DataRow("<num> <num>")]
    [DataRow("<ip> <email> <path>")]
    [DataRow("")]
    [DataRow("-- :: ==")]
    public void SearchTextIsEmptyWithoutLiteralWords(string template)
    {
        // Arrange

        // Act
        var text = TemplateTerms.SearchText(template);

        // Assert
        Assert.AreEqual(string.Empty, text, $"no word of '{template}' qualifies");
    }

    #endregion // Methods
}