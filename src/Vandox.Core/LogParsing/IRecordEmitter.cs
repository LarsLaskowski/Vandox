using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Receives what a parser reads from one file.
/// </summary>
public interface IRecordEmitter
{
    #region Methods

    /// <summary>
    /// Takes the next record, in the parser's deterministic order (file order except where the parser combines lines into one record). An exception stops the import; the parser lets it pass.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the record is taken</returns>
    ValueTask RecordAsync(DataRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reports input that was not turned into a record.
    /// </summary>
    /// <param name="line">The 1-based line number; 0 when unknown</param>
    /// <param name="reason">A fixed description that never contains input text</param>
    void Skip(long line, string reason);

    #endregion // Methods
}