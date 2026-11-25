using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Reads the service availability through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedServiceHistoryReader : IServiceHistoryReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IServiceHistoryReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedServiceHistoryReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedServiceHistoryReader(IServiceHistoryReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IServiceHistoryReader

    /// <inheritdoc/>
    public Task<ServiceAvailabilityResult> ServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ServiceAvailabilityAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // IServiceHistoryReader
}