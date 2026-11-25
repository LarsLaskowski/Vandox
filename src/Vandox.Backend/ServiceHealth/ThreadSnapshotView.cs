using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Builds the section Long-running queries from the snapshot stamps and one snapshot.
/// </summary>
public static class ThreadSnapshotView
{
    #region Methods

    /// <summary>
    /// Builds the panel.
    /// </summary>
    /// <param name="stamps">The snapshots of the range, newest first</param>
    /// <param name="shown">The snapshot shown, <see langword="null"/> for none</param>
    /// <param name="at">The moment of the address, <see langword="null"/> for none</param>
    /// <returns>The panel</returns>
    public static ThreadPanel Build(IReadOnlyList<ThreadSnapshotStamp> stamps, ThreadSnapshot? shown, DateTimeOffset? at)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003 // Skeleton: removed with the implementation