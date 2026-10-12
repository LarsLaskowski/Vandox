namespace Vandox.Storage;

/// <summary>
/// Reads the statistics of log templates.
/// </summary>
public interface ILogTemplateReader
{
    #region Methods

    /// <summary>
    /// Returns the template a message of a source and program belongs to.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the template, or <c>null</c> when no stored line has it</returns>
    /// <exception cref="InvalidQueryException">The message is longer than <see cref="Vandox.Core.Model.ModelLimits.MaxTextBytes"/> UTF-8 bytes</exception>
    Task<LogTemplate?> FindTemplateAsync(string source, string program, string message, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the counts per UTC hour of a template, ordered by hour.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the hourly counts</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the templates first seen in a range whose source has earlier lines.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the templates</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<LogTemplate>> NewTemplatesAsync(NewTemplateQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the template hours in a range whose count is above the usual level of the template.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the spikes</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<TemplateSpike>> TemplateSpikesAsync(TemplateSpikeQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}