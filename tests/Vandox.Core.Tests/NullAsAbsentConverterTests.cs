using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="NullAsAbsentConverter{T}"/>
/// </summary>
[TestClass]
public class NullAsAbsentConverterTests
{
    #region Methods

    /// <summary>
    /// A <c>null</c> token reads as the absent value the converter was given, not as the default of the type.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterReadsNullAsAbsentValue()
    {
        // Arrange
        var options = CreateOptions(Create(42));

        // Act
        var value = JsonSerializer.Deserialize<int>("null", options);

        // Assert
        Assert.AreEqual(42, value, "the absent value");
    }

    /// <summary>
    /// A string reads a <c>null</c> token as the given absent text.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterReadsNullStringAsEmptyString()
    {
        // Arrange
        var options = CreateOptions(Create(string.Empty));

        // Act
        var value = JsonSerializer.Deserialize<string>("null", options);

        // Assert
        Assert.AreEqual(string.Empty, value, "the absent string");
    }

    /// <summary>
    /// A date reads a <c>null</c> token as the absent date.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterReadsNullDateAsAbsentDate()
    {
        // Arrange
        var absent = new DateTimeOffset(2000, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var options = CreateOptions(Create(absent));

        // Act
        var value = JsonSerializer.Deserialize<DateTimeOffset>("null", options);

        // Assert
        Assert.AreEqual(absent, value, "the absent date");
    }

    /// <summary>
    /// A token that is not <c>null</c> is read by the inner converter.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterReadsOtherTokensWithInnerConverter()
    {
        // Arrange
        var number = CreateOptions(Create(42));
        var text = CreateOptions(Create(string.Empty));
        var date = CreateOptions(Create(default(DateTimeOffset)));

        // Act
        var parsedNumber = JsonSerializer.Deserialize<int>("7", number);
        var parsedText = JsonSerializer.Deserialize<string>("\"abc\"", text);
        var parsedDate = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-03-01T12:00:00Z\"", date);

        // Assert
        Assert.AreEqual(7, parsedNumber, "number");
        Assert.AreEqual("abc", parsedText, "text");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), parsedDate, "date");
    }

    /// <summary>
    /// A token of the wrong type still fails in the inner converter.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterRefusesTokenOfWrongType()
    {
        // Arrange
        var options = CreateOptions(Create(0));

        // Act
        var exception = Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<int>("\"x\"", options), "string for a number");

        // Assert
        Assert.IsNotNull(exception, "the error is a JsonException");
    }

    /// <summary>
    /// The converter asks the serializer to hand it the <c>null</c> token.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterHandlesNull()
    {
        // Arrange
        var converter = Create(0);

        // Act
        var handles = converter.HandleNull;

        // Assert
        Assert.IsTrue(handles, "null tokens reach the converter");
    }

    /// <summary>
    /// A value is written by the inner converter, so the written JSON does not change.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterWritesValueLikeInnerConverter()
    {
        // Arrange
        var number = CreateOptions(Create(0));
        var text = CreateOptions(Create(string.Empty));

        // Act
        var writtenNumber = JsonSerializer.Serialize(7, number);
        var writtenText = JsonSerializer.Serialize("a b", text);

        // Assert
        Assert.AreEqual("7", writtenNumber, "number");
        Assert.AreEqual("\"a b\"", writtenText, "text");
    }

    /// <summary>
    /// A dictionary key is read and written through the inner converter.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterReadsAndWritesPropertyNameThroughInnerConverter()
    {
        // Arrange
        var converter = Create(0);
        var options = CreateOptions(converter);
        using var stream = new MemoryStream();

        // Act
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            converter.WriteAsPropertyName(writer, 5, options);
            writer.WriteNumberValue(1);
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        var key = ReadKey(converter, options);

        // Assert
        Assert.AreEqual("""{"5":1}""", json, "the key is written as a name");
        Assert.AreEqual(5, key, "the key is read from a name");
    }

    /// <summary>
    /// Creates the converter under test around the converter of the built-in serializer.
    /// </summary>
    /// <typeparam name="T">The converted type</typeparam>
    /// <param name="absent">The value a <c>null</c> token reads as</param>
    /// <returns>The converter</returns>
    private static NullAsAbsentConverter<T> Create<T>(T absent)
    {
        return new NullAsAbsentConverter<T>((JsonConverter<T>)JsonSerializerOptions.Default.GetConverter(typeof(T)), absent);
    }

    /// <summary>
    /// Creates serializer options that use the given converter.
    /// </summary>
    /// <param name="converter">The converter</param>
    /// <returns>The options</returns>
    private static JsonSerializerOptions CreateOptions(JsonConverter converter)
    {
        return new JsonSerializerOptions
               {
                   Converters = { converter }
               };
    }

    /// <summary>
    /// Reads the name of the first property of <c>{"5":1}</c> with the converter.
    /// </summary>
    /// <param name="converter">The converter</param>
    /// <param name="options">The options</param>
    /// <returns>The key</returns>
    private static int ReadKey(NullAsAbsentConverter<int> converter, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader("""{"5":1}"""u8);

        reader.Read();
        reader.Read();

        return converter.ReadAsPropertyName(ref reader, typeof(int), options);
    }

    #endregion // Methods
}