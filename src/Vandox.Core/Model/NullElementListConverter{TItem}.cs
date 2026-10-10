using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads a JSON array into a list and a <c>null</c> element as an element with every field at its zero value.
/// </summary>
/// <typeparam name="TItem">The element type.</typeparam>
internal sealed class NullElementListConverter<TItem> : JsonConverter<List<TItem>>
    where TItem : class, new()
{
    #region JsonConverter

    /// <inheritdoc />
    public override List<TItem>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    #endregion // JsonConverter
}