using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Reads the snapshots of a moment, one kind after the other, so that one decoded snapshot is held at a time.
/// </summary>
public sealed class HistoryMomentReader
{
    #region Fields

    /// <summary>
    /// The reader of the snapshots.
    /// </summary>
    private readonly IHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryMomentReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public HistoryMomentReader(IHistoryReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Picks the closer of the stamps on both sides of a moment: the nearer one, the one at or before the moment on equal distance.
    /// </summary>
    /// <param name="atOrBefore">The stamp at or before the moment</param>
    /// <param name="after">The stamp after the moment</param>
    /// <param name="at">The moment</param>
    /// <returns>The stamp, or <see langword="null"/> when both are missing</returns>
    public static SnapshotStamp? Closest(SnapshotStamp? atOrBefore, SnapshotStamp? after, DateTimeOffset at)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the process and the connection snapshot closest to a moment.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel of the moment</returns>
    public Task<HistoryMoment> ReadAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325, S4487