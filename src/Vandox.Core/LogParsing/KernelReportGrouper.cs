#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Joins the lines of a multi-line kernel report into one record.
/// </summary>
internal sealed class KernelReportGrouper
{
    #region Constants

    /// <summary>
    /// The most lines of one report.
    /// </summary>
    internal const int MaxLines = 2000;

    /// <summary>
    /// The UTF-8 bytes of the decoded text kept from the start of a report.
    /// </summary>
    internal const int HeadBytes = 8192;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The most time a report without an end line may span.
    /// </summary>
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Flushes an open report; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}