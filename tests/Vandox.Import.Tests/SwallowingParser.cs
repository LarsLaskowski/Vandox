using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Import.Tests;

/// <summary>
/// A parser double that makes a log line record of every line of a file whose first line starts with <c>LOG</c> and catches the
/// failure of <see cref="IRecordEmitter.RecordAsync"/>, so it keeps reading past the line that caused it.
/// </summary>
internal sealed class SwallowingParser : ILogParser
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SwallowingParser"/> class.
    /// </summary>
    /// <param name="type">The source type</param>
    internal SwallowingParser(string type)
    {
        Type = type;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of failures of <see cref="IRecordEmitter.RecordAsync"/> the parser caught.
    /// </summary>
    internal int Caught { get; private set; }

    /// <summary>
    /// Gets the number of lines the parser read.
    /// </summary>
    internal long LinesRead { get; private set; }

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
        var reader = new LogLineReader(input);

        while (await reader.ReadAsync(cancellationToken))
        {
            LinesRead = reader.LineNumber;

            try
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
            }
            catch (StoreException)
            {
                Caught++;
            }
        }
    }

    #endregion // ILogParser
}