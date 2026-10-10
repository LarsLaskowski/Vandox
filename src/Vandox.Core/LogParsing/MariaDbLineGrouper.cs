using System.Text;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Joins the MariaDB lines that the journal and syslog parsers read (program <c>mariadbd</c> or <c>mysqld</c>) into entries: a
/// header line and the following lines without a header of the same host, program and process within <see cref="MaxSpan"/> of
/// the header line, as long as the message stays within the text limit, with the header and event rules of the MariaDB error
/// log. A line that does not join is passed on unchanged, so no text is dropped. One entry is open at a time, so memory does
/// not depend on the input.
/// </summary>
internal sealed class MariaDbLineGrouper
{
    #region Constants

    private const string ServerProgram = "mariadbd";
    private const string LegacyProgram = "mysqld";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The most time a line of an entry may lie before or after its header line.
    /// </summary>
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);

    private readonly MariaDbEventClassifier _classifier = new();
    private readonly byte[] _buffer = new byte[ModelLimits.MaxTextBytes];

    private DataRecord? _record;
    private LogLine? _header;
    private MariaDbLine? _line;
    private MariaDbMessage? _message;
    private string _event = string.Empty;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Tells whether the program of a line is a MariaDB server.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <returns><c>true</c> for <c>mariadbd</c> and <c>mysqld</c></returns>
    internal static bool IsServer(LogLine payload)
    {
        return string.Equals(payload.Program, ServerProgram, StringComparison.Ordinal) || string.Equals(payload.Program, LegacyProgram, StringComparison.Ordinal);
    }

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order: the record of the open entry when this record ends
    /// it, and this record when it does not join an entry.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready)
    {
        if (record.Data is LogLine payload && IsServer(payload) && payload.Message.IndexOf('\n') < 0)
        {
            AddServerLine(record, payload, ready);

            return;
        }

        ready.Add(record);
    }

    /// <summary>
    /// Emits the open entry, if any; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        Flush(ready);
    }

    /// <summary>
    /// Handles a single-line message of a MariaDB server program.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="payload">Its payload</param>
    /// <param name="ready">Receives the records to emit</param>
    private void AddServerLine(DataRecord record, LogLine payload, List<DataRecord> ready)
    {
        var header = ParseHeader(payload.Message);

        if (header is not null)
        {
            Flush(ready);
            Open(record, payload, header);

            return;
        }

        if (Joins(record, payload))
        {
            return;
        }

        if (SameKey(payload))
        {
            Flush(ready);
        }

        ready.Add(record);
    }

    /// <summary>
    /// Parses a message as a MariaDB header.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>The header, or <c>null</c> when the message is no header</returns>
    private MariaDbLine? ParseHeader(string message)
    {
        if (message.Length > 0 && char.IsAsciiDigit(message[0]) && Encoding.UTF8.TryGetBytes(message, _buffer, out var written))
        {
            return MariaDbLine.TryParse(_buffer.AsSpan(0, written));
        }

        return null;
    }

    /// <summary>
    /// Opens an entry with its header line.
    /// </summary>
    /// <param name="record">The record of the header line</param>
    /// <param name="payload">Its payload</param>
    /// <param name="header">The parsed header</param>
    private void Open(DataRecord record, LogLine payload, MariaDbLine header)
    {
        _record = record;
        _header = payload;
        _line = header;
        _event = _classifier.Classify(header);
        _message = new MariaDbMessage(header.Message, payload.Truncated);
    }

    /// <summary>
    /// Tells whether a line has the host, program and process of the open entry.
    /// </summary>
    /// <param name="payload">The payload of the line</param>
    /// <returns><c>true</c> when an entry is open and the key is the same</returns>
    private bool SameKey(LogLine payload)
    {
        return _header is not null && string.Equals(_header.Host, payload.Host, StringComparison.Ordinal) && string.Equals(_header.Program, payload.Program, StringComparison.Ordinal) && _header.Pid == payload.Pid;
    }

    /// <summary>
    /// Takes a line into the open entry when it has the key of the entry, comes within <see cref="MaxSpan"/> of the header line
    /// and fits.
    /// </summary>
    /// <param name="record">The record of the line</param>
    /// <param name="payload">Its payload</param>
    /// <returns><c>true</c> when the line joined</returns>
    private bool Joins(DataRecord record, LogLine payload)
    {
        return SameKey(payload) && (record.CapturedAt - _record!.CapturedAt).Duration() <= MaxSpan && _message!.TryAdd(payload.Message, payload.Truncated);
    }

    /// <summary>
    /// Emits the open entry as one record and closes it.
    /// </summary>
    /// <param name="ready">Receives the record</param>
    private void Flush(List<DataRecord> ready)
    {
        if (_record is null || _header is null || _line is null || _message is null)
        {
            return;
        }

        var message = _message.Build(out var truncated);

        ready.Add(new DataRecord
                  {
                      Origin = _record.Origin,
                      Source = _record.Source,
                      Seq = _record.Seq,
                      CapturedAt = _record.CapturedAt,
                      Data = new LogLine
                             {
                                 Log = _header.Log,
                                 Host = _header.Host,
                                 Program = _header.Program,
                                 Pid = _header.Pid,
                                 Priority = _line.Priority ?? _header.Priority,
                                 Message = message,
                                 Truncated = truncated,
                                 Event = _event
                             }
                  });

        _record = null;
        _header = null;
        _line = null;
        _message = null;
        _event = string.Empty;
    }

    #endregion // Methods
}