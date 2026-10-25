using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestFieldPath"/>
/// </summary>
[TestClass]
public class IngestFieldPathTests
{
    #region Constants

    private const string KeyText = "SENTINEL";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Every quoted map key of a field path becomes an empty pair of brackets, whatever the key holds; list indexes and names stay. The paths are the forms
    /// <c>FieldError</c> produces: quotes and backslashes escaped, a cut key followed by three dots.
    /// </summary>
    /// <param name="field">The field path as the decoder reports it</param>
    /// <param name="expected">The reduced path</param>
    /// <param name="escaped">Whether the path holds a backslash, which the case is about</param>
    [TestMethod]
    [DataRow("kind", "kind", false)]
    [DataRow("", "", false)]
    [DataRow("data.processes[3].command", "data.processes[3].command", false)]
    [DataRow("data.labels[\"SENTINEL\"]", "data.labels[]", false)]
    [DataRow("data.labels[\"SENTINEL KEY\"].value", "data.labels[].value", false)]
    [DataRow("data.a[\"SENTINEL\"].b[\"SENTINEL\"].c[2]", "data.a[].b[].c[2]", false)]
    [DataRow("data.labels[\"SENTINEL\\\"]SENTINEL\"]", "data.labels[]", true)]
    [DataRow("data.labels[\"SENTINEL\\\\\"]", "data.labels[]", true)]
    [DataRow("data.labels[\"SENTINEL\\\\\\\"]SENTINEL\"]", "data.labels[]", true)]
    [DataRow("data.labels[\"SENTINEL]SENTINEL[SENTINEL\"]", "data.labels[]", false)]
    [DataRow("data.labels[\"SENTINEL[\"]", "data.labels[]", false)]
    [DataRow("data.labels[\"SENTINEL\"...]", "data.labels[]", false)]
    [DataRow("data.labels[\"SENTINEL\\\"]\"...]", "data.labels[]", true)]
    [DataRow("data.labels[\"SENTINEL\\n\\u0001\"]", "data.labels[]", true)]
    [DataRow("data.labels[\"\"]", "data.labels[]", false)]
    public void IngestFieldPathReducesMapKeys(string field, string expected, bool escaped)
    {
        // Act
        var reduced = IngestFieldPath.Reduce(field);

        // Assert
        Assert.AreEqual(escaped, field.Contains('\\', StringComparison.Ordinal), "the case still holds its backslash");
        Assert.AreEqual(expected, reduced, $"reduced form of {field}");

        if (reduced is not null)
        {
            Assert.DoesNotContain(KeyText, reduced, "no key text is left");
        }
    }

    /// <summary>
    /// A path that does not parse yields no value, so nothing of it is logged.
    /// </summary>
    /// <param name="field">The field path</param>
    [TestMethod]
    [DataRow("data.labels[\"SENTINEL")]
    [DataRow("data.labels[\"SENTINEL\"")]
    [DataRow("data.labels[\"SENTINEL\\\"]")]
    [DataRow("data.labels[\"SENTINEL\"x]")]
    [DataRow("data.labels[\"SENTINEL\"..]")]
    [DataRow("data.labels[\"SENTINEL\"....]")]
    [DataRow("data.labels[SENTINEL]")]
    [DataRow("data.labels[3")]
    [DataRow("data.labels[\"SENTINEL\"][")]
    public void IngestFieldPathFailsClosedForAPathThatDoesNotParse(string field)
    {
        // Act
        var reduced = IngestFieldPath.Reduce(field);

        // Assert
        Assert.IsNull(reduced, $"no value for {field}");
    }

    #endregion // Methods
}