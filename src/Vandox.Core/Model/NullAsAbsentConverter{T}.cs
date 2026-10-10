using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

#pragma warning disable RH2003, S2325, S1172

/// <summary>
/// Reads a JSON <c>null</c> as the absent value of <typeparamref name="T"/> and leaves every other token to the inner converter.
/// </summary>
/// <typeparam name="T">The converted type.</typeparam>
internal sealed class NullAsAbsentConverter<T> : JsonConverter<T>
{
    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="NullAsAbsentConverter{T}"/> class.
    /// </summary>
    /// <param name="inner">The converter for every token that is not <c>null</c>.</param>
    /// <param name="absent">The value a <c>null</c> token reads as.</param>
    internal NullAsAbsentConverter(JsonConverter<T> inner, T absent)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructor

    #region JsonConverter

    /// <inheritdoc />
    public override bool HandleNull => throw new NotImplementedException();

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    #endregion // JsonConverter
}