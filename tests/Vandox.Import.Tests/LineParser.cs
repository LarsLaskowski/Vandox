using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Import.Tests;

/// <summary>
/// A parser double: every line of a file whose first line starts with <c>LOG</c> becomes a log line record one second after
/// <see cref="Base"/> per line number. A line starting with <c>bad</c> is skipped, a line starting with <c>refuse</c> becomes an
/// invalid record, and a line starting with <c>boom</c> makes the parser fail. A line starting with <c>raw</c> is reported with a
/// warning and then becomes a record like any other line, and a line starting with <c>huge</c> becomes a process snapshot whose JSON
/// is larger than the payload limit. The first lines it is told to skip are skipped.
/// </summary>
internal sealed class LineParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The capture time of line 1.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Fields

    private readonly List<LogFile> _seen = [];
    private readonly long _skipFirst;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LineParser"/> class.
    /// </summary>
    /// <param name="type">The source type</param>
    /// <param name="lineFramed">The value of <see cref="LineFramed"/></param>
    /// <param name="skipFirst">The number of leading lines the parser skips instead of making records of them</param>
    internal LineParser(string type, bool lineFramed = true, long skipFirst = 0)
    {
        Type = type;
        LineFramed = lineFramed;
        _skipFirst = skipFirst;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the files the parser was asked to parse.
    /// </summary>
    internal IReadOnlyList<LogFile> Seen => _seen;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the record of a line.
    /// </summary>
    /// <param name="line">The line number</param>
    /// <param name="text">The text of the line</param>
    /// <param name="invalid">Whether to make the record violate the model</param>
    /// <returns>The record</returns>
    private static DataRecord Record(long line, string text, bool invalid)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = "import",
                   CapturedAt = Base.AddSeconds(line),
                   Data = new LogLine
                          {
                              Log = invalid ? string.Empty : "test",
                              Message = text
                          }
               };
    }

    /// <summary>
    /// Creates the record of a line that holds a process snapshot above the payload limit.
    /// </summary>
    /// <param name="line">The line number</param>
    /// <returns>The record</returns>
    private static DataRecord HugeRecord(long line)
    {
        var processes = new List<ProcessSample>();

        for (var pid = 1; pid <= 200; pid++)
        {
            processes.Add(new ProcessSample
                          {
                              Pid = pid,
                              Command = "c",
                              Cmdline = new string('<', 16384)
                          });
        }

        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = "import",
                   CapturedAt = Base.AddSeconds(line),
                   Data = new ProcessSnapshot
                          {
                              Complete = true,
                              Processes = processes
                          }
               };
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public bool LineFramed { get; }

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        return head.StartsWith("LOG"u8) ? Confidence.MatchContent : Confidence.NoMatch;
    }

    /// <inheritdoc />
    /// <returns>A task that completes when the work is done</returns>
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        _seen.Add(file);

        var reader = new LogLineReader(input);

        while (await reader.ReadAsync(cancellationToken))
        {
            var text = Encoding.UTF8.GetString(reader.Line.Span);

            if (reader.LineNumber <= _skipFirst)
            {
                output.Skip(reader.LineNumber, "leading line");

                continue;
            }

            if (text.StartsWith("boom", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the parser gave up");
            }

            if (text.StartsWith("bad", StringComparison.Ordinal))
            {
                output.Skip(reader.LineNumber, "bad line");
            }
            else
            {
                if (text.StartsWith("raw", StringComparison.Ordinal))
                {
                    output.Warn(reader.LineNumber, "raw line");
                }

                var record = text.StartsWith("huge", StringComparison.Ordinal) ? HugeRecord(reader.LineNumber) : Record(reader.LineNumber, text, text.StartsWith("refuse", StringComparison.Ordinal));

                await output.RecordAsync(record, cancellationToken);
            }
        }
    }

    #endregion // ILogParser
}