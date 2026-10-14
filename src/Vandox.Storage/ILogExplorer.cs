namespace Vandox.Storage;

/// <summary>
/// Explores stored log lines for the logs view: filtered pages, the context of a line, sources and templates.
/// </summary>
public interface ILogExplorer
{
    #region Methods

    /// <summary>
    /// Returns one page of the log lines matching every filter, ordered by capture time then ID.
    /// </summary>
    /// <param name="filter">The filters</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the page</returns>
    /// <exception cref="InvalidQueryException">A filter breaks a rule</exception>
    Task<LogPage> FindLogsAsync(LogFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a log line with its neighbours in the same log.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the context</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule or the line does not exist</exception>
    Task<LogContext> LogContextAsync(LogContextQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct sources of stored log lines.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the sources in ordinal order</returns>
    Task<IReadOnlyList<string>> LogSourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns one template.
    /// </summary>
    /// <param name="id">The template ID</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the template, or <c>null</c> when there is none</returns>
    /// <exception cref="InvalidQueryException">The ID is not positive</exception>
    Task<LogTemplate?> TemplateAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the templates with lines in a range.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the templates, most lines first</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<TemplateCount>> TemplatesAsync(TemplateListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the hours of a range in which a source has lines.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the start of each hour, ascending</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<DateTimeOffset>> SourceHoursAsync(SourceHourQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the counts per UTC hour of a template, ordered by hour.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the hourly counts</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}