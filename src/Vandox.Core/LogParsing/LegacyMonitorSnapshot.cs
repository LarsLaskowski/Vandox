using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// One snapshot of the legacy monitoring log.
/// </summary>
internal sealed class LegacyMonitorSnapshot
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyMonitorSnapshot"/> class.
    /// </summary>
    /// <param name="headerLine">The line number of the header</param>
    /// <param name="capturedAt">The instant of the header</param>
    /// <param name="counters">The vmstat counter history of the parse</param>
    internal LegacyMonitorSnapshot(long headerLine, DateTimeOffset capturedAt, Dictionary<string, LegacyCounter> counters)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a line of the snapshot.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The decoded line</param>
    /// <param name="cut">Whether the line was cut</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(long lineNumber, string line, bool cut, IRecordEmitter output, List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Finishes the snapshot: its single skip, or the acceptance and the open lists.
    /// </summary>
    /// <param name="endOfInput">Whether the end of the input closed the snapshot</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(bool endOfInput, IRecordEmitter output, List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}