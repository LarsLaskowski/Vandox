namespace Vandox.Storage;

/// <summary>
/// The lines of one series read.
/// </summary>
public sealed class LinePage
{
    #region Properties

    /// <summary>
    /// Gets the lines.
    /// </summary>
    public IReadOnlyList<MetricLine> Lines { get; init; } = [];

    /// <summary>
    /// Gets the flag that lines were cut at the line limit.
    /// </summary>
    public bool LinesCut { get; init; }

    /// <summary>
    /// Gets the flag that the distinct sample times exceeded the limit.
    /// </summary>
    public bool TimesExceeded { get; init; }

    #endregion // Properties
}