using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ParserRegistry"/> and <see cref="ParserTypes"/>
/// </summary>
[TestClass]
public class ParserRegistryTests
{
    #region Methods

    /// <summary>
    /// The parser with the highest confidence wins; on equal confidence the earlier one.
    /// </summary>
    [TestMethod]
    public void ParserRegistryDetectPicksHighestConfidenceThenEarliest()
    {
        // Arrange
        var name = new FakeParser("syslog", Confidence.MatchName);
        var content = new FakeParser("kern", Confidence.MatchContent);
        var first = new FakeParser("mail", Confidence.MatchName);
        var second = new FakeParser("mail.log", Confidence.MatchName);
        var registry = new ParserRegistry([name, content, first, second]);

        // Act
        var byName = registry.Detect(new LogFile("var/syslog", null), []);
        var byContent = registry.Detect(new LogFile("kern", null), []);
        var tie = registry.Detect(new LogFile("mail.log", null), []);
        var none = registry.Detect(new LogFile("other", null), []);

        // Assert
        Assert.AreSame(name, byName.Parser, "name match");
        Assert.AreEqual(Confidence.MatchName, byName.Confidence, "name confidence");
        Assert.AreSame(content, byContent.Parser, "content match");
        Assert.AreSame(first, tie.Parser, "the earlier parser wins a tie");
        Assert.IsNull(none.Parser, "no parser");
        Assert.AreEqual(Confidence.NoMatch, none.Confidence, "no confidence");
        Assert.AreEqual("syslog,kern,mail,mail.log", string.Join(',', registry.GetTypes()), "types in registration order");
    }

    /// <summary>
    /// Null parsers, invalid types and duplicate types are refused.
    /// </summary>
    [TestMethod]
    public void ParserRegistryRefusesInvalidParsers()
    {
        // Act
        var nullParser = Assert.ThrowsExactly<ArgumentException>(() => new ParserRegistry([null]), "null parser");
        var badType = Assert.ThrowsExactly<ArgumentException>(() => new ParserRegistry([new FakeParser("Bad Type", Confidence.MatchName)]), "bad type");
        var twice = Assert.ThrowsExactly<ArgumentException>(() => new ParserRegistry([new FakeParser("a", Confidence.MatchName), new FakeParser("a", Confidence.MatchName)]), "duplicate type");

        // Assert
        Assert.StartsWith("logparse: invalid parser: parser 0 is null", nullParser.Message, "null message");
        Assert.StartsWith("logparse: invalid parser: type \"Bad Type\" must match", badType.Message, "type message");
        Assert.StartsWith("logparse: invalid parser: type \"a\" registered twice", twice.Message, "duplicate message");
    }

    /// <summary>
    /// The parser type rule accepts lower-case names up to 64 characters.
    /// </summary>
    /// <param name="type">The type</param>
    /// <param name="valid">Whether it is valid</param>
    [TestMethod]
    [DataRow("syslog", true)]
    [DataRow("a", true)]
    [DataRow("mail.log_v2-x", true)]
    [DataRow("", false)]
    [DataRow("1abc", false)]
    [DataRow("Abc", false)]
    [DataRow("a b", false)]
    [DataRow("abc\n", false)]
    public void ParserTypesIsValidChecksPattern(string type, bool valid)
    {
        // Act
        var result = ParserTypes.IsValid(type);

        // Assert
        Assert.AreEqual(valid, result, "validity");
    }

    /// <summary>
    /// The longest valid type has 64 characters.
    /// </summary>
    [TestMethod]
    public void ParserTypesIsValidLimitsLength()
    {
        // Act
        var longest = ParserTypes.IsValid(new string('a', 64));
        var tooLong = ParserTypes.IsValid(new string('a', 65));

        // Assert
        Assert.IsTrue(longest, "64 characters");
        Assert.IsFalse(tooLong, "65 characters");
    }

    #endregion // Methods
}