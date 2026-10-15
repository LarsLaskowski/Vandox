using Microsoft.AspNetCore.WebUtilities;

namespace Vandox.Backend.Reports;

/// <summary>
/// The language the address of the report page or the download asks for.
/// </summary>
public sealed class ReportAddress
{
    #region Constants

    private const string LanguageName = "lang";

    private const string RepeatedText = "The address gives the parameter lang more than once.";

    private const string InvalidText = "The address has an invalid value for the parameter lang.";

    #endregion // Constants

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
        ArgumentNullException.ThrowIfNull(query);

        var address = new ReportAddress();
        var values = QueryHelpers.ParseQuery(query);

        if (values.TryGetValue(LanguageName, out var found))
        {
            if (found.Count > 1)
            {
                address.Error = RepeatedText;
            }
            else if (found[0] is { } language && ReportText.For(language) is not null)
            {
                address.Language = language;
            }
            else
            {
                address.Error = InvalidText;
            }
        }

        return address;
    }

    /// <summary>
    /// Writes the language as a query string.
    /// </summary>
    /// <returns>The query string with the leading question mark</returns>
    public string ToQuery()
    {
        return $"?{LanguageName}={Language}";
    }

    #endregion // Methods
}