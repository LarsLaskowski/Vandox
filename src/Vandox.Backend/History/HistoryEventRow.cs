using Vandox.Backend.Logs;

namespace Vandox.Backend.History;

/// <summary>
/// A row of the event list.
/// </summary>
public sealed class HistoryEventRow
{
    #region Properties

    /// <summary>
    /// Gets the time.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the kind text.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text runs.
    /// </summary>
    public IReadOnlyList<TextRun> Text { get; init; } = [];

    #endregion // Properties
}