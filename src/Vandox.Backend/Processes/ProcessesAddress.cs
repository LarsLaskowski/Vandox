using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

/// <summary>
/// The address of the processes view: the source, the range, the moment and a second point.
/// </summary>
public sealed class ProcessesAddress
{
    #region Constants

    private const string SourceName = "source";

    private const string CompareName = "compare";

    private const string NeedsMoment = "The address needs both a moment and a second point for a comparison.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The range and the moment.
    /// </summary>
    private AddressTimes _times = new();

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the source; <see langword="null"/> is the default source of the charts.
    /// </summary>
    public string? Source { get; set; }

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
    /// Gets or sets the second point of a comparison.
    /// </summary>
    public DateTimeOffset? Compare { get; set; }

    /// <summary>
    /// Gets or sets the text of the first error; empty when the address is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses the query string of the address.
    /// </summary>
    /// <param name="query">The query string, with or without the leading question mark</param>
    /// <returns>The address; its <see cref="Error"/> names the first error</returns>
    public static ProcessesAddress Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = new ProcessesAddress();

        address.Error = address.Read(QueryHelpers.ParseQuery(query));

        return address;
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public ProcessesAddress Copy()
    {
        return new ProcessesAddress
               {
                   Source = Source,
                   _times = _times.Copy(),
                   Compare = Compare,
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

        if (Source is not null)
        {
            parts.Add($"{SourceName}={Uri.EscapeDataString(Source)}");
        }

        _times.AppendTo(parts);

        if (Compare is not null)
        {
            parts.Add($"{CompareName}={LogsAddress.FormatTime(Compare.Value)}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Checks whether another address draws the same charts: the same source, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><see langword="true"/> when the charts are the same</returns>
    public bool SameChart(ProcessesAddress other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Source, other.Source, StringComparison.Ordinal) && _times.SameRange(other._times);
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

        if (Source is not null && AddressTimes.IsValidName(Source) == false)
        {
            return AddressTimes.Invalid(SourceName);
        }

        if (Compare is not null && StorableTime.Contains(Compare.Value) == false)
        {
            return AddressTimes.Invalid(CompareName);
        }

        var error = _times.Validate();

        return error.Length > 0 ? error : NeedsMomentError();
    }

    /// <summary>
    /// Tells the error of a second point without a moment.
    /// </summary>
    /// <returns>The error, or an empty text</returns>
    private string NeedsMomentError()
    {
        return Compare is not null && At is null ? NeedsMoment : string.Empty;
    }

    /// <summary>
    /// Reads the parameters in the order source, range, from, to, at, compare, then checks the range and the comparison.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    private string Read(Dictionary<string, StringValues> values)
    {
        var error = ReadSource(values);

        if (error.Length == 0)
        {
            error = _times.ReadValues(values);
        }

        if (error.Length == 0)
        {
            error = ReadCompare(values);
        }

        if (error.Length == 0)
        {
            error = _times.ValidateRange();
        }

        return error.Length > 0 ? error : NeedsMomentError();
    }

    /// <summary>
    /// Reads the source.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadSource(Dictionary<string, StringValues> values)
    {
        if (values.TryGetValue(SourceName, out var found) == false)
        {
            return string.Empty;
        }

        if (found.Count > 1)
        {
            return AddressTimes.Repeated(SourceName);
        }

        var value = found[0] ?? string.Empty;

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (AddressTimes.IsValidName(value) == false)
        {
            return AddressTimes.Invalid(SourceName);
        }

        Source = value;

        return string.Empty;
    }

    /// <summary>
    /// Reads the second point.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadCompare(Dictionary<string, StringValues> values)
    {
        if (values.TryGetValue(CompareName, out var found) == false)
        {
            return string.Empty;
        }

        if (found.Count > 1)
        {
            return AddressTimes.Repeated(CompareName);
        }

        var value = found[0] ?? string.Empty;

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (LogsAddress.TryReadTime(value, out var time) == false)
        {
            return AddressTimes.Invalid(CompareName);
        }

        Compare = time;

        return string.Empty;
    }

    #endregion // Methods
}