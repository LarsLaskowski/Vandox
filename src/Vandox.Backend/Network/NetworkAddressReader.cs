using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the table of the top remote addresses.
/// </summary>
public sealed class NetworkAddressReader
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The reader of the rollups.
    /// </summary>
    private readonly IConnectionRollupReader _reader;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkAddressReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the rollups</param>
    public NetworkAddressReader(IConnectionRollupReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Reads the table of a source and a range.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="step">The length of a step of the charts</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the table</returns>
    public Task<NetworkAddresses> ReadAsync(string source, HistoryRange range, TimeSpan step, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    #endregion // Methods
}