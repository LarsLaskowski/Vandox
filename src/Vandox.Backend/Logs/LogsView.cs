using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// Constants and user texts of the logs view.
/// </summary>
public static class LogsView
{
    #region Constants

    /// <summary>
    /// The number of lines of one page.
    /// </summary>
    public const int PageLines = 100;

    /// <summary>
    /// The scan bound of one search call.
    /// </summary>
    public const int ScanRows = 12000;

    /// <summary>
    /// The number of context lines on each side of a hit.
    /// </summary>
    public const int ContextLines = 5;

    /// <summary>
    /// The number of templates of the template list.
    /// </summary>
    public const int TemplateRows = 200;

    /// <summary>
    /// The number of explorer calls in flight across all circuits.
    /// </summary>
    public const int MaxConcurrentQueries = 2;

    /// <summary>
    /// The fixed title of the template chart.
    /// </summary>
    public const string ChartTitle = "Lines per hour";

    /// <summary>
    /// The fixed label of the template chart series.
    /// </summary>
    public const string ChartSeries = "Lines";

    /// <summary>
    /// The column header of the count in the range.
    /// </summary>
    public const string CountInRangeHeader = "Lines in range (whole hours)";

    /// <summary>
    /// The error of a repeated parameter; the argument is the parameter name.
    /// </summary>
    public const string AddressRepeatedFormat = "The address gives the parameter {0} more than once.";

    /// <summary>
    /// The error of an invalid value; the argument is the parameter name.
    /// </summary>
    public const string AddressInvalidValueFormat = "The address has an invalid value for the parameter {0}.";

    /// <summary>
    /// The error of a start time that is not before the end time.
    /// </summary>
    public const string AddressRangeReversed = "The address has a start time that is not before its end time.";

    /// <summary>
    /// The text shown while a search runs.
    /// </summary>
    public const string Searching = "Searching…";

    /// <summary>
    /// The error of a refused search; the argument is the rule.
    /// </summary>
    public const string SearchRefusedFormat = "The search cannot run: {0}.";

    /// <summary>
    /// The error of a search that ran into the deadline.
    /// </summary>
    public const string SearchDeadline = "The search took longer than 10 seconds and was stopped. Narrow the time range or add filters.";

    /// <summary>
    /// The error of a failed read.
    /// </summary>
    public const string LogsReadFailed = "The logs could not be read. The service log has the details.";

    /// <summary>
    /// The text of an unknown template.
    /// </summary>
    public const string TemplateUnknown = "This template does not exist. Template numbers change when the statistics are rebuilt.";

    /// <summary>
    /// The text of a search ended by the scan bound; the argument is the time.
    /// </summary>
    public const string SearchedUpToFormat = "Searched up to {0}.";

    /// <summary>
    /// The error of a template query that ran into the deadline.
    /// </summary>
    public const string QueryDeadline = "The query took longer than 10 seconds and was stopped. Narrow the time range or choose a source.";

    /// <summary>
    /// The error of a failed template read.
    /// </summary>
    public const string TemplatesReadFailed = "The templates could not be read. The service log has the details.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time after which a search or query is stopped.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Writes a time the way the pages show times: UTC with milliseconds.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    public static string ShowTime(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a time the way the pages show first and last seen: UTC with seconds.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    public static string ShowSeconds(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a user text of this class with one argument.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="argument">The argument</param>
    /// <returns>The text</returns>
    public static string Format(string format, string argument)
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, argument);
    }

    /// <summary>
    /// Gets the text for a query that the store refused: the rule without the prefix of the store.
    /// </summary>
    /// <param name="exception">The refusal</param>
    /// <returns>The user text</returns>
    public static string Refused(InvalidQueryException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string prefix = "store: invalid query: ";

        var message = exception.Message;

        return Format(SearchRefusedFormat, message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message);
    }

    /// <summary>
    /// Lists the sources a form can offer: the stored ones that an address can name, and the one the address names now.
    /// </summary>
    /// <param name="sources">The stored sources</param>
    /// <param name="current">The source of the address, or an empty text</param>
    /// <returns>The sources in order</returns>
    public static IReadOnlyList<string> SourceOptions(IReadOnlyList<string> sources, string current)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var options = sources.Where(IsOffered).ToList();

        if (current.Length > 0 && options.Contains(current))
        {
            return options;
        }

        if (current.Length > 0)
        {
            options.Add(current);
        }

        return options;
    }

    /// <summary>
    /// Tells whether an address can name a source.
    /// </summary>
    /// <param name="source">The source</param>
    /// <returns><c>true</c> when the source passes the rules of the address</returns>
    private static bool IsOffered(string source)
    {
        var address = new LogsAddress
                      {
                          Source = source
                      };

        return address.Validate().Length == 0;
    }

    #endregion // Methods
}