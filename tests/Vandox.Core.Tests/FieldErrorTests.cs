using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="FieldError"/>
/// </summary>
[TestClass]
public class FieldErrorTests
{
    #region Methods

    /// <summary>
    /// The message names the field and the reason.
    /// </summary>
    [TestMethod]
    public void FieldErrorMessageNamesFieldAndReason()
    {
        // Arrange
        var error = new FieldError("name", "required");

        // Act
        var text = error.ToString();

        // Assert
        Assert.AreEqual("model: name: required", text, "message with a field");
    }

    /// <summary>
    /// The message of an error without a field has no field part.
    /// </summary>
    [TestMethod]
    public void FieldErrorMessageWithoutFieldNamesReasonOnly()
    {
        // Arrange
        var error = new FieldError(string.Empty, "required");

        // Act
        var text = error.Message;

        // Assert
        Assert.AreEqual("model: required", text, "message without a field");
    }

    /// <summary>
    /// A prefix is joined with a dot, or directly before an index.
    /// </summary>
    /// <param name="field">The field</param>
    /// <param name="expected">The prefixed field</param>
    [TestMethod]
    [DataRow("", "data")]
    [DataRow("name", "data.name")]
    [DataRow("[0].name", "data[0].name")]
    public void FieldErrorWithPrefixJoinsPath(string field, string expected)
    {
        // Arrange
        var error = new FieldError(field, "x");

        // Act
        var prefixed = error.WithPrefix("data");

        // Assert
        Assert.AreEqual(expected, prefixed.Field, "prefixed field");
        Assert.AreEqual("x", prefixed.Reason, "reason is kept");
    }

    /// <summary>
    /// Quoting escapes special characters and cuts long names.
    /// </summary>
    [TestMethod]
    public void FieldErrorQuoteNameEscapesAndCuts()
    {
        // Arrange
        var longName = new string('a', ModelLimits.MaxNameBytes + 5);

        // Act
        var quoted = FieldError.QuoteName("a\"b\\c\n\r\t\u0001é");
        var cut = FieldError.QuoteName(longName);

        // Assert
        Assert.AreEqual("\"a\\\"b\\\\c\\n\\r\\t\\u0001é\"", quoted, "escaped name");
        Assert.AreEqual($"\"{new string('a', ModelLimits.MaxNameBytes)}\"...", cut, "cut name");
    }

    #endregion // Methods
}