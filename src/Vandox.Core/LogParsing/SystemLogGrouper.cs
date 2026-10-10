using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// The stages the journal and syslog parsers pass their records through, in this order: MariaDB entries, then kernel reports.
/// </summary>
internal sealed class SystemLogGrouper
{
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
    /// Emits the open MariaDB entry, then the open kernel report; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}