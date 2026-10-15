namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The fixed texts, limits and paths of the report page and its download.
/// </summary>
public static class ReportsView
{
    #region Constants

    /// <summary>
    /// The number of outages the page shows at a time.
    /// </summary>
    public const int PageOutages = 50;

    /// <summary>
    /// The title of the page.
    /// </summary>
    public const string Title = "Forensics report";

    /// <summary>
    /// The status shown while the report is made.
    /// </summary>
    public const string Running = "Making the report… It reads every stored log line, which can take a few minutes on a large database.";

    /// <summary>
    /// The text shown when the run reaches its deadline.
    /// </summary>
    public const string RunDeadline = "Making the report took longer than 2 minutes and was stopped.";

    /// <summary>
    /// The text shown when the run fails.
    /// </summary>
    public const string RunFailure = "The report could not be made. The service log has the details.";

    /// <summary>
    /// The text of the download link.
    /// </summary>
    public const string Download = "Download Markdown";

    /// <summary>
    /// The text of the link to the next outages.
    /// </summary>
    public const string NextOutages = "Next outages";

    /// <summary>
    /// The text of the link back to the first outages.
    /// </summary>
    public const string FirstOutages = "First outages";

    /// <summary>
    /// The format of the range of outages shown: first, last and total.
    /// </summary>
    public const string OutageRange = "Outages {0} to {1} of {2}.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The deadline of a page run and of a download run.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Gets the address of the page in a language.
    /// </summary>
    /// <param name="language">The language, <c>en</c> or <c>de</c></param>
    /// <returns>The relative address</returns>
    public static string PageHref(string language)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the address of the download in a language.
    /// </summary>
    /// <param name="language">The language, <c>en</c> or <c>de</c></param>
    /// <returns>The relative address</returns>
    public static string DownloadHref(string language)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the name of the downloaded file in a language.
    /// </summary>
    /// <param name="language">The language, <c>en</c> or <c>de</c></param>
    /// <returns>The file name</returns>
    public static string FileName(string language)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}