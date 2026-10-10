using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Maps the kind of a record to the type of its payload and reads and writes payloads as JSON.
/// </summary>
public static class PayloadRegistry
{
    #region Fields

    private static readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal)
                                                              {
                                                                  [RecordKind.Metric] = typeof(MetricPoint),
                                                                  [RecordKind.ProcessSnapshot] = typeof(ProcessSnapshot),
                                                                  [RecordKind.ConnectionSnapshot] = typeof(ConnectionSnapshot),
                                                                  [RecordKind.ServiceState] = typeof(ServiceState),
                                                                  [RecordKind.MariaDbStatus] = typeof(MariaDbStatus),
                                                                  [RecordKind.KernelEvent] = typeof(KernelEvent),
                                                                  [RecordKind.LogLine] = typeof(LogLine),
                                                                  [RecordKind.Gap] = typeof(Gap)
                                                              };

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the options every payload is read and written with.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
                                                           {
                                                               DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                                                               Converters = { new NullAsAbsentConverterFactory() }
                                                           };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the type of the payload of a record kind.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The payload type, or <c>null</c> when the kind is unknown</returns>
    public static Type? TypeOf(string kind)
    {
        return _types.GetValueOrDefault(kind);
    }

    /// <summary>
    /// Reads the payload of a record of the given kind.
    /// </summary>
    /// <param name="kind">A known kind</param>
    /// <param name="json">The JSON document of the payload</param>
    /// <returns>The payload, or <c>null</c> when the document is <c>null</c></returns>
    /// <exception cref="JsonException">The document does not fit the payload type</exception>
    public static IPayload? Deserialize(string kind, JsonElement json)
    {
        return (IPayload?)json.Deserialize(_types[kind], Options);
    }

    /// <summary>
    /// Writes a payload as a JSON document.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <returns>The JSON text</returns>
    public static string Serialize(IPayload payload)
    {
        return JsonSerializer.Serialize(payload, payload.GetType(), Options);
    }

    #endregion // Methods
}