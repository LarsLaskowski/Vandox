using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A record writer for tests that build an ingest endpoint and never reach its write.
/// </summary>
internal sealed class UnusedRecordWriter : IRecordWriter
{
    #region IRecordWriter

    /// <inheritdoc />
    public Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("The test reached a write it does not expect.");
    }

    #endregion // IRecordWriter
}