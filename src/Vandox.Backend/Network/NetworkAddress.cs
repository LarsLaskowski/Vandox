using Microsoft.AspNetCore.WebUtilities;

using Vandox.Backend.History;

namespace Vandox.Backend.Network;

/// <summary>
/// The address of the network view: the source, the range and the moment.
/// </summary>
public sealed class NetworkAddress
{
    #region Constants

    private const string SourceName = "source";

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
    /// Gets or sets the first error of the address; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads an address from a query string.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>The address; its error names the first invalid parameter</returns>
    public static NetworkAddress Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = new NetworkAddress();
        var values = QueryHelpers.ParseQuery(query);
        var (error, source) = AddressTimes.ReadName(values, SourceName);

        address.Source = source;

        if (error.Length == 0)
        {
            error = address._times.ReadParameters(values);
        }

        address.Error = error;

        return address;
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public NetworkAddress Copy()
    {
        return new NetworkAddress
               {
                   Source = Source,
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
    /// <returns>The query string, empty for the default address</returns>
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

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Checks whether another address shows the same charts: the same source, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><c>true</c> when the charts are the same</returns>
    public bool SameChart(NetworkAddress other)
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

        return _times.Validate();
    }

    #endregion // Methods
}