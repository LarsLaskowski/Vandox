using System.Globalization;
using System.Text;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Joins the lines of a multi-line kernel report into one record. The grouper keeps the head and the tail of the report,
/// so its memory does not depend on the length of the report.
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

    private const string KernelProgram = "kernel";
    private const int MarkerReserveBytes = 32;
    private const int SeparatorBytes = 1;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The most time a report without an end line may span.
    /// </summary>
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);

    private static readonly string[] _oomEnds = ["Out of memory: Killed process", "Memory cgroup out of memory: Killed process", "Out of memory and no killable processes"];

    private readonly List<string> _head = [];
    private readonly Queue<(string Text, int Bytes)> _tail = new();
    private DataRecord? _first;
    private bool _oom;
    private int _lines;
    private byte? _priority;
    private bool _truncated;
    private int _headBytes;
    private bool _headClosed;
    private int _tailBytes;
    private int _omitted;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Passes the records to the emitter in order and empties the list.
    /// </summary>
    /// <param name="ready">The records</param>
    /// <param name="output">Receives the records</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the emitter took all records</returns>
    internal static async Task EmitAsync(List<DataRecord> ready, IRecordEmitter output, CancellationToken cancellationToken)
    {
        foreach (var record in ready)
        {
            await output.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        }

        ready.Clear();
    }

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready)
    {
        if (record.Data is not LogLine line || line.Program != KernelProgram)
        {
            ready.Add(record);

            return;
        }

        if (_first is not null)
        {
            if (((LogLine)_first.Data!).Host != line.Host)
            {
                ready.Add(record);

                return;
            }

            if (Joins(record, line))
            {
                Append(line);

                if (IsEnd(line.Message) || _lines >= MaxLines)
                {
                    Flush(ready);
                }

                return;
            }

            Flush(ready);
        }

        if (IsStart(line.Message))
        {
            Open(record, line);
        }
        else
        {
            ready.Add(record);
        }
    }

    /// <summary>
    /// Flushes an open report; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready)
    {
        Flush(ready);
    }

    /// <summary>
    /// Tells whether a kernel message opens a report.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for an OOM report or a <c>cut here</c> report</returns>
    private static bool IsStart(string message)
    {
        return message.Contains("invoked oom-killer:", StringComparison.Ordinal) || message.Contains("------------[ cut here ]------------", StringComparison.Ordinal);
    }

    /// <summary>
    /// Tells whether a kernel line of the host of the open report belongs to it: it is no start line and comes within
    /// <see cref="MaxSpan"/> of the first line.
    /// </summary>
    /// <param name="record">The record of the line</param>
    /// <param name="line">Its payload</param>
    /// <returns><c>true</c> when the line joins the report</returns>
    private bool Joins(DataRecord record, LogLine line)
    {
        if (IsStart(line.Message))
        {
            return false;
        }

        return record.CapturedAt - _first!.CapturedAt <= MaxSpan;
    }

    /// <summary>
    /// Opens a report with its first line.
    /// </summary>
    /// <param name="record">The record of the first line</param>
    /// <param name="line">Its payload</param>
    private void Open(DataRecord record, LogLine line)
    {
        _first = record;
        _oom = line.Message.Contains("invoked oom-killer:", StringComparison.Ordinal);
        Append(line);
    }

    /// <summary>
    /// Tells whether a message ends the open report.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> when it holds an end marker of the report type</returns>
    private bool IsEnd(string message)
    {
        return _oom ? _oomEnds.Any(marker => message.Contains(marker, StringComparison.Ordinal)) : message.Contains("---[ end trace ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds a line to the open report.
    /// </summary>
    /// <param name="line">The payload of the line</param>
    private void Append(LogLine line)
    {
        _lines++;
        _truncated |= line.Truncated;

        if (line.Priority is not null && (_priority is null || line.Priority < _priority))
        {
            _priority = line.Priority;
        }

        var text = line.Message;

        if (_headClosed)
        {
            AppendTail(text);
        }
        else
        {
            AppendHead(text);
        }
    }

    /// <summary>
    /// Adds a line to the head, or to the tail when the head is full; the first line is cut to the head budget.
    /// </summary>
    /// <param name="text">The message of the line</param>
    private void AppendHead(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var cost = _head.Count == 0 ? bytes : bytes + SeparatorBytes;

        if (_headBytes + cost <= HeadBytes)
        {
            _head.Add(text);
            _headBytes += cost;

            return;
        }

        _headClosed = true;

        if (_head.Count > 0)
        {
            AppendTail(text);

            return;
        }

        text = Utf8Text.Cut(text, HeadBytes, out var cut);
        _truncated |= cut;
        _head.Add(text);
        _headBytes = Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>
    /// Adds a line to the tail, which drops its oldest lines to stay within the budget of the message.
    /// </summary>
    /// <param name="text">The message of the line</param>
    private void AppendTail(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var capacity = _omitted > 0 ? ModelLimits.MaxTextBytes - MarkerReserveBytes : ModelLimits.MaxTextBytes;

        if (_headBytes + _tailBytes + bytes + SeparatorBytes > capacity)
        {
            capacity = ModelLimits.MaxTextBytes - MarkerReserveBytes;

            while (_tail.Count > 0 && _headBytes + _tailBytes + bytes + SeparatorBytes > capacity)
            {
                _tailBytes -= _tail.Dequeue().Bytes + SeparatorBytes;
                _omitted++;
            }

            if (_headBytes + _tailBytes + bytes + SeparatorBytes > capacity)
            {
                text = Utf8Text.Cut(text, capacity - _headBytes - SeparatorBytes, out _);
                bytes = Encoding.UTF8.GetByteCount(text);
                _truncated = true;
            }
        }

        _tail.Enqueue((text, bytes));
        _tailBytes += bytes + SeparatorBytes;
    }

    /// <summary>
    /// Emits the open report, if any, and resets the state.
    /// </summary>
    /// <param name="ready">Receives the record</param>
    private void Flush(List<DataRecord> ready)
    {
        if (_first is null)
        {
            return;
        }

        ready.Add(_lines == 1 ? _first : Build());

        _first = null;
        _lines = 0;
        _priority = null;
        _truncated = false;
        _head.Clear();
        _tail.Clear();
        _headBytes = 0;
        _headClosed = false;
        _tailBytes = 0;
        _omitted = 0;
    }

    /// <summary>
    /// Builds the record of the open report.
    /// </summary>
    /// <returns>The record</returns>
    private DataRecord Build()
    {
        var first = _first!;
        var firstLine = (LogLine)first.Data!;
        var message = new StringBuilder();

        message.AppendJoin('\n', _head);

        if (_omitted > 0)
        {
            message.Append('\n').Append('[').Append(_omitted.ToString(CultureInfo.InvariantCulture)).Append(" lines omitted]");
        }

        foreach (var entry in _tail)
        {
            message.Append('\n').Append(entry.Text);
        }

        return new DataRecord
               {
                   Origin = first.Origin,
                   Source = first.Source,
                   Seq = first.Seq,
                   CapturedAt = first.CapturedAt,
                   Data = new LogLine
                          {
                              Log = firstLine.Log,
                              Host = firstLine.Host,
                              Program = KernelProgram,
                              Pid = 0,
                              Priority = _priority,
                              Message = message.ToString(),
                              Truncated = _truncated || _omitted > 0
                          }
               };
    }

    #endregion // Methods
}