using System.Text;
using System.Text.Json;

using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="WireKeyCheck"/>
/// </summary>
[TestClass]
public class WireKeyCheckTests
{
    #region Methods

    /// <summary>
    /// Distinct keys are fine, also when the same key sits in sibling objects, at another depth or in a list of objects, and a value that is no object has no keys.
    /// </summary>
    /// <param name="json">The line</param>
    [TestMethod]
    [DataRow("""{"a":1,"b":2}""")]
    [DataRow("""{"a":{"a":{"a":1}}}""")]
    [DataRow("""[{"a":1},{"a":2}]""")]
    [DataRow("""{"x":[{"a":1,"b":2},{"b":1,"a":2}]}""")]
    [DataRow("""{"a":{"b":1},"c":{"b":1}}""")]
    [DataRow("""{"kind":1,"kinds":2}""")]
    [DataRow("""{"a_b":1,"a-b":2}""")]
    [DataRow("""{"a":[[],{}],"b":{}}""")]
    [DataRow("1")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    public void FindViolationAcceptsDistinctKeys(string json)
    {
        // Arrange
        var check = new WireKeyCheck();
        using var document = JsonDocument.Parse(json);

        // Act
        var violation = check.FindViolation(document.RootElement);

        // Assert
        Assert.IsNull(violation, "no violation");
    }

    /// <summary>
    /// Two keys of one object that are equal ignoring ASCII case are a duplicate, also when escaped, at any depth, and also when one of them is <c>null</c>.
    /// </summary>
    /// <param name="json">The line</param>
    [TestMethod]
    [DataRow("""{"a":1,"a":2}""")]
    [DataRow("""{"a":1,"A":2}""")]
    [DataRow("""{"a":{"b":1,"B":2}}""")]
    [DataRow("""{"x":[{"a":1,"A":2}]}""")]
    [DataRow("""{"a":null,"A":1}""")]
    [DataRow("""{"a":1,"b":2,"a":3}""")]
    [DataRow("{\"a\":1,\"\\u0061\":2}")]
    [DataRow("{\"kind\":1,\"\\u006bind\":2}")]
    [DataRow("{\"KIND\":1,\"\\u004BIND\":2}")]
    [DataRow("""{"x":[{"a":1},{"b":1,"b":2}]}""")]
    public void FindViolationReportsDuplicateKey(string json)
    {
        // Arrange
        var check = new WireKeyCheck();
        using var document = JsonDocument.Parse(json);

        // Act
        var violation = check.FindViolation(document.RootElement);

        // Assert
        Assert.AreEqual("duplicate key", violation, "violation");
    }

    /// <summary>
    /// A repeat is found as the 8th, 9th, 10th, 65th and 66th key of an object, and the same keys in a sibling object are fine.
    /// </summary>
    /// <param name="count">The number of keys of the object before the repeat</param>
    [TestMethod]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(64)]
    [DataRow(65)]
    [DataRow(1025)]
    public void FindViolationFindsRepeatAfterManyKeys(int count)
    {
        // Arrange
        var check = new WireKeyCheck();
        var keys = Keys(count);
        using var repeated = JsonDocument.Parse(string.Concat("{\"x\":{", keys, $",\"K{count}\":0}}}}"));
        using var siblings = JsonDocument.Parse(string.Concat("{\"x\":[{", keys, "},{", keys, "}]}"));

        // Act
        var duplicate = check.FindViolation(repeated.RootElement);
        var fine = check.FindViolation(siblings.RootElement);

        // Assert
        Assert.AreEqual("duplicate key", duplicate, "repeat of the last key");
        Assert.IsNull(fine, "the same keys in sibling objects");
    }

    /// <summary>
    /// A key that is not ASCII after unescaping is reported, however it is spelled, and an unpaired surrogate escape does not throw.
    /// </summary>
    /// <param name="key">The JSON text of the key, escapes included, without the quotes</param>
    [TestMethod]
    [DataRow("é")]
    [DataRow("\\u00e9")]
    [DataRow("\\u212aind")]
    [DataRow("\\u017feq")]
    [DataRow("\\u0131")]
    [DataRow("\\u0080")]
    [DataRow("\\ud83d\\ude00")]
    [DataRow("\\ud800")]
    [DataRow("\\udc00")]
    [DataRow("a\\udc00")]
    [DataRow("\\ud800\\u0041")]
    [DataRow("\\ud800a")]
    public void FindViolationReportsKeyThatIsNotAscii(string key)
    {
        // Arrange
        var check = new WireKeyCheck();
        using var inObject = JsonDocument.Parse($"{{\"{key}\":1}}");
        using var nested = JsonDocument.Parse($"{{\"a\":[{{\"b\":{{\"{key}\":1}}}}]}}");

        // Act
        var top = check.FindViolation(inObject.RootElement);
        var deep = check.FindViolation(nested.RootElement);

        // Assert
        Assert.AreEqual("key not ASCII", top, "key at the top");
        Assert.AreEqual("key not ASCII", deep, "key in a nested object");
    }

    /// <summary>
    /// A key whose bytes are no valid UTF-8 is reported as not ASCII and does not throw.
    /// </summary>
    /// <param name="hex">The bytes of the key in hexadecimal</param>
    [TestMethod]
    [DataRow("FF")]
    [DataRow("6B696EFF64")]
    [DataRow("C0AF")]
    [DataRow("EDA080")]
    [DataRow("C3")]
    public void FindViolationReportsKeyOfInvalidUtf8(string hex)
    {
        // Arrange
        var check = new WireKeyCheck();
        byte[] json = [.. Encoding.UTF8.GetBytes("{\"a\":{\""), .. Convert.FromHexString(hex), .. Encoding.UTF8.GetBytes("\":1}}")];
        using var document = JsonDocument.Parse(json);

        // Act
        var violation = check.FindViolation(document.RootElement);

        // Assert
        Assert.AreEqual("key not ASCII", violation, "violation");
    }

    /// <summary>
    /// The first violation in document order is reported.
    /// </summary>
    [TestMethod]
    public void FindViolationReportsFirstViolationInDocumentOrder()
    {
        // Arrange
        var check = new WireKeyCheck();
        using var duplicateFirst = JsonDocument.Parse("{\"a\":1,\"a\":2,\"\\ud800\":3}");
        using var asciiFirst = JsonDocument.Parse("{\"\\ud800\":3,\"a\":1,\"a\":2}");

        // Act
        var duplicate = check.FindViolation(duplicateFirst.RootElement);
        var ascii = check.FindViolation(asciiFirst.RootElement);

        // Assert
        Assert.AreEqual("duplicate key", duplicate, "duplicate before the key that is not ASCII");
        Assert.AreEqual("key not ASCII", ascii, "key that is not ASCII before the duplicate");
    }

    /// <summary>
    /// After a violation at depth 3, in an object of more than 64 keys and in one of fewer, a following call with the same keys once each at the same depths finds nothing.
    /// </summary>
    /// <param name="siblings">The number of keys of the object at depth 3 before the broken key</param>
    /// <param name="reason">The violation</param>
    [TestMethod]
    [DataRow(2, "duplicate key")]
    [DataRow(70, "duplicate key")]
    [DataRow(2, "key not ASCII")]
    [DataRow(70, "key not ASCII")]
    public void FindViolationLeavesNoKeyBehindAfterViolation(int siblings, string reason)
    {
        // Arrange
        var check = new WireKeyCheck();
        var keys = Keys(siblings);
        var broken = reason == "duplicate key" ? "\"K1\":0" : "\"\\ud800\":0";
        using var violating = JsonDocument.Parse(AtDepth3($"{keys},{broken}"));
        using var clean = JsonDocument.Parse(AtDepth3(keys));

        // Act
        var first = check.FindViolation(violating.RootElement);
        var second = check.FindViolation(clean.RootElement);
        var third = check.FindViolation(clean.RootElement);
        var fourth = check.FindViolation(violating.RootElement);

        // Assert
        Assert.AreEqual(reason, first, "the violation is found");
        Assert.IsNull(second, "the same keys once each right after the violation");
        Assert.IsNull(third, "and again");
        Assert.AreEqual(reason, fourth, "the violation is found again");
    }

    /// <summary>
    /// The same keys on consecutive calls of one instance are not duplicates of each other.
    /// </summary>
    [TestMethod]
    public void FindViolationKeepsNoKeyFromOneCallToTheNext()
    {
        // Arrange
        var check = new WireKeyCheck();
        using var line = JsonDocument.Parse("{\"kind\":\"metric\",\"data\":{\"name\":\"cpu\",\"labels\":{\"a\":\"1\"}},\"x\":[{\"a\":1},{\"a\":2}]}");

        // Act
        var results = Enumerable.Range(0, 5).Select(_ => check.FindViolation(line.RootElement)).ToList();

        // Assert
        Assert.IsTrue(results.All(result => result is null), "no violation on any of five calls");
    }

    /// <summary>
    /// A sibling object after an object of more than 64 keys, and the next call, find none of its keys.
    /// </summary>
    [TestMethod]
    public void FindViolationForgetsKeysOfLargeObject()
    {
        // Arrange
        var check = new WireKeyCheck();
        var large = Keys(65);
        using var document = JsonDocument.Parse(string.Concat("{\"x\":[{", large, "},{\"k1\":1,\"k65\":2},{", large, "}]}"));

        // Act
        var first = check.FindViolation(document.RootElement);
        var second = check.FindViolation(document.RootElement);

        // Assert
        Assert.IsNull(first, "siblings of a large object");
        Assert.IsNull(second, "the next call");
    }

    /// <summary>
    /// A line nested 64 deep is walked to its end.
    /// </summary>
    [TestMethod]
    public void FindViolationWalksLineNestedToTheLimit()
    {
        // Arrange
        var check = new WireKeyCheck();
        var nested = string.Concat(string.Concat(Enumerable.Repeat("{\"a\":", WireFormat.MaxDepth - 1)), "{\"b\":1,\"B\":2}", new string('}', WireFormat.MaxDepth - 1));
        var options = new JsonDocumentOptions
                      {
                          MaxDepth = WireFormat.MaxDepth
                      };
        using var document = JsonDocument.Parse(nested, options);

        // Act
        var violation = check.FindViolation(document.RootElement);

        // Assert
        Assert.AreEqual("duplicate key", violation, "the repeat at depth 64");
    }

    /// <summary>
    /// Builds the keys <c>k1</c> to <c>kN</c> with a value each.
    /// </summary>
    /// <param name="count">The number of keys</param>
    /// <returns>The members as JSON text</returns>
    private static string Keys(int count)
    {
        return string.Join(",", Enumerable.Range(1, count).Select(number => $"\"k{number}\":{number}"));
    }

    /// <summary>
    /// Builds a line whose members sit in an object at depth 3.
    /// </summary>
    /// <param name="members">The members as JSON text</param>
    /// <returns>The line</returns>
    private static string AtDepth3(string members)
    {
        return string.Concat("{\"a\":{\"b\":{", members, "}}}");
    }

    #endregion // Methods
}