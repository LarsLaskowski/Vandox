using Vandox.Storage;

namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

/// <summary>
/// The filters of the logs view as they appear in the address.
/// </summary>
public sealed class LogsAddress
{
    #region Properties

    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program (service).
    /// </summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the highest priority to show.
    /// </summary>
    public byte? Severity { get; set; }

    /// <summary>
    /// Gets or sets the template number; 0 for none.
    /// </summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Gets or sets the start of the range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the error of the address; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the filters from a query string.
    /// </summary>
    /// <param name="query">The query string, with or without the leading question mark</param>
    /// <returns>The filters and the error of the address</returns>
    public static LogsAddress Parse(string query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks the current values.
    /// </summary>
    /// <returns>The error text, or an empty text when the values are valid</returns>
    public string Validate()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the filters as a query string.
    /// </summary>
    /// <returns>The query string with its leading question mark, or an empty text when no filter is set</returns>
    /// <exception cref="InvalidOperationException">A value is not valid</exception>
    public string ToQuery()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the store filter of the address.
    /// </summary>
    /// <param name="limit">The page size</param>
    /// <param name="scanLimit">The scan bound</param>
    /// <param name="after">The position to continue from</param>
    /// <returns>The filter</returns>
    public LogFilter ToFilter(int limit, int scanLimit, LogPosition? after)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}