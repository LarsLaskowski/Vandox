using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One new template or template spike of an incident page.
/// </summary>
public sealed class IncidentTemplateView
{
    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; init; } = [];

    /// <summary>
    /// Gets the program.
    /// </summary>
    public IReadOnlyList<TextRun> Program { get; init; } = [];

    /// <summary>
    /// Gets the template.
    /// </summary>
    public IReadOnlyList<TextRun> Template { get; init; } = [];

    /// <summary>
    /// Gets the time the template was first seen, or the hour of the spike.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the address of the logs around the time.
    /// </summary>
    public string LogsHref { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of lines.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Gets the usual level of a spike as text, or empty for a new template.
    /// </summary>
    public string UsualLevel { get; init; } = string.Empty;

    #endregion // Properties
}