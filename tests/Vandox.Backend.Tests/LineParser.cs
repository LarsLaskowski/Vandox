using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// A parser double: every line of a file whose first line starts with <c>LOG</c> becomes a log line record one second after
/// <see cref="Base"/> per line number. A line starting with <c>bad</c> is skipped, a line starting with <c>refuse</c> becomes an
/// invalid record, and a line starting with <c>boom</c> makes the parser fail.
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

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LineParser"/> class.
    /// </summary>
    /// <param name="type">The source type</param>
    internal LineParser(string type)
    {
        Type = type;
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

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type { get; }

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
                await output.RecordAsync(Record(reader.LineNumber, text, text.StartsWith("refuse", StringComparison.Ordinal)), cancellationToken);
            }
        }
    }

    #endregion // ILogParser
}