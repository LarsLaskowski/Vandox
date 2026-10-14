using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One timeline entry of an incident page.
/// </summary>
public sealed class TimelineRowView
{
    #region Properties

    /// <summary>
    /// Gets the time.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the address of the logs around the time.
    /// </summary>
    public string LogsHref { get; init; } = string.Empty;

    /// <summary>
    /// Gets the CSS class of the marker, or empty for an entry without one.
    /// </summary>
    public string Marker { get; init; } = string.Empty;

    /// <summary>
    /// Gets the type of the entry.
    /// </summary>
    public IReadOnlyList<TextRun> Type { get; init; } = [];

    /// <summary>
    /// Gets the host of the entry.
    /// </summary>
    public IReadOnlyList<TextRun> Host { get; init; } = [];

    /// <summary>
    /// Gets the texts of the entry.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TextRun>> Texts { get; init; } = [];

    /// <summary>
    /// Gets the number of log lines of the entry.
    /// </summary>
    public long LineCount { get; init; }

    #endregion // Properties
}