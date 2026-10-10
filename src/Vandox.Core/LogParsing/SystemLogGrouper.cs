using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The stages the journal and syslog parsers pass their records through, in this order: MariaDB entries, then kernel reports.
/// </summary>
internal sealed class SystemLogGrouper
{
    #region Fields

    private readonly MariaDbLineGrouper _mariaDb = new();
    private readonly KernelReportGrouper _kernel = new();
    private readonly List<DataRecord> _staged = [];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready)
    {
        _mariaDb.Add(record, _staged);
        Forward(ready);
    }

    /// <summary>
    /// Emits the open MariaDB entry, then the open kernel report; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        _mariaDb.Finish(_staged);
        Forward(ready);
        _kernel.Finish(ready);
    }

    /// <summary>
    /// Passes the staged records through the kernel stage in order and empties the staging list.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    private void Forward(List<DataRecord> ready)
    {
        foreach (var staged in _staged)
        {
            _kernel.Add(staged, ready);
        }

        _staged.Clear();
    }

    #endregion // Methods
}