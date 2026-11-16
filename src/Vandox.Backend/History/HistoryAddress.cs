using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325

/// <summary>
/// The address of the history view: the metrics, the range and the moment.
/// </summary>
public sealed class HistoryAddress
{
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
    /// <param name="query">The query string, with or without the leading question mark</param>
    /// <returns>The address; its <see cref="Error"/> names the first error</returns>
    public static HistoryAddress Parse(string query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public HistoryAddress Copy()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the range: the fixed one, else the preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    public HistoryRange Range(DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the address as a query string after checking every value again.
    /// </summary>
    /// <returns>The query string</returns>
    /// <exception cref="InvalidOperationException">The address has an error or a value the checks refuse</exception>
    public string ToQuery()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks whether another address draws the same chart: the same metrics, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><see langword="true"/> when the charts are the same</returns>
    public bool SameChart(HistoryAddress other)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325