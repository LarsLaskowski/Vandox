namespace Vandox.Core.Wire;

/// <summary>
/// Reads lines from a stream with a bound on the line length and on the total number of bytes, so a hostile stream
/// cannot make the reader allocate without limit.
/// </summary>
internal sealed class LineReader
{
    #region Constants

    private const int InitialBufferBytes = 64 * 1024;

    #endregion // Constants

    #region Fields

    private readonly Stream _stream;
    private readonly int _maxLine;
    private readonly long _maxTotal;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private long _total;
    private bool _eof;
    private WireErrorKind? _pendingLimit;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LineReader"/> class.
    /// </summary>
    /// <param name="stream">The stream to read, already decompressed</param>
    /// <param name="maxLine">The longest line in bytes, without the line feed</param>
    /// <param name="maxTotal">The most bytes the stream may deliver</param>
    internal LineReader(Stream stream, int maxLine, long maxTotal)
    {
        _stream = stream;
        _maxLine = maxLine;
        _maxTotal = maxTotal;
        _buffer = new byte[Math.Min(InitialBufferBytes, maxLine + 1)];
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the next line without its line feed and without a carriage return before it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The line, or <c>null</c> after the last line</returns>
    /// <exception cref="WireException">The line or the stream exceeds a limit, or the stream fails; the line number is 0 and set by the caller</exception>
    internal async ValueTask<ReadOnlyMemory<byte>?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var index = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);

            if (index >= 0)
            {
                return TakeLine(index, index + 1);
            }

            if (_end - _start > _maxLine)
            {
                throw new WireException(WireErrorKind.LimitExceeded, 0, $"line longer than {_maxLine} bytes");
            }

            if (_pendingLimit is not null)
            {
                throw new WireException(WireErrorKind.LimitExceeded, 0, $"more than {_maxTotal} decompressed bytes");
            }

            if (_eof)
            {
                if (_end > _start)
                {
                    return TakeLine(_end, _end);
                }

                return null;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cuts the line that ends at <paramref name="lineEnd"/> out of the buffer.
    /// </summary>
    /// <param name="lineEnd">Index of the line feed, or the end of the data</param>
    /// <param name="next">Index of the first byte of the following line</param>
    /// <returns>The line</returns>
    private ReadOnlyMemory<byte> TakeLine(int lineEnd, int next)
    {
        if (lineEnd - _start > _maxLine)
        {
            throw new WireException(WireErrorKind.LimitExceeded, 0, $"line longer than {_maxLine} bytes");
        }

        var length = lineEnd - _start;
        var line = new ReadOnlyMemory<byte>(_buffer, _start, length);

        if (length > 0 && _buffer[lineEnd - 1] == (byte)'\r')
        {
            line = line[..^1];
        }

        _start = next;

        return line;
    }

    /// <summary>
    /// Reads more bytes into the buffer, moving the pending data to its start and growing it as far as the line limit needs.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when bytes were read or the stream ended</returns>
    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, (int)Math.Min((long)_buffer.Length * 2, (long)_maxLine + 1));
        }

        int read;

        try
        {
            read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new WireException(WireErrorKind.Malformed, 0, "stream is not valid gzip data");
        }

        if (read == 0)
        {
            _eof = true;

            return;
        }

        _total += read;

        if (_total > _maxTotal)
        {
            read -= (int)(_total - _maxTotal);
            _total = _maxTotal;
            _pendingLimit = WireErrorKind.LimitExceeded;
        }

        _end += read;
    }

    #endregion // Methods
}