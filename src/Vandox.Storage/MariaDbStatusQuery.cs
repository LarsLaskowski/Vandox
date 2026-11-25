namespace Vandox.Storage;

/// <summary>
/// A query for the MariaDB status on the steps of a time axis.
/// </summary>
public sealed class MariaDbStatusQuery
{
    #region Properties

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the step of the axis.
    /// </summary>
    public TimeSpan Step { get; init; }

    #endregion // Properties
}