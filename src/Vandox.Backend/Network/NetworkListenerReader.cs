using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the panel of the listeners.
/// </summary>
public sealed class NetworkListenerReader
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The reader of the snapshots.
    /// </summary>
    private readonly IHistoryReader _reader;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkListenerReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public NetworkListenerReader(IHistoryReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Reads the panel of a source, a range and a moment.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="at">The moment, <see langword="null"/> for the latest snapshot in the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel</returns>
    public Task<NetworkListenerView> ReadAsync(string source, HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    #endregion // Methods
}