using System.Text.Json;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="BatchWriter"/>
/// </summary>
[TestClass]
public class BatchWriterTests
{
    #region Methods

    /// <summary>
    /// The labels are stored exactly as the serializer would write them with the keys in ordinal order, so rows written before and
    /// after the writer was optimized (record 0082) are alike.
    /// </summary>
    /// <param name="first">The first label key</param>
    /// <param name="second">The second label key</param>
    /// <param name="value">The label value</param>
    [TestMethod]
    [DataRow("cpu", "a", "0")]
    [DataRow("b", "B", "x<y>&'z\"")]
    [DataRow("mount", "device", "sda1/ü€😀")]
    [DataRow("é", "e", "tab\there\nnewline")]
    public void BatchWriterSerializesLabelsInOrdinalOrder(string first, string second, string value)
    {
        // Arrange
        var labels = new Dictionary<string, string>
                     {
                         [first] = value,
                         [second] = "other"
                     };
        var expected = JsonSerializer.Serialize(new SortedDictionary<string, string>(labels, StringComparer.Ordinal));

        // Act
        var json = BatchWriter.SerializeLabels(labels);

        // Assert
        Assert.AreEqual(expected, json, "stored text");
    }

    /// <summary>
    /// A single label and repeated use of the same serializer give the same text.
    /// </summary>
    [TestMethod]
    public void BatchWriterSerializesSingleLabel()
    {
        // Arrange
        var labels = new Dictionary<string, string>
                     {
                         ["only"] = "one"
                     };

        // Act and assert
        Assert.AreEqual("""{"only":"one"}""", BatchWriter.SerializeLabels(labels), "single label");
        Assert.AreEqual("""{"only":"one"}""", BatchWriter.SerializeLabels(labels), "second call");
    }

    #endregion // Methods
}