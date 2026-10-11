using Vandox.Core.LogParsing;

namespace Vandox.Backend.Tests;

/// <summary>
/// A parser double that claims every file starting with <c>LOG</c>, reads a few bytes of the input, cancels the run and returns without an exception, so content is left unread.
/// </summary>
internal sealed class CancelAfterReadParser : ILogParser
{
    #region Fields

    private readonly CancellationTokenSource _source;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CancelAfterReadParser"/> class.
    /// </summary>
    /// <param name="source">Cancelled after the parser read</param>
    internal CancelAfterReadParser(CancellationTokenSource source)
    {
        _source = source;
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc />
    public string Type => "cancel-after-read";

    /// <inheritdoc />
    public bool LineFramed => false;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        return head.StartsWith("LOG"u8) ? Confidence.MatchContent : Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];

        _ = await input.ReadAsync(buffer, cancellationToken);
        await _source.CancelAsync();
    }

    #endregion // ILogParser
}