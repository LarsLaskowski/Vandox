using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

#pragma warning disable RH2003, S2325

/// <summary>
/// Creates converters that read a JSON <c>null</c> as if the key were absent.
/// </summary>
internal sealed class NullAsAbsentConverterFactory : JsonConverterFactory
{
    #region JsonConverter

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        throw new NotImplementedException();
    }

    #endregion // JsonConverter

    #region JsonConverterFactory

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    #endregion // JsonConverterFactory
}