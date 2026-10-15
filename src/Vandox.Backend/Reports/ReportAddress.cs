namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The language the address of the report page or the download asks for.
/// </summary>
public sealed class ReportAddress
{
    #region Properties

    /// <summary>
    /// Gets the language, <c>en</c> or <c>de</c>.
    /// </summary>
    public string Language { get; private set; } = "en";

    /// <summary>
    /// Gets the fixed text of the refusal, or an empty text when the address is valid.
    /// </summary>
    public string Error { get; private set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the language from a query string.
    /// </summary>
    /// <param name="query">The query string, with or without the leading question mark</param>
    /// <returns>The address</returns>
    public static ReportAddress Parse(string query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the language as a query string.
    /// </summary>
    /// <returns>The query string with the leading question mark</returns>
    public string ToQuery()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}