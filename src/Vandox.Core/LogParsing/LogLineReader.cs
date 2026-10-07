using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads lines of bounded length from a stream. A longer line is cut at a UTF-8 boundary and the rest of it is discarded,
/// so a parser's memory does not depend on the input.
/// </summary>
public sealed class LogLineReader
{
    #region Constants

    /// <summary>
    /// The longest line the reader returns; longer lines are cut.
    /// </summary>
    public const int MaxLineBytes = ModelLimits.MaxTextBytes;

    private const int KeepBytes = MaxLineBytes + 2;
    private const int ReadBufferBytes = 16 * 1024;
    private const int Utf8MaxSequence = 4;

    #endregion // Constants

    #region Fields

    private readonly Stream _input;
    private readonly byte[] _read = new byte[ReadBufferBytes];
    private readonly byte[] _line = new byte[KeepBytes];
    private int _position;
    private int _length;
    private int _lineLength;
    private bool _truncated;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LogLineReader"/> class.
    /// </summary>
    /// <param name="input">The stream to read</param>
    public LogLineReader(Stream input)
    {
        _input = input;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the line <see cref="ReadAsync"/> returned last, without its line ending. It is valid until the next read.
    /// </summary>
    public ReadOnlyMemory<byte> Line => new ReadOnlyMemory<byte>(_line, 0, _lineLength);

    /// <summary>
    /// Gets a value indicating whether the last line was cut to <see cref="MaxLineBytes"/>.
    /// </summary>
    public bool Truncated => _truncated;

    /// <summary>
    /// Gets the 1-based number of the line <see cref="ReadAsync"/> returned last; 0 before the first.
    /// </summary>
    public long LineNumber { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the next line without <c>\n</c> or <c>\r\n</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>true</c> when a line was read, <c>false</c> after the last line</returns>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var kept = 0;
        long total = 0;

        while (true)
        {
            if (_position == _length)
            {
                _length = await _input.ReadAsync(_read.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;

                if (_length == 0)
                {
                    break;
                }
            }

            var span = _read.AsSpan(_position, _length - _position);
            var index = span.IndexOf((byte)'\n');
            var take = index >= 0 ? index + 1 : span.Length;
            var room = KeepBytes - kept;

            if (room > 0)
            {
                var copy = Math.Min(room, take);

                span[..copy].CopyTo(_line.AsSpan(kept));
                kept += copy;
            }

            total += take;
            _position += take;

            if (index >= 0)
            {
                break;
            }
        }

        if (total == 0)
        {
            return false;
        }

        LineNumber++;
        Finish(kept, total);

        return true;
    }

    /// <summary>
    /// Finishes the line in the buffer: removes the line ending and cuts an over-long line.
    /// </summary>
    /// <param name="kept">The number of bytes kept in the buffer</param>
    /// <param name="total">The number of bytes of the line including its line ending</param>
    private void Finish(int kept, long total)
    {
        var length = kept;

        if (total <= KeepBytes && length > 0 && _line[length - 1] == (byte)'\n')
        {
            length--;

            if (length > 0 && _line[length - 1] == (byte)'\r')
            {
                length--;
            }
        }

        _truncated = length > MaxLineBytes;
        _lineLength = _truncated ? CutAt(length) : length;
    }

    /// <summary>
    /// Returns the largest length of at most <see cref="MaxLineBytes"/> at which the line can be cut without splitting a UTF-8
    /// sequence.
    /// </summary>
    /// <param name="length">The length of the line</param>
    /// <returns>The length to keep</returns>
    private int CutAt(int length)
    {
        var cut = Math.Min(MaxLineBytes, length - 1);

        for (var back = 0; back < Utf8MaxSequence - 1 && cut > 0 && (_line[cut] & 0xC0) == 0x80; back++)
        {
            cut--;
        }

        return cut;
    }

    #endregion // Methods
}