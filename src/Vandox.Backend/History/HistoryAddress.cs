using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The address of the history view: the metrics, the range and the moment.
/// </summary>
public sealed partial class HistoryAddress
{
    #region Constants

    /// <summary>
    /// The most bytes of the source and of the name of a metric.
    /// </summary>
    private const int MaxPartBytes = 128;

    private const string MetricName = "metric";

    private const string RangeName = "range";

    private const string FromName = "from";

    private const string ToName = "to";

    private const string AtName = "at";

    private const string RepeatedFormat = "The address gives the parameter {0} more than once.";

    private const string InvalidFormat = "The address has an invalid value for the parameter {0}.";

    private const string TooManyMetrics = "The address names more than 8 metrics.";

    private const string BothKinds = "The address gives both a preset range and a start or end time.";

    private const string NeedsBoth = "The address needs both a start and an end time.";

    private const string Reversed = "The address has a start time that is not before its end time.";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the metrics, at most eight, distinct.
    /// </summary>
    public IReadOnlyList<SeriesKey> Metrics { get; set; } = [];

    /// <summary>
    /// Gets or sets the preset range.
    /// </summary>
    public HistoryPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets the start of a fixed range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of a fixed range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the moment.
    /// </summary>
    public DateTimeOffset? At { get; set; }

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
                   Preset = Preset,
                   From = From,
                   To = To,
                   At = At,
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
        if (From is not null && To is not null)
        {
            return new HistoryRange(From.Value, To.Value);
        }

        return new HistoryRange(now - HistoryView.PresetSpan(Preset ?? HistoryPreset.OneDay), now);
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

        if (Preset is not null)
        {
            parts.Add($"{RangeName}={HistoryView.PresetValue(Preset.Value)}");
        }

        if (From is not null && To is not null)
        {
            parts.Add($"{FromName}={LogsAddress.FormatTime(From.Value)}");
            parts.Add($"{ToName}={LogsAddress.FormatTime(To.Value)}");
        }

        if (At is not null)
        {
            parts.Add($"{AtName}={LogsAddress.FormatTime(At.Value)}");
        }

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

        return Preset == other.Preset
               && From == other.From
               && To == other.To
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
        return IsValidPart(metric.Source) && IsValidPart(metric.Name);
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
    /// Tells whether a part of a metric is valid.
    /// </summary>
    /// <param name="part">The source or the name</param>
    /// <returns><c>true</c> when the part is valid</returns>
    private static bool IsValidPart(string? part)
    {
        return part is { Length: >= 1 and <= MaxPartBytes } && PartPattern().IsMatch(part);
    }

    /// <summary>
    /// Builds the error of an invalid value.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    private static string Invalid(string name)
    {
        return HistoryView.Fill(InvalidFormat, name);
    }

    /// <summary>
    /// Reads a preset value.
    /// </summary>
    /// <param name="value">The value</param>
    /// <param name="preset">The preset</param>
    /// <returns><c>true</c> when the value names a preset</returns>
    private static bool TryReadPreset(string value, out HistoryPreset preset)
    {
        var found = Enum.GetValues<HistoryPreset>().Cast<HistoryPreset?>().FirstOrDefault(candidate => string.Equals(HistoryView.PresetValue(candidate!.Value), value, StringComparison.Ordinal));

        preset = found.GetValueOrDefault();

        return found is not null;
    }

    /// <summary>
    /// The pattern of the source and of the name of a metric: the name pattern of the wire format, ending in <c>\z</c> so that a trailing line feed is refused.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

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
            return Invalid(MetricName);
        }

        if (Metrics.Count > HistoryView.MaxMetrics)
        {
            return TooManyMetrics;
        }

        if ((From is not null && StorableTime.Contains(From.Value) == false) || (To is not null && StorableTime.Contains(To.Value) == false))
        {
            return Invalid(From is not null && StorableTime.Contains(From.Value) == false ? FromName : ToName);
        }

        if (At is not null && StorableTime.Contains(At.Value) == false)
        {
            return Invalid(AtName);
        }

        return ValidateRange();
    }

    /// <summary>
    /// Checks how the preset, the start and the end go together.
    /// </summary>
    /// <returns>The error, or an empty text</returns>
    private string ValidateRange()
    {
        if (Preset is not null && (From is not null || To is not null))
        {
            return BothKinds;
        }

        if ((From is null) != (To is null))
        {
            return NeedsBoth;
        }

        return From is not null && To is not null && From.Value >= To.Value ? Reversed : string.Empty;
    }

    /// <summary>
    /// Reads the parameters in the order metric, range, from, to, at.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    private string Read(Dictionary<string, StringValues> values)
    {
        var error = ReadMetrics(values);

        foreach (var name in new[] { RangeName, FromName, ToName, AtName })
        {
            if (error.Length > 0)
            {
                return error;
            }

            if (values.TryGetValue(name, out var found))
            {
                error = found.Count > 1 ? HistoryView.Fill(RepeatedFormat, name) : ReadValue(name, found[0] ?? string.Empty);
            }
        }

        return error.Length > 0 ? error : ValidateRange();
    }

    /// <summary>
    /// Reads one single-valued parameter.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <param name="value">The value; empty is no value</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadValue(string name, string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var valid = false;

        if (name == RangeName)
        {
            valid = TryReadPreset(value, out var preset);
            Preset = valid ? preset : null;
        }
        else if (LogsAddress.TryReadTime(value, out var time))
        {
            valid = true;

            switch (name)
            {
                case FromName:
                    From = time;
                    break;

                case ToName:
                    To = time;
                    break;

                default:
                    At = time;
                    break;
            }
        }

        return valid ? string.Empty : Invalid(name);
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
                return Invalid(MetricName);
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