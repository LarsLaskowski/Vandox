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
    public const int ScanRows = 50000;

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
}