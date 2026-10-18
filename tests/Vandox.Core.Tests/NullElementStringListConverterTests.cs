using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="NullElementStringListConverter"/>
/// </summary>
[TestClass]
public class NullElementStringListConverterTests
{
    #region Methods

    /// <summary>
    /// A null element reads as an empty string and the other elements are kept in order.
    /// </summary>
    [TestMethod]
    public void NullElementStringListConverterReadNullElementReadsAsEmptyString()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var result = JsonSerializer.Deserialize<List<string>>("""["tcp",null,"udp"]""", options);

        // Assert
        Assert.IsNotNull(result, "a list is read");
        Assert.AreSequenceEqual(new[] { "tcp", string.Empty, "udp" }, result, "the null element is an empty string");
    }

    /// <summary>
    /// An empty array reads as an empty list.
    /// </summary>
    [TestMethod]
    public void NullElementStringListConverterReadEmptyArrayReadsEmptyList()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var result = JsonSerializer.Deserialize<List<string>>("[]", options);

        // Assert
        Assert.IsNotNull(result, "a list is read");
        Assert.IsEmpty(result, "no element is read");
    }

    /// <summary>
    /// A token that does not start an array is rejected.
    /// </summary>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow("\"tcp\"")]
    [DataRow("{}")]
    [DataRow("5")]
    public void NullElementStringListConverterReadNonArrayThrowsJsonException(string json)
    {
        // Arrange
        var options = CreateOptions();

        // Act
        List<string>? Act()
        {
            return JsonSerializer.Deserialize<List<string>>(json, options);
        }

        // Assert
        Assert.ThrowsExactly<JsonException>(Act, $"{json} is not an array");
    }

    /// <summary>
    /// Writing and reading again returns the same elements.
    /// </summary>
    [TestMethod]
    public void NullElementStringListConverterWriteRoundTripsTheElements()
    {
        // Arrange
        var options = CreateOptions();
        var list = new List<string>
                   {
                       "tcp",
                       "udp6"
                   };

        // Act
        var json = JsonSerializer.Serialize(list, options);
        var result = JsonSerializer.Deserialize<List<string>>(json, options);

        // Assert
        Assert.AreEqual("""["tcp","udp6"]""", json, "the list is written as a JSON array");
        Assert.IsNotNull(result, "a list is read");
        Assert.AreSequenceEqual(list, result, "the elements survive the round trip");
    }

    /// <summary>
    /// Creates serializer options that use the converter.
    /// </summary>
    /// <returns>The options</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();

        options.Converters.Add(new NullElementStringListConverter());

        return options;
    }

    #endregion // Methods
}