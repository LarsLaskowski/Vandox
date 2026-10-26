namespace Vandox.Storage;

/// <summary>
/// Counts the records of a batch that were stored and those that were already present.
/// </summary>
/// <param name="Stored">The number of records stored</param>
/// <param name="Duplicates">The number of records that were already present</param>
public readonly record struct WriteResult(int Stored, int Duplicates)
{
    #region Properties

    /// <summary>
    /// Gets what the batch changed in the agent's gaps and clock state, or <see langword="null"/> when nothing of note.
    /// </summary>
    public TimelineChange? Timeline { get; init; }

    #endregion // Properties
}