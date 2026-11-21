namespace Vandox.Storage;

/// <summary>
/// A source that has program rollups.
/// </summary>
public sealed class ProgramSource
{
    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the latest bucket of either tier in the range.
    /// </summary>
    public DateTimeOffset LatestBucket { get; init; }

    #endregion // Properties
}