using System.Buffers;
using System.IO.Compression;

namespace Vandox.Core.Wire;

/// <summary>
/// Reads the decompressed bytes of a gzip stream made of one or more members and checks the framing of each member
/// (header, CRC-32 and size trailer) by the rules of the wire format.
/// </summary>
internal sealed class GzipMemberReader : Stream
{
    #region Constants

    /// <summary>
    /// The size of the buffer the reader takes from the input.
    /// </summary>
    private const int InputBufferSize = 4096;

    /// <summary>
    /// The longest file name or comment including its terminating zero byte.
    /// </summary>
    private const int MaxHeaderStringBytes = 512;

    /// <summary>
    /// The length of the fixed part of a header.
    /// </summary>
    private const int FixedHeaderLength = 10;

    /// <summary>
    /// The length of the trailer: CRC-32 and size.
    /// </summary>
    private const int TrailerLength = 8;

    /// <summary>
    /// The flag of the header checksum.
    /// </summary>
    private const int FlagHeaderCrc = 0x02;

    /// <summary>
    /// The flag of the extra field.
    /// </summary>
    private const int FlagExtra = 0x04;

    /// <summary>
    /// The flag of the file name.
    /// </summary>
    private const int FlagName = 0x08;

    /// <summary>
    /// The flag of the comment.
    /// </summary>
    private const int FlagComment = 0x10;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The compressed stream.
    /// </summary>
    private readonly Stream _input;

    /// <summary>
    /// The bytes taken from the input and not yet consumed.
    /// </summary>
    private readonly byte[] _buffer = new byte[InputBufferSize];

    /// <summary>
    /// A one-byte scratch array for the checksum of header bytes.
    /// </summary>
    private readonly byte[] _single = new byte[1];

    /// <summary>
    /// The position of the next unconsumed byte in <see cref="_buffer"/>.
    /// </summary>
    private int _start;

    /// <summary>
    /// The end of the valid bytes in <see cref="_buffer"/>.
    /// </summary>
    private int _end;

    /// <summary>
    /// The inflater of the current member, <c>null</c> between members.
    /// </summary>
    private DeflateStream? _inflater;

    /// <summary>
    /// The checksum of the current member's content so far.
    /// </summary>
    private uint _crc;

    /// <summary>
    /// The size of the current member's content so far, modulo 2^32.
    /// </summary>
    private uint _size;

    /// <summary>
    /// The checksum of the header bytes read so far.
    /// </summary>
    private uint _headerCrc;

    /// <summary>
    /// The framing error found after bytes were already returned by the same read, thrown by the next read.
    /// </summary>
    private InvalidDataException? _failure;

    /// <summary>
    /// Whether a member header was read.
    /// </summary>
    private bool _started;

    /// <summary>
    /// Whether the end of the last member was reached.
    /// </summary>
    private bool _done;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GzipMemberReader"/> class.
    /// </summary>
    /// <param name="input">The compressed stream</param>
    internal GzipMemberReader(Stream input)
    {
        _input = input;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Consumes one byte that is already buffered.
    /// </summary>
    /// <param name="value">The byte</param>
    /// <returns><c>true</c> when a byte was buffered, <c>false</c> when the buffer is empty</returns>
    internal bool TryTakeBuffered(out byte value)
    {
        if (_start < _end)
        {
            value = _buffer[_start++];

            return true;
        }

        value = 0;

        return false;
    }

    /// <summary>
    /// Consumes one byte of the input.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The byte, or -1 at the end of the input</returns>
    internal async ValueTask<int> TryReadByteAsync(CancellationToken cancellationToken)
    {
        if (await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return _buffer[_start++];
        }

        return -1;
    }

    /// <summary>
    /// Reads the header of the next member and starts its inflater.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns><c>false</c> when a member starts, <c>true</c> when the input ended exactly after a member</returns>
    /// <exception cref="InvalidDataException">The input is empty, ends inside a header or does not hold a valid header</exception>
    private async ValueTask<bool> StartMemberAsync(CancellationToken cancellationToken)
    {
        if (await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);

            return false;
        }

        if (_started)
        {
            return true;
        }

        throw new InvalidDataException("gzip stream is empty");
    }

    /// <summary>
    /// Reads and checks the header of a member and starts its inflater.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the header is read</returns>
    /// <exception cref="InvalidDataException">The input ends inside the header or the header is not valid</exception>
    private async ValueTask ReadHeaderAsync(CancellationToken cancellationToken)
    {
        _started = true;
        _headerCrc = 0;

        var magic1 = await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        var magic2 = await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        var method = await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);

        if (magic1 != 0x1f || magic2 != 0x8b || method != 0x08)
        {
            throw new InvalidDataException("gzip header is invalid");
        }

        var flags = await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);

        for (var skipped = 4; skipped < FixedHeaderLength; skipped++)
        {
            await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & FlagExtra) != 0)
        {
            var length = await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);

            length |= await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false) << 8;

            for (var index = 0; index < length; index++)
            {
                await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if ((flags & FlagName) != 0)
        {
            await SkipHeaderStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & FlagComment) != 0)
        {
            await SkipHeaderStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & FlagHeaderCrc) != 0)
        {
            var expected = (ushort)_headerCrc;
            var actual = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

            actual |= await ReadByteAsync(cancellationToken).ConfigureAwait(false) << 8;

            if (actual != expected)
            {
                throw new InvalidDataException("gzip header checksum does not match");
            }
        }

        _crc = 0;
        _size = 0;
        _inflater = new DeflateStream(new GzipMemberInput(this), CompressionMode.Decompress);
    }

    /// <summary>
    /// Checks the trailer of the member whose content ended.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the trailer is checked</returns>
    /// <exception cref="InvalidDataException">The trailer is missing or does not match the content</exception>
    private async ValueTask EndMemberAsync(CancellationToken cancellationToken)
    {
        uint trailerCrc = 0;
        uint trailerSize = 0;

        for (var index = 0; index < TrailerLength; index++)
        {
            var value = (uint)await ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (index < 4)
            {
                trailerCrc |= value << (8 * index);
            }
            else
            {
                trailerSize |= value << (8 * (index - 4));
            }
        }

        await _inflater!.DisposeAsync().ConfigureAwait(false);
        _inflater = null;

        if (trailerCrc != _crc || trailerSize != _size)
        {
            throw new InvalidDataException("gzip trailer does not match the content");
        }
    }

    /// <summary>
    /// Skips a zero-terminated header string of at most <see cref="MaxHeaderStringBytes"/> bytes including the zero.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the string is skipped</returns>
    /// <exception cref="InvalidDataException">The string is longer or the input ends inside it</exception>
    private async ValueTask SkipHeaderStringAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaxHeaderStringBytes; index++)
        {
            if (await ReadHeaderByteAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return;
            }
        }

        throw new InvalidDataException("gzip header string is too long");
    }

    /// <summary>
    /// Reads one header byte and adds it to the header checksum.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The byte</returns>
    /// <exception cref="InvalidDataException">The input ended</exception>
    private async ValueTask<int> ReadHeaderByteAsync(CancellationToken cancellationToken)
    {
        var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

        _single[0] = (byte)value;
        _headerCrc = Crc32.Update(_headerCrc, _single);

        return value;
    }

    /// <summary>
    /// Consumes one byte of the input.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The byte</returns>
    /// <exception cref="InvalidDataException">The input ended</exception>
    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var value = await TryReadByteAsync(cancellationToken).ConfigureAwait(false);

        if (value >= 0)
        {
            return value;
        }

        throw new InvalidDataException("gzip stream ends unexpectedly");
    }

    /// <summary>
    /// Makes sure at least one unconsumed byte is buffered.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns><c>true</c> when a byte is available, <c>false</c> at the end of the input</returns>
    private async ValueTask<bool> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        if (_start < _end)
        {
            return true;
        }

        _start = 0;
        _end = await _input.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

        return _end > 0;
    }

    #endregion // Methods

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => _input.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);

        try
        {
            var read = ReadAsync(rented.AsMemory(0, buffer.Length)).AsTask().GetAwaiter().GetResult();

            rented.AsSpan(0, read).CopyTo(buffer);

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_failure is not null)
        {
            throw _failure;
        }

        var total = 0;

        try
        {
            while (total < buffer.Length)
            {
                if (_done)
                {
                    break;
                }

                if (_inflater is null)
                {
                    _done = await StartMemberAsync(cancellationToken).ConfigureAwait(false);

                    continue;
                }

                var read = await _inflater.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);

                if (read > 0)
                {
                    _crc = Crc32.Update(_crc, buffer.Span.Slice(total, read));
                    _size += (uint)read;
                    total += read;
                }
                else
                {
                    await EndMemberAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (InvalidDataException exception) when (total > 0)
        {
            _failure = exception;
        }

        return total;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inflater?.Dispose();
            _inflater = null;
        }

        base.Dispose(disposing);
    }

    #endregion // Stream
}