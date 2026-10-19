using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Reads the stored listener comparisons through the shared gate of the web views.
/// </summary>
public sealed class GatedListenerHistoryReader : IListenerHistoryReader
{
    #region Fields

    private readonly IListenerHistoryReader _inner;

    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedListenerHistoryReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedListenerHistoryReader(IListenerHistoryReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IListenerHistoryReader

    /// <inheritdoc />
    public Task<ListenerTallyPage> ListenerTalliesAsync(CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ListenerTalliesAsync(cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListenerFindingPage> ListenerFindingsAsync(ListenerFindingQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ListenerFindingsAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // IListenerHistoryReader
}