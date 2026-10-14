using Vandox.Backend.Incidents;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentNoteText"/>
/// </summary>
[TestClass]
public class IncidentNoteTextTests
{
    #region Methods

    /// <summary>
    /// Line ends become line feeds, leading line feeds are removed because a text area drops the first one, a blank text is empty and other text is kept.
    /// </summary>
    /// <param name="input">The text of the form</param>
    /// <param name="expected">The normalized text</param>
    /// <param name="acceptable">Whether the normalized text is acceptable</param>
    [TestMethod]
    [DataRow("one\r\ntwo", "one\ntwo", true, DisplayName = "CR LF")]
    [DataRow("one\rtwo", "one\ntwo", true, DisplayName = "CR")]
    [DataRow("one\ntwo", "one\ntwo", true, DisplayName = "LF")]
    [DataRow("a\r\n\r\nb\rc\nd", "a\n\nb\nc\nd", true, DisplayName = "mixed line ends")]
    [DataRow("\nabc", "abc", true, DisplayName = "leading LF")]
    [DataRow("\r\n\r\nabc", "abc", true, DisplayName = "leading CR LF CR LF")]
    [DataRow("\n\n\nabc\n", "abc\n", true, DisplayName = "leading LFs only")]
    [DataRow("", "", true, DisplayName = "empty")]
    [DataRow("   ", "", true, DisplayName = "spaces")]
    [DataRow("\n \r\n\t ", "", true, DisplayName = "blank lines")]
    [DataRow("a b", "a b", true, DisplayName = "inner space")]
    [DataRow("ab\0cd", "ab\0cd", false, DisplayName = "embedded U+0000")]
    [DataRow("\0abc", "\0abc", false, DisplayName = "leading U+0000")]
    public void IncidentNoteTextNormalizesLineEndsAndBlankText(string input, string expected, bool acceptable)
    {
        // Act
        var normalized = IncidentNoteText.Normalize(input);

        // Assert
        Assert.AreEqual(expected, normalized, "the normalized text");
        Assert.AreEqual(acceptable, IncidentNoteText.IsAcceptable(normalized), "a text with U+0000 is not acceptable, any other text of this table is");
    }

    /// <summary>
    /// A text is acceptable up to 1,800 UTF-16 characters and without U+0000, however many characters in a row.
    /// </summary>
    /// <param name="kind">The kind of the text</param>
    /// <param name="acceptable">Whether the text is acceptable</param>
    [TestMethod]
    [DataRow("empty", true)]
    [DataRow("1800-ascii", true)]
    [DataRow("1800-euro", true)]
    [DataRow("1801-ascii", false)]
    [DataRow("900-surrogate-pairs-and-one", false)]
    [DataRow("nul-leading", false)]
    [DataRow("nul-embedded", false)]
    [DataRow("nul-alone", false)]
    public void IncidentNoteTextAcceptsAtMostEighteenHundredCharactersWithoutNul(string kind, bool acceptable)
    {
        // Arrange
        var text = kind switch
                   {
                       "1800-ascii" => new string('a', 1800),
                       "1800-euro" => new string((char)0x20AC, 1800),
                       "1801-ascii" => new string('a', 1801),
                       "900-surrogate-pairs-and-one" => string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 900)) + "x",
                       "nul-leading" => (char)0 + "abc",
                       "nul-embedded" => "ab" + (char)0 + "c",
                       "nul-alone" => ((char)0).ToString(),
                       _ => string.Empty
                   };

        // Act
        var result = IncidentNoteText.IsAcceptable(text);

        // Assert
        Assert.AreEqual(acceptable, result, kind);
    }

    /// <summary>
    /// The number a note is written for is 1 to 18 ASCII digits above 0, and nothing else: no sign, no space, no other digits, no notation.
    /// </summary>
    [TestMethod]
    public void IncidentNoteTextAcceptsOnlyPlainDigitsForWrittenFor()
    {
        // Arrange
        var accepted = new (string Value, long Expected)[]
                       {
                           ("1", 1L),
                           ("42", 42L),
                           ("007", 7L),
                           ("123456789012345678", 123456789012345678L)
                       };
        var refused = new[]
                      {
                          string.Empty,
                          "0",
                          "000000000000000000",
                          "-1",
                          "+1",
                          " 1",
                          "1 ",
                          "1 2",
                          char.ConvertFromUtf32(0xFF11),
                          char.ConvertFromUtf32(0x0663),
                          "1" + char.ConvertFromUtf32(0xFF11),
                          "1e3",
                          "0x1",
                          "1.0",
                          "1234567890123456789",
                          "9223372036854775807",
                          "1" + (char)0
                      };

        // Act
        var acceptedResults = accepted.Select(item => (Parsed: IncidentNoteText.TryParseWrittenFor(item.Value, out var number), Number: number)).ToArray();
        var refusedResults = refused.Select(value => (Value: value, Parsed: IncidentNoteText.TryParseWrittenFor(value, out var number), Number: number)).ToArray();

        // Assert
        for (var index = 0; index < accepted.Length; index++)
        {
            Assert.IsTrue(acceptedResults[index].Parsed, $"'{accepted[index].Value}' is accepted");
            Assert.AreEqual(accepted[index].Expected, acceptedResults[index].Number, $"the number of '{accepted[index].Value}'");
        }

        foreach (var result in refusedResults)
        {
            Assert.IsFalse(result.Parsed, $"the value of {result.Value.Length} characters starting with U+{(result.Value.Length == 0 ? 0 : result.Value[0]):X4} is refused");
        }
    }

    #endregion // Methods
}