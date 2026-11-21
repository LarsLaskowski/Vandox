namespace Vandox.Storage;

/// <summary>
/// A read of the top programs of one source over a range.
/// </summary>
public sealed class ProgramRollupQuery
{
    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range, exclusive.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the step of the axis.
    /// </summary>
    public TimeSpan Step { get; init; }

    /// <summary>
    /// Gets the measure.
    /// </summary>
    public ProgramMeasure Measure { get; init; }

    /// <summary>
    /// Gets the number of programs to return.
    /// </summary>
    public int Count { get; init; }

    #endregion // Properties
}