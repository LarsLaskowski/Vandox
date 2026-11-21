using Vandox.Backend.History;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation
/// <summary>
/// The address of the processes view: the source, the range, the moment and a second point.
/// </summary>
public sealed class ProcessesAddress
{
    #region Properties

    /// <summary>
    /// Gets or sets the source; <see langword="null"/> is the default source of the charts.
    /// </summary>
    public string? Source { get; set; }

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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public ProcessesAddress Copy()
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
    /// Checks whether another address draws the same charts: the same source, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><see langword="true"/> when the charts are the same</returns>
    public bool SameChart(ProcessesAddress other)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325 // Skeleton: removed with the implementation