namespace Vandox.Backend.History;

/// <summary>
/// A range of the history view in UTC.
/// </summary>
public sealed class HistoryRange
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryRange"/> class.
    /// </summary>
    /// <param name="start">The start of the range</param>
    /// <param name="end">The end of the range</param>
    public HistoryRange(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the the start of the range.
    /// </summary>
    public DateTimeOffset Start { get; }

    /// <summary>
    /// Gets the the end of the range.
    /// </summary>
    public DateTimeOffset End { get; }

    #endregion // Properties
}