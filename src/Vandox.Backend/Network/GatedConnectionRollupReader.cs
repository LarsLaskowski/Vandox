using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the connection rollups through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedConnectionRollupReader : IConnectionRollupReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IConnectionRollupReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedConnectionRollupReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedConnectionRollupReader(IConnectionRollupReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IConnectionRollupReader

    /// <inheritdoc/>
    public Task<ConnectionRollupResult> ConnectionRollupsAsync(ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ConnectionRollupsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ConnectionAddressResult> ConnectionAddressesAsync(ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ConnectionAddressesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ProgramSourceList> ConnectionSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ConnectionSourcesAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // IConnectionRollupReader
}