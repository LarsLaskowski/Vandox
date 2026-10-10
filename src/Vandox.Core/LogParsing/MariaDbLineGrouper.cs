using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Joins the MariaDB lines that the journal and syslog parsers read (program <c>mariadbd</c> or <c>mysqld</c>) into entries: a
/// header line and the following lines without a header of the same host, program and process within <see cref="MaxSpan"/> of
/// the header line, as long as the message stays within the text limit, with the header and event rules of the MariaDB error
/// log. A line that does not join is passed on unchanged, so no text is dropped. One entry is open at a time, so memory does
/// not depend on the input.
/// </summary>
internal sealed class MariaDbLineGrouper
{
    #region Fields

    /// <summary>
    /// The most time a line of an entry may lie before or after its header line.
    /// </summary>
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order: the record of the open entry when this record ends
    /// it, and this record when it does not join an entry.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Emits the open entry, if any; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}