namespace Vandox.Storage;

/// <summary>
/// The totals of the snapshots one fold step adds to the bucket of a source.
/// </summary>
internal sealed class ProgramSourceTotals
{
    #region Methods

#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation

    /// <summary>
    /// Includes one snapshot.
    /// </summary>
    /// <param name="rss">The resident memory of all programs of the snapshot</param>
    /// <param name="pss">The proportional memory of all programs, or <c>null</c> when the snapshot has none</param>
    /// <param name="cpu">The CPU percentage of all programs</param>
    /// <param name="incomplete">Whether the snapshot's programs do not account for all of its processes</param>
    internal void Include(double rss, double? pss, double cpu, bool incomplete)
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003, S2325

    #endregion // Methods
}