#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the output of <c>journalctl -o export</c>.
/// </summary>
public sealed class JournalExportParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of this parser.
    /// </summary>
    public const string ParserType = "journal";

    #endregion // Constants

    #region ILogParser

    /// <inheritdoc />
    public string Type => ParserType;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogParser
}