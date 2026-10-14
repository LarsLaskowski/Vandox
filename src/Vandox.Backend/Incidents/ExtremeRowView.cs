using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One metric extreme of an incident page.
/// </summary>
public sealed class ExtremeRowView
{
    #region Properties

    /// <summary>
    /// Gets the metric name.
    /// </summary>
    public IReadOnlyList<TextRun> Name { get; init; } = [];

    /// <summary>
    /// Gets the kind of the extreme.
    /// </summary>
    public IReadOnlyList<TextRun> Extreme { get; init; } = [];

    /// <summary>
    /// Gets the source.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; init; } = [];

    /// <summary>
    /// Gets the time of the extreme.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the address of the logs around the time.
    /// </summary>
    public string LogsHref { get; init; } = string.Empty;

    /// <summary>
    /// Gets the value as text.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    #endregion // Properties
}