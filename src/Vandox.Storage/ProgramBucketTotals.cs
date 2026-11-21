namespace Vandox.Storage;

/// <summary>
/// The totals of the values one fold step adds to a program in a bucket.
/// </summary>
internal sealed class ProgramBucketTotals
{
    #region Methods

#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation

    /// <summary>
    /// Adds the values of one snapshot.
    /// </summary>
    /// <param name="rss">The resident memory of the program in the snapshot</param>
    /// <param name="pss">The proportional memory of the program, or <c>null</c> when the snapshot has none</param>
    /// <param name="cpu">The CPU percentage of the program</param>
    internal void Add(double rss, double? pss, double cpu)
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003, S2325

    #endregion // Methods
}