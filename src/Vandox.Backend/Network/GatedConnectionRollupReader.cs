using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the connection rollups through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedConnectionRollupReader : IConnectionRollupReader
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IConnectionRollupReader _inner;
#pragma warning restore S4487

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedConnectionRollupReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedConnectionRollupReader(IConnectionRollupReader inner, WebReaderGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IConnectionRollupReader

    /// <inheritdoc/>
    public Task<ConnectionRollupResult> ConnectionRollupsAsync(ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <inheritdoc/>
    public Task<ConnectionAddressResult> ConnectionAddressesAsync(ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <inheritdoc/>
    public Task<ProgramSourceList> ConnectionSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // IConnectionRollupReader
}