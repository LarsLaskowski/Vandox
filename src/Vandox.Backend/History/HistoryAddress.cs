using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The address of the history view: the metrics, the range and the moment.
/// </summary>
public sealed class HistoryAddress
{
    #region Constants

    private const string MetricName = "metric";

    private const string TooManyMetrics = "The address names more than 8 metrics.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The range and the moment.
    /// </summary>
    private AddressTimes _times = new();

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the metrics, at most eight, distinct.
    /// </summary>
    public IReadOnlyList<SeriesKey> Metrics { get; set; } = [];

    /// <summary>
    /// Gets or sets the preset range.
    /// </summary>
    public HistoryPreset? Preset
    {
        get => _times.Preset;
        set => _times.Preset = value;
    }

    /// <summary>
    /// Gets or sets the start of a fixed range.
    /// </summary>
    public DateTimeOffset? From
    {
        get => _times.From;
        set => _times.From = value;
    }

    /// <summary>
    /// Gets or sets the end of a fixed range.
    /// </summary>
    public DateTimeOffset? To
    {
        get => _times.To;
        set => _times.To = value;
    }

    /// <summary>
    /// Gets or sets the moment.
    /// </summary>
    public DateTimeOffset? At
    {
        get => _times.At;
        set => _times.At = value;
    }

    /// <summary>
    /// Gets or sets the text of the first error; empty when the address is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses the query string of the address.
    /// </summary>
    /// <param name="query">The query string, with or without the leading question mark; it is read as the logs view reads its own</param>
    /// <returns>The address; its <see cref="Error"/> names the first error</returns>
    public static HistoryAddress Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = new HistoryAddress();
        var values = QueryHelpers.ParseQuery(query);

        address.Error = address.Read(values);

        return address;
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public HistoryAddress Copy()
    {
        return new HistoryAddress
               {
                   Metrics = [.. Metrics],
                   _times = _times.Copy(),
                   Error = Error
               };
    }

    /// <summary>
    /// Gives the range: the fixed one, else the preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    public HistoryRange Range(DateTimeOffset now)
    {
        return _times.Range(now);
    }

    /// <summary>
    /// Writes the address as a query string after checking every value again.
    /// </summary>
    /// <returns>The query string</returns>
    /// <exception cref="InvalidOperationException">The address has an error or a value the checks refuse</exception>
    public string ToQuery()
    {
        var error = Validate();

        if (error.Length > 0)
        {
            throw new InvalidOperationException(error);
        }

        var parts = new List<string>();

        foreach (var metric in Metrics)
        {
            parts.Add($"{MetricName}={Uri.EscapeDataString(metric.Source)},{Uri.EscapeDataString(metric.Name)}");
        }

        _times.AppendTo(parts);

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Checks whether another address draws the same chart: the same metrics, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><see langword="true"/> when the charts are the same</returns>
    public bool SameChart(HistoryAddress other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _times.SameRange(other._times)
               && Metrics.Count == other.Metrics.Count
               && Metrics.Zip(other.Metrics).All(pair => IsSame(pair.First, pair.Second));
    }

    /// <summary>
    /// Tells whether the address accepts a metric: both parts match the name pattern of the wire format and are 1 to 128 bytes long.
    /// </summary>
    /// <param name="metric">The metric</param>
    /// <returns><c>true</c> when the metric is valid</returns>
    internal static bool IsValidMetric(SeriesKey metric)
    {
        return AddressTimes.IsValidName(metric.Source) && AddressTimes.IsValidName(metric.Name);
    }

    /// <summary>
    /// Tells whether two metrics are the same.
    /// </summary>
    /// <param name="first">The first metric</param>
    /// <param name="second">The second metric</param>
    /// <returns><c>true</c> when source and name are equal</returns>
    private static bool IsSame(SeriesKey first, SeriesKey second)
    {
        return string.Equals(first.Source, second.Source, StringComparison.Ordinal) && string.Equals(first.Name, second.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks every value of the address again.
    /// </summary>
    /// <returns>The first error, or an empty text</returns>
    private string Validate()
    {
        if (Error.Length > 0)
        {
            return Error;
        }

        if (Metrics.Any(metric => IsValidMetric(metric) == false))
        {
            return AddressTimes.Invalid(MetricName);
        }

        if (Metrics.Count > HistoryView.MaxMetrics)
        {
            return TooManyMetrics;
        }

        return _times.Validate();
    }

    /// <summary>
    /// Reads the parameters in the order metric, range, from, to, at.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    private string Read(Dictionary<string, StringValues> values)
    {
        var error = ReadMetrics(values);

        return error.Length > 0 ? error : _times.ReadParameters(values);
    }

    /// <summary>
    /// Reads the metrics, each distinct one once in the order of the address.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadMetrics(Dictionary<string, StringValues> values)
    {
        if (values.TryGetValue(MetricName, out var found) == false)
        {
            return string.Empty;
        }

        var metrics = new List<SeriesKey>();

        foreach (var value in found)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var comma = value.IndexOf(',', StringComparison.Ordinal);
            var metric = comma < 0 ? null : new SeriesKey(value[..comma], value[(comma + 1)..]);

            if (metric is null || IsValidMetric(metric) == false)
            {
                return AddressTimes.Invalid(MetricName);
            }

            if (metrics.Any(known => IsSame(known, metric)) == false)
            {
                metrics.Add(metric);
            }
        }

        Metrics = metrics;

        return metrics.Count > HistoryView.MaxMetrics ? TooManyMetrics : string.Empty;
    }

    #endregion // Methods
}