using Vandox.Core.LogParsing;

namespace Vandox.Backend.Tests;

/// <summary>
/// A parser double that claims every file starting with <c>LOG</c> and cancels the run while it parses.
/// </summary>
internal sealed class CancellingParser : ILogParser
{
    #region Fields

    private readonly CancellationTokenSource _source;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CancellingParser"/> class.
    /// </summary>
    /// <param name="source">Cancelled when the parser runs</param>
    internal CancellingParser(CancellationTokenSource source)
    {
        _source = source;
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc />
    public string Type => "cancelling";

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        return head.StartsWith("LOG"u8) ? Confidence.MatchContent : Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        await _source.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await input.CopyToAsync(Stream.Null, cancellationToken);
    }

    #endregion // ILogParser
}