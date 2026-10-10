using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="NullElementListConverter{TProcessConnections}"/>
/// </summary>
[TestClass]
public class NullElementListConverterTests
{
    #region Methods

    /// <summary>
    /// A <c>null</c> element reads as an element with every field at its zero value, and the other elements are read as they are.
    /// </summary>
    [TestMethod]
    public void NullElementListConverterReadsNullElementAsEmptyElement()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var list = JsonSerializer.Deserialize<List<ProcessConnections>>("""[null,{"pid":3},null]""", options);

        // Assert
        Assert.IsNotNull(list, "the list is read");
        Assert.HasCount(3, list, "every element is kept");
        Assert.IsNotNull(list[0], "the first element is not null");
        Assert.AreEqual(0, list[0].Pid, "the first element has every field at zero");
        Assert.IsNotNull(list[1], "the second element is not null");
        Assert.AreEqual(3, list[1].Pid, "the second element is read as it is");
        Assert.IsNotNull(list[2], "the last element is not null");
        Assert.AreEqual(0, list[2].Pid, "the last element has every field at zero");
    }

    /// <summary>
    /// A list of objects without a <c>null</c> reads like the built-in converter reads it.
    /// </summary>
    [TestMethod]
    public void NullElementListConverterReadsMixedListWithoutNulls()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var list = JsonSerializer.Deserialize<List<ProcessConnections>>("""[{"pid":1},{},{"pid":2}]""", options);

        // Assert
        Assert.IsNotNull(list, "the list is read");
        Assert.AreSequenceEqual([1, 0, 2], list.Select(item => item.Pid).ToList(), "numbers in order");
    }

    /// <summary>
    /// An empty array reads as an empty list.
    /// </summary>
    [TestMethod]
    public void NullElementListConverterReadsEmptyArray()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var list = JsonSerializer.Deserialize<List<ProcessConnections>>("[]", options);

        // Assert
        Assert.IsNotNull(list, "the list is read");
        Assert.IsEmpty(list, "no element");
    }

    /// <summary>
    /// A <c>null</c> list stays <c>null</c>: only its elements are read as absent.
    /// </summary>
    [TestMethod]
    public void NullElementListConverterKeepsNullListNull()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var list = JsonSerializer.Deserialize<List<ProcessConnections>>("null", options);

        // Assert
        Assert.IsNull(list, "a null list stays null");
    }

    /// <summary>
    /// A token that is not an array is refused with a <see cref="JsonException"/>.
    /// </summary>
    /// <param name="json">The JSON text</param>
    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"pid":1}""")]
    [DataRow("5")]
    [DataRow("\"x\"")]
    [DataRow("true")]
    public void NullElementListConverterRefusesTokenThatIsNoArray(string json)
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var exception = Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<List<ProcessConnections>>(json, options), "not an array");

        // Assert
        Assert.IsNotNull(exception, "the error is a JsonException");
    }

    /// <summary>
    /// An element of the wrong type is still refused with a <see cref="JsonException"/>.
    /// </summary>
    /// <param name="json">The JSON text</param>
    [TestMethod]
    [DataRow("[5]")]
    [DataRow("""["x"]""")]
    [DataRow("""[{"pid":"x"}]""")]
    [DataRow("""[null,[]]""")]
    public void NullElementListConverterRefusesElementOfWrongType(string json)
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var exception = Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<List<ProcessConnections>>(json, options), "wrong element");

        // Assert
        Assert.IsNotNull(exception, "the error is a JsonException");
    }

    /// <summary>
    /// The list is written as a JSON array of its elements.
    /// </summary>
    [TestMethod]
    public void NullElementListConverterWritesElements()
    {
        // Arrange
        var options = CreateOptions();
        var first = new ProcessConnections
                    {
                        Pid = 1,
                        Command = "a",
                        Count = 2
                    };
        var second = new ProcessConnections
                     {
                         Pid = 2,
                         Command = "b",
                         Count = 0
                     };
        List<ProcessConnections> list = [first, second];

        // Act
        var json = JsonSerializer.Serialize(list, options);
        var empty = JsonSerializer.Serialize(new List<ProcessConnections>(), options);

        // Assert
        Assert.AreEqual("""[{"pid":1,"command":"a","count":2},{"pid":2,"command":"b","count":0}]""", json, "elements are written in order");
        Assert.AreEqual("[]", empty, "an empty list is an empty array");
    }

    /// <summary>
    /// Creates serializer options that use the converter under test for a list of <see cref="ProcessConnections"/>.
    /// </summary>
    /// <returns>The options</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
               {
                   Converters = { new NullElementListConverter<ProcessConnections>() }
               };
    }

    #endregion // Methods
}