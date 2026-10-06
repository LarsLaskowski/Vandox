using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// One numeric measurement.
/// </summary>
public sealed class MetricPoint : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets the name of the metric.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the measured value.
    /// </summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>
    /// Gets or sets the unit of the value.
    /// </summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the labels of the measurement.
    /// </summary>
    [JsonPropertyName("labels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Labels { get; set; }

    #endregion // Properties

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.Metric;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.Name("name", Name) ?? Check.Finite("value", Value) ?? Check.OptionalName("unit", Unit);

        if (error is not null)
        {
            return error;
        }

        if (Labels is null)
        {
            return null;
        }

        if (Labels.Count > ModelLimits.MaxLabels)
        {
            return Check.Invalid("labels", "too many entries");
        }

        foreach (var key in Check.SortedKeys(Labels))
        {
            var path = Check.Keyed("labels", key);

            error = Check.Name(path, key) ?? Check.Short(path, Labels[key]);

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    #endregion // IPayload
}