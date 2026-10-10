using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Creates converters that read a JSON <c>null</c> as if the key were absent.
/// </summary>
internal sealed class NullAsAbsentConverterFactory : JsonConverterFactory
{
    #region Fields

    private static readonly HashSet<Type> _scalars = [
                                                         typeof(string),
                                                         typeof(bool),
                                                         typeof(byte),
                                                         typeof(short),
                                                         typeof(int),
                                                         typeof(uint),
                                                         typeof(long),
                                                         typeof(ulong),
                                                         typeof(double),
                                                         typeof(DateTimeOffset)
                                                     ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates the converter of a scalar type.
    /// </summary>
    /// <typeparam name="T">The scalar type</typeparam>
    /// <returns>The converter</returns>
    public static JsonConverter<T> CreateScalar<T>()
    {
        var inner = (JsonConverter<T>)JsonSerializerOptions.Default.GetConverter(typeof(T));
        var absent = typeof(T) == typeof(string) ? (T)(object)string.Empty : default!;

        return new NullAsAbsentConverter<T>(inner, absent);
    }

    /// <summary>
    /// Checks whether a type is the item type of a list this factory claims.
    /// </summary>
    /// <param name="type">The type to check</param>
    /// <returns><c>true</c> for a public class with a public parameterless constructor</returns>
    private static bool IsItem(Type type)
    {
        return type.IsClass && type.IsPublic && type != typeof(string) && type.GetConstructor(Type.EmptyTypes) is not null;
    }

    #endregion // Methods

    #region JsonConverter

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        if (_scalars.Contains(typeToConvert))
        {
            return true;
        }

        return typeToConvert.IsGenericType
               && typeToConvert.GetGenericTypeDefinition() == typeof(List<>)
               && IsItem(typeToConvert.GetGenericArguments()[0]);
    }

    #endregion // JsonConverter

    #region JsonConverterFactory

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (CanConvert(typeToConvert))
        {
            if (typeToConvert.IsGenericType)
            {
                return Activator.CreateInstance(typeof(NullElementListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0])) as JsonConverter;
            }

            return typeof(NullAsAbsentConverterFactory).GetMethod(nameof(CreateScalar))?.MakeGenericMethod(typeToConvert).Invoke(null, null) as JsonConverter;
        }

        return null;
    }

    #endregion // JsonConverterFactory
}