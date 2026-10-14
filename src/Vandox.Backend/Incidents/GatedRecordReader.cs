using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the records through the shared gate of the web views.
/// </summary>
public sealed class GatedRecordReader : IRecordReader
{
    #region Fields

    private readonly IRecordReader _inner;

    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedRecordReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedRecordReader(IRecordReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IRecordReader

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.RecordsAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // IRecordReader
}