using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Import.Tests;

/// <summary>
/// A parser double that makes a log line record of every line of a file whose first line starts with <c>LOG</c>, can stop reading
/// after a number of lines, and counts how often it started and finished.
/// </summary>
internal sealed class ProbeParser : ILogParser
{
    #region Fields

    private readonly long _stopAfter;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeParser"/> class.
    /// </summary>
    /// <param name="type">The source type</param>
    /// <param name="stopAfter">The number of lines after which the parser returns without reading the rest; 0 reads everything</param>
    internal ProbeParser(string type, long stopAfter = 0)
    {
        Type = type;
        _stopAfter = stopAfter;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of times the parser started.
    /// </summary>
    internal int Started { get; private set; }

    /// <summary>
    /// Gets the number of times the parser returned without an error.
    /// </summary>
    internal int Completed { get; private set; }

    #endregion // Properties

    #region ILogParser

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public bool LineFramed => true;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        return head.StartsWith("LOG"u8) ? Confidence.MatchContent : Confidence.NoMatch;
    }

    /// <inheritdoc />
    /// <returns>A task that completes when the work is done</returns>
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        Started++;

        var reader = new LogLineReader(input);

        while (await reader.ReadAsync(cancellationToken))
        {
            await output.RecordAsync(new DataRecord
                                     {
                                         Origin = RecordOrigin.Import,
                                         Source = "import",
                                         CapturedAt = LineParser.Base.AddSeconds(reader.LineNumber),
                                         Data = new LogLine
                                                {
                                                    Log = "test",
                                                    Message = Encoding.UTF8.GetString(reader.Line.Span)
                                                }
                                     },
                                     cancellationToken);

            if (_stopAfter > 0 && reader.LineNumber >= _stopAfter)
            {
                break;
            }
        }

        Completed++;
    }

    #endregion // ILogParser
}