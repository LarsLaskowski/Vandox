using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable S4487, S2325, RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Reads the section Long-running queries.
/// </summary>
public sealed class ThreadSnapshotReader
{
    #region Fields

    /// <summary>
    /// The reader of the thread snapshots.
    /// </summary>
    private readonly IMariaDbHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadSnapshotReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the thread snapshots</param>
    public ThreadSnapshotReader(IMariaDbHistoryReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the panel of a range.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="at">The moment of the address, <see langword="null"/> for the latest snapshot</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel</returns>
    public Task<ThreadPanel> ReadAsync(HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore S4487, S2325, RH2003 // Skeleton: removed with the implementation