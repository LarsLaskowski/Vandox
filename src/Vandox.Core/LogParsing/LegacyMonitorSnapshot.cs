using System.Text;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// One snapshot of the legacy monitoring log.
/// </summary>
internal sealed class LegacyMonitorSnapshot
{
    #region Constants

    private const string TruncatedSnapshot = "truncated snapshot";
    private const string MalformedSnapshot = "malformed snapshot";
    private const string RowNotRead = "process row not read";
    private const string DuplicateProcess = "duplicate process id";
    private const string TooManyProcesses = "too many processes in a snapshot";
    private const string TooManySockets = "too many sockets in a snapshot";
    private const string TooManyLines = "too many lines in a section";
    private const string MeminfoNotRead = "meminfo line not read";
    private const string PressureNotRead = "pressure line not read";
    private const string VmstatNotRead = "vmstat line not read";
    private const string RepeatedSection = "repeated section";
    private const string UnknownSection = "unknown section";
    private const int MaxRows = 4096;
    private const int MaxSockets = 4096;
    private const int MaxMeminfoLines = 128;
    private const int MaxPressureLines = 16;
    private const int MaxVmstatLines = 64;
    private const int MaxLsofLineBytes = 1024;
    private const int MaxSectionNameLength = 32;
    private const string MarkerStart = "----- ";
    private const string MarkerEnd = " -----";

    #endregion // Constants

    #region Fields

    private static readonly string[] _counterNames = ["pswpin", "pswpout", "pgmajfault", "oom_kill"];

    private readonly long _headerLine;
    private readonly DateTimeOffset _capturedAt;
    private readonly Dictionary<string, LegacyCounter> _counters;
    private readonly List<(long Line, string Reason)> _held = [];
    private readonly HashSet<string> _meminfoFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pressureKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _vmstatCounters = new(StringComparer.Ordinal);
    private LegacySnapshotPhase _phase = LegacySnapshotPhase.Load;
    private LegacySection _section = LegacySection.None;
    private LegacyProcessTable? _table = new();
    private LegacyConnectionTally? _tally;
    private double[] _load = [];
    private double[] _cpu = [];
    private ulong[] _memory = [];
    private ulong[] _swap = [];
    private uint _tasks;
    private int _rowLines;
    private bool _rowsIntact = true;
    private bool _newFormat;
    private int _seenSections;
    private int _sectionLines;
    private bool _lsofIntact = true;
    private string? _previousResource;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyMonitorSnapshot"/> class.
    /// </summary>
    /// <param name="headerLine">The line number of the header</param>
    /// <param name="capturedAt">The instant of the header</param>
    /// <param name="counters">The vmstat counter history of the parse</param>
    internal LegacyMonitorSnapshot(long headerLine, DateTimeOffset capturedAt, Dictionary<string, LegacyCounter> counters)
    {
        _headerLine = headerLine;
        _capturedAt = capturedAt;
        _counters = counters;
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
        if (_phase == LegacySnapshotPhase.Skipped || line.AsSpan().Trim(' ').IsEmpty)
        {
            return;
        }

        if (IsMarker(line, out var name))
        {
            AddMarker(lineNumber, name, output, ready);

            return;
        }

        switch (_phase)
        {
            case LegacySnapshotPhase.Rows:
                {
                    AddRow(lineNumber, line, cut, output);
                }
                break;

            case LegacySnapshotPhase.Sections:
                {
                    AddSectionLine(lineNumber, line, cut, output, ready);
                }
                break;

            default:
                {
                    AddSummaryLine(line, output);
                }
                break;
        }
    }

    /// <summary>
    /// Finishes the snapshot: its single skip, or the acceptance and the open lists.
    /// </summary>
    /// <param name="endOfInput">Whether the end of the input closed the snapshot</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(bool endOfInput, IRecordEmitter output, List<DataRecord> ready)
    {
        if (_phase == LegacySnapshotPhase.Skipped)
        {
            return;
        }

        if (_phase == LegacySnapshotPhase.Rows)
        {
            Accept(endOfInput, output, ready);
        }
        else if (_phase == LegacySnapshotPhase.Sections)
        {
            EndSection(endOfInput, ready);
        }
        else
        {
            output.Skip(_headerLine, TruncatedSnapshot);
            _phase = LegacySnapshotPhase.Skipped;
        }
    }

    /// <summary>
    /// Tells whether a line is a section marker <c>----- name -----</c>.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="name">The name of the section</param>
    /// <returns><c>true</c> for a marker with a name of 1 to 32 lower-case ASCII letters</returns>
    private static bool IsMarker(string line, out string name)
    {
        name = string.Empty;

        if (line.Length > MarkerStart.Length + MarkerEnd.Length && line.StartsWith(MarkerStart, StringComparison.Ordinal) && line.EndsWith(MarkerEnd, StringComparison.Ordinal))
        {
            var inner = line.Substring(MarkerStart.Length, line.Length - MarkerStart.Length - MarkerEnd.Length);

            if (inner.Length <= MaxSectionNameLength && inner.All(char.IsAsciiLetterLower))
            {
                name = inner;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the section a marker names.
    /// </summary>
    /// <param name="name">The name of the section</param>
    /// <returns>The section; <see cref="LegacySection.Ignored"/> for an unknown name</returns>
    private static LegacySection SectionOf(string name)
    {
        return name switch
               {
                   "meminfo" => LegacySection.Meminfo,
                   "pressure" => LegacySection.Pressure,
                   "vmstat" => LegacySection.Vmstat,
                   "lsof" => LegacySection.Lsof,
                   _ => LegacySection.Ignored
               };
    }

    /// <summary>
    /// Builds the key that tells a pressure entry from the others of the snapshot.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <returns>The resource and kind</returns>
    private static string PressureKey(LegacyPressure entry)
    {
        return $"{entry.Resource}/{entry.Kind}";
    }

    /// <summary>
    /// Counts a line of a section against its cap.
    /// </summary>
    /// <param name="cap">The most lines of the section</param>
    /// <param name="beyond"><c>true</c> when the line is the first one beyond the cap</param>
    /// <returns><c>true</c> when the line is within the cap and is to be read</returns>
    private bool TakeLine(int cap, out bool beyond)
    {
        beyond = _sectionLines == cap;

        var within = _sectionLines < cap;

        if (_sectionLines <= cap)
        {
            _sectionLines++;
        }

        return within;
    }

    /// <summary>
    /// Fails the snapshot as malformed and drops what it held.
    /// </summary>
    /// <param name="output">Receives the skip</param>
    private void Fail(IRecordEmitter output)
    {
        output.Skip(_headerLine, MalformedSnapshot);
        _phase = LegacySnapshotPhase.Skipped;
        _table = null;
        _held.Clear();
    }

    /// <summary>
    /// Reads the line of the top part that is expected next.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="output">Receives the skip of a malformed snapshot</param>
    private void AddSummaryLine(string line, IRecordEmitter output)
    {
        var read = _phase switch
                   {
                       LegacySnapshotPhase.Load => LegacyTopLine.TryParseLoad(line, out _load),
                       LegacySnapshotPhase.Tasks => LegacyTopLine.TryParseTasks(line, out _tasks),
                       LegacySnapshotPhase.Cpu => LegacyTopLine.TryParseCpu(line, out _cpu),
                       LegacySnapshotPhase.Memory => LegacyTopLine.TryParseMemory(line, out _memory),
                       LegacySnapshotPhase.Swap => LegacyTopLine.TryParseSwap(line, out _swap),
                       _ => LegacyTopLine.IsTableHeader(line)
                   };

        if (read)
        {
            _phase++;
        }
        else
        {
            Fail(output);
        }
    }

    /// <summary>
    /// Reads a row of the process table.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="cut">Whether the line was cut</param>
    /// <param name="output">Receives the skip of a malformed snapshot</param>
    private void AddRow(long lineNumber, string line, bool cut, IRecordEmitter output)
    {
        if (_rowLines > MaxRows)
        {
            return;
        }

        if (_rowLines == MaxRows)
        {
            _rowLines++;
            _rowsIntact = false;
            _held.Add((lineNumber, TooManyProcesses));

            return;
        }

        _rowLines++;

        if (LegacyTopLine.IsSummaryStart(line) || LegacyTopLine.IsTableHeader(line))
        {
            Fail(output);

            return;
        }

        var row = LegacyTopLine.TryParseRow(line);

        if (row is not null && _table!.TryAdd(row, cut))
        {
            return;
        }

        _rowsIntact = false;
        _held.Add((lineNumber, row is null ? RowNotRead : DuplicateProcess));
    }

    /// <summary>
    /// Handles a section marker: accepts the top part, ends the section before and begins the next.
    /// </summary>
    /// <param name="lineNumber">The line number of the marker</param>
    /// <param name="name">The name of the section</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    private void AddMarker(long lineNumber, string name, IRecordEmitter output, List<DataRecord> ready)
    {
        if (_phase < LegacySnapshotPhase.Rows)
        {
            Fail(output);

            return;
        }

        if (_phase == LegacySnapshotPhase.Rows)
        {
            _newFormat = true;
            Accept(false, output, ready);
        }

        EndSection(false, ready);
        BeginSection(lineNumber, name, output);
    }

    /// <summary>
    /// Accepts the top part: hands out the summary metrics and the process snapshot and reports the held-back skips.
    /// </summary>
    /// <param name="endOfInput">Whether the end of the input ended the table</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    private void Accept(bool endOfInput, IRecordEmitter output, List<DataRecord> ready)
    {
        var table = _table!;
        var complete = _rowsIntact && table.Count == _tasks;

        if (endOfInput)
        {
            complete = false;
        }

        LegacyMetrics.AddSummary(ready, _capturedAt, _load, _cpu, _memory, _swap);

        var snapshot = table.Build(_tasks, _newFormat, complete);

        if (snapshot is not null)
        {
            ready.Add(LegacyMetrics.Record(_capturedAt, snapshot));
        }

        foreach (var (line, reason) in _held)
        {
            output.Skip(line, reason);
        }

        _held.Clear();
        _table = null;
        _phase = LegacySnapshotPhase.Sections;
    }

    /// <summary>
    /// Begins a section.
    /// </summary>
    /// <param name="lineNumber">The line number of the marker</param>
    /// <param name="name">The name of the section</param>
    /// <param name="output">Receives the skip of an unknown or repeated section</param>
    private void BeginSection(long lineNumber, string name, IRecordEmitter output)
    {
        var section = SectionOf(name);

        _sectionLines = 0;
        _previousResource = null;

        if (section == LegacySection.Ignored)
        {
            output.Skip(lineNumber, UnknownSection);
        }
        else if ((_seenSections & (1 << (int)section)) != 0)
        {
            output.Skip(lineNumber, RepeatedSection);
            section = LegacySection.Ignored;
        }

        _seenSections |= 1 << (int)section;
        _section = section;
        _tally = section == LegacySection.Lsof ? new LegacyConnectionTally() : null;
    }

    /// <summary>
    /// Ends the open section: an lsof section hands out its connection snapshot.
    /// </summary>
    /// <param name="endOfInput">Whether the end of the input ends the section</param>
    /// <param name="ready">Receives the records to emit</param>
    private void EndSection(bool endOfInput, List<DataRecord> ready)
    {
        if (_section == LegacySection.Lsof && _sectionLines > 0)
        {
            var complete = _lsofIntact;

            if (endOfInput)
            {
                complete = false;
            }

            ready.Add(LegacyMetrics.Record(_capturedAt, _tally!.Build(complete)));
        }

        _tally = null;
        _section = LegacySection.None;
    }

    /// <summary>
    /// Reads a line of the open section.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="cut">Whether the line was cut</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    private void AddSectionLine(long lineNumber, string line, bool cut, IRecordEmitter output, List<DataRecord> ready)
    {
        var cap = _section switch
                  {
                      LegacySection.Meminfo => MaxMeminfoLines,
                      LegacySection.Pressure => MaxPressureLines,
                      LegacySection.Vmstat => MaxVmstatLines,
                      LegacySection.Lsof => MaxSockets,
                      _ => 0
                  };

        if (cap == 0)
        {
            return;
        }

        if (TakeLine(cap, out var beyond))
        {
            ReadSectionLine(lineNumber, line, cut, output, ready);
        }
        else if (beyond)
        {
            _lsofIntact &= _section != LegacySection.Lsof;
            output.Skip(lineNumber, _section == LegacySection.Lsof ? TooManySockets : TooManyLines);
        }
    }

    /// <summary>
    /// Reads a line of the open section that is within its cap.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="cut">Whether the line was cut</param>
    /// <param name="output">Receives the skips</param>
    /// <param name="ready">Receives the records to emit</param>
    private void ReadSectionLine(long lineNumber, string line, bool cut, IRecordEmitter output, List<DataRecord> ready)
    {
        switch (_section)
        {
            case LegacySection.Meminfo:
                {
                    ReadMeminfo(lineNumber, line, output, ready);
                }
                break;

            case LegacySection.Pressure:
                {
                    ReadPressure(lineNumber, line, output, ready);
                }
                break;

            case LegacySection.Vmstat:
                {
                    ReadVmstat(lineNumber, line, output, ready);
                }
                break;

            default:
                {
                    ReadSocket(lineNumber, line, cut, output);
                }
                break;
        }
    }

    /// <summary>
    /// Reads a meminfo line.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="output">Receives the skip</param>
    /// <param name="ready">Receives the records to emit</param>
    private void ReadMeminfo(long lineNumber, string line, IRecordEmitter output, List<DataRecord> ready)
    {
        if (LegacyProcLine.TryParseMeminfo(line, out var field, out var value, out var kilobytes) && _meminfoFields.Add(field))
        {
            ready.Add(LegacyMetrics.Metric(_capturedAt, kilobytes ? "meminfo_bytes" : "meminfo", value, kilobytes ? "bytes" : string.Empty, "field", field));
        }
        else
        {
            output.Skip(lineNumber, MeminfoNotRead);
        }
    }

    /// <summary>
    /// Reads a pressure line.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="output">Receives the skip</param>
    /// <param name="ready">Receives the records to emit</param>
    private void ReadPressure(long lineNumber, string line, IRecordEmitter output, List<DataRecord> ready)
    {
        if (LegacyProcLine.IsNotAvailable(line))
        {
            _previousResource = null;

            return;
        }

        var entries = LegacyProcLine.TryParsePressure(line, _previousResource);

        _previousResource = null;

        if (entries is null || _pressureKeys.Overlaps(entries.Select(PressureKey)))
        {
            output.Skip(lineNumber, PressureNotRead);

            return;
        }

        foreach (var entry in entries)
        {
            _pressureKeys.Add(PressureKey(entry));
            LegacyMetrics.AddPressure(ready, _capturedAt, entry);
        }

        _previousResource = entries.Count == 1 && entries[0].Kind == "some" ? entries[0].Resource : null;
    }

    /// <summary>
    /// Reads a vmstat line.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="output">Receives the skip</param>
    /// <param name="ready">Receives the records to emit</param>
    private void ReadVmstat(long lineNumber, string line, IRecordEmitter output, List<DataRecord> ready)
    {
        if (LegacyProcLine.TryParseVmstat(line, out var counter, out var value))
        {
            if (Array.IndexOf(_counterNames, counter) >= 0)
            {
                if (_vmstatCounters.Add(counter))
                {
                    AddCounter(counter, value, ready);
                }
                else
                {
                    output.Skip(lineNumber, VmstatNotRead);
                }
            }
        }
        else
        {
            output.Skip(lineNumber, VmstatNotRead);
        }
    }

    /// <summary>
    /// Hands out a counter and, when the previous snapshot of the parse had it at an earlier instant and not above, its rate.
    /// </summary>
    /// <param name="counter">The counter name</param>
    /// <param name="value">The value</param>
    /// <param name="ready">Receives the records to emit</param>
    private void AddCounter(string counter, ulong value, List<DataRecord> ready)
    {
        ready.Add(LegacyMetrics.Metric(_capturedAt, "vmstat_total", value, string.Empty, "counter", counter));

        if (_counters.TryGetValue(counter, out var previous) && previous.At < _capturedAt && previous.Value <= value)
        {
            ready.Add(LegacyMetrics.Metric(_capturedAt, "vmstat_per_second", (value - previous.Value) / (_capturedAt - previous.At).TotalSeconds, "per_second", "counter", counter));
        }

        _counters[counter] = new LegacyCounter(_capturedAt, value);
    }

    /// <summary>
    /// Reads a line of the lsof section.
    /// </summary>
    /// <param name="lineNumber">The line number</param>
    /// <param name="line">The line</param>
    /// <param name="cut">Whether the line was cut</param>
    /// <param name="output">Receives the skip</param>
    private void ReadSocket(long lineNumber, string line, bool cut, IRecordEmitter output)
    {
        if (LegacyLsofLine.IsHeader(line))
        {
            return;
        }

        var reason = LegacyLsofLine.NotRead;
        var parsed = cut || Encoding.UTF8.GetByteCount(line) > MaxLsofLineBytes ? null : LegacyLsofLine.TryParse(line, out reason);

        if (parsed is not null)
        {
            _tally!.Add(parsed);

            return;
        }

        _lsofIntact = false;
        output.Skip(lineNumber, reason ?? LegacyLsofLine.NotRead);
    }

    #endregion // Methods
}