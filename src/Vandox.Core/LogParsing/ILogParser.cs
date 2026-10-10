namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads one log format. Parsing must be deterministic: the same content and file give the same records in the same
/// order, because an interrupted import is resumed by count. It must bound its memory independently of the input size and
/// honor the cancellation token.
/// </summary>
public interface ILogParser
{
    #region Constants

    /// <summary>
    /// The most content <see cref="Detect"/> is given: the first bytes of the decompressed file.
    /// </summary>
    public const int SniffBytes = 4096;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the source type, unique in a registry and accepted by <see cref="ParserTypes.IsValid"/>, e.g. <c>syslog</c>.
    /// </summary>
    string Type { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Rates a file from its name and head.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="head">The first up to <see cref="SniffBytes"/> bytes of the decompressed content</param>
    /// <returns>How well the parser matches</returns>
    Confidence Detect(LogFile file, ReadOnlySpan<byte> head);

    /// <summary>
    /// Reads the whole decompressed content of a file and passes every record to the emitter in a deterministic order. Records have origin
    /// import and UTC capture times.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="input">The content</param>
    /// <param name="output">Receives the records</param>
    /// <param name="cancellationToken">Cancels the parse</param>
    /// <returns>A task that completes when the content is read</returns>
    Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);

    #endregion // Methods
}