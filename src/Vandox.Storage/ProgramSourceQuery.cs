namespace Vandox.Storage;

/// <summary>
/// A read of the sources that have program rollups in a range.
/// </summary>
public sealed class ProgramSourceQuery
{
    #region Properties

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range, exclusive.
    /// </summary>
    public DateTimeOffset To { get; init; }

    #endregion // Properties
}