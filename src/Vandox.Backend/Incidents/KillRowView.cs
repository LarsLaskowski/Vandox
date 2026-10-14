using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One kill of an incident page.
/// </summary>
public sealed class KillRowView
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
    /// Gets the host.
    /// </summary>
    public IReadOnlyList<TextRun> Host { get; init; } = [];

    /// <summary>
    /// Gets the command of the victim.
    /// </summary>
    public IReadOnlyList<TextRun> Victim { get; init; } = [];

    /// <summary>
    /// Gets the process ID of the victim as text.
    /// </summary>
    public string Pid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the anonymous memory of the victim as text.
    /// </summary>
    public string AnonRss { get; init; } = string.Empty;

    /// <summary>
    /// Gets the OOM score adjustment of the victim as text.
    /// </summary>
    public string OomScoreAdj { get; init; } = string.Empty;

    #endregion // Properties
}