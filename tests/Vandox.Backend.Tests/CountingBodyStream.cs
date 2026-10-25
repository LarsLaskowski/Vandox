namespace Vandox.Backend.Tests;

/// <summary>
/// A request body that counts how often and how far it was read. It serves fixed bytes, an endless run of zero bytes, or fixed bytes followed by a read that
/// never returns until it is cancelled (a body that trickles in) or by a read that throws a scripted exception.
/// </summary>
internal sealed class CountingBodyStream : Stream
{
    #region Fields

    private readonly byte[] _data;

    private readonly bool _endless;

    private readonly bool _blockAtEnd;

    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _position;

    private int _readCalls;

    private long _bytesRead;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBodyStream"/> class.
    /// </summary>
    /// <param name="data">The bytes the body serves</param>
    /// <param name="blockAtEnd">Whether a read after the last byte waits for its cancellation instead of returning the end of the body</param>
    internal CountingBodyStream(byte[] data, bool blockAtEnd = false)
        : this(data, false, blockAtEnd)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBodyStream"/> class.
    /// </summary>
    /// <param name="data">The bytes the body serves</param>
    /// <param name="endless">Whether the body never ends and serves zero bytes</param>
    /// <param name="blockAtEnd">Whether a read after the last byte waits for its cancellation</param>
    private CountingBodyStream(byte[] data, bool endless, bool blockAtEnd)
    {
        _data = data;
        _endless = endless;
        _blockAtEnd = blockAtEnd;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of reads, started or finished.
    /// </summary>
    internal int ReadCalls => Volatile.Read(ref _readCalls);

    /// <summary>
    /// Gets the number of bytes that were handed out.
    /// </summary>
    internal long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>
    /// Gets a task that completes when the first read starts.
    /// </summary>
    internal Task FirstRead => _firstRead.Task;

    /// <summary>
    /// Gets or sets the exception that a read after the last byte throws instead of returning the end of the body, <c>null</c> for none.
    /// </summary>
    internal Exception? FailAtEnd { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a body that never ends and serves zero bytes.
    /// </summary>
    /// <returns>The body</returns>
    internal static CountingBodyStream Endless()
    {
        return new CountingBodyStream([], true, false);
    }

    #endregion // Methods

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => true;

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
        // A body that is only read has nothing to flush.
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readCalls);
        _firstRead.TrySetResult();

        if (_endless)
        {
            buffer.Span.Clear();
            Interlocked.Add(ref _bytesRead, buffer.Length);

            return buffer.Length;
        }

        var left = _data.Length - _position;

        if (left <= 0)
        {
            if (FailAtEnd is not null)
            {
                throw FailAtEnd;
            }

            if (_blockAtEnd)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return 0;
        }

        var count = (int)Math.Min(left, buffer.Length);

        _data.AsMemory((int)_position, count).CopyTo(buffer);
        _position += count;
        Interlocked.Add(ref _bytesRead, count);

        return count;
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

    #endregion // Stream
}