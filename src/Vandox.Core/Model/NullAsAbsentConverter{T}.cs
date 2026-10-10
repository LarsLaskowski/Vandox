using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Reads a JSON <c>null</c> as the absent value of <typeparamref name="T"/> and leaves every other token to the inner converter.
/// </summary>
/// <typeparam name="T">The converted type.</typeparam>
internal sealed class NullAsAbsentConverter<T> : JsonConverter<T>
{
    #region Fields

    private readonly JsonConverter<T> _inner;
    private readonly T _absent;

    #endregion // Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="NullAsAbsentConverter{T}"/> class.
    /// </summary>
    /// <param name="inner">The converter for every token that is not <c>null</c>.</param>
    /// <param name="absent">The value a <c>null</c> token reads as.</param>
    internal NullAsAbsentConverter(JsonConverter<T> inner, T absent)
    {
        _inner = inner;
        _absent = absent;
    }

    #endregion // Constructor

    #region JsonConverter

    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return _absent;
        }

        return _inner.Read(ref reader, typeToConvert, options) ?? _absent;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        _inner.Write(writer, value, options);
    }

    /// <inheritdoc />
    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return _inner.ReadAsPropertyName(ref reader, typeToConvert, options);
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        _inner.WriteAsPropertyName(writer, value, options);
    }

    #endregion // JsonConverter
}