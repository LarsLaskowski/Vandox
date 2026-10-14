#pragma warning disable RH2003, S2325

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the records through the shared gate of the web views.
/// </summary>
public sealed class GatedRecordReader : IRecordReader
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedRecordReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedRecordReader(IRecordReader inner, WebReaderGate gate)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region IRecordReader

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IRecordReader
}