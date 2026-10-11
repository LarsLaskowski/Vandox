using System.Security.Cryptography;

using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// The content a parser reads in pass 2: at most the size that was hashed in pass 1, hashed and counted again so that a change
/// between the passes is detected, with a progress report every <see cref="ImportLimits.ProgressLines"/> lines.
/// </summary>
internal sealed class ContentStream : FilterStream
{
    #region Fields

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _limit;
    private readonly Action<long> _report;
    private readonly Queue<ImportLineHashes> _queue = new();
    private long _next = ImportLimits.ProgressLines;
    private long _newlines;
    private long _counted;
    private long _skippedNewlines;
    private bool _skipping;
    private byte _last;
    private byte[] _row = [];
    private int _rowCount;
    private int _queuedHashes;
    private long _rowFirst;
    private long _rowLast;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentStream"/> class.
    /// </summary>
    /// <param name="inner">The decompressed content</param>
    /// <param name="limit">The size hashed in pass 1; nothing beyond it is read</param>
    /// <param name="report">Called with the line count at every progress mark</param>
    internal ContentStream(Stream inner, long limit, Action<long> report)
        : base(inner)
    {
        _limit = limit;
        _report = report;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of bytes read.
    /// </summary>
    internal long BytesRead { get; private set; }

    /// <summary>
    /// Gets the number of lines read: the line feeds plus one for a last line without one.
    /// </summary>
    internal long Lines => _counted > 0 && _last != (byte)'\n' ? _newlines + 1 : _newlines;

    /// <summary>
    /// Gets or sets the writer that receives every complete row of line hashes (with <c>FileId</c> and <c>Attempt</c> 0); <c>null</c> keeps no line hashes.
    /// </summary>
    internal Func<ImportLineHashes, CancellationToken, Task>? LineHashWriter { get; set; }

    /// <summary>
    /// Gets the number of line hashes computed and not yet passed to the writer.
    /// </summary>
    internal int PendingLineHashes => _queuedHashes + _rowCount;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Passes the queued rows and the partial row of line hashes to the writer, in order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the rows are written</returns>
    internal async Task FlushLineHashesAsync(CancellationToken cancellationToken)
    {
        await WriteQueuedAsync(cancellationToken).ConfigureAwait(false);

        if (_rowCount == 0 || LineHashWriter is null)
        {
            return;
        }

        var row = TakeRow();

        await LineHashWriter(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the SHA-256 of what was read.
    /// </summary>
    /// <returns>The hash, 32 bytes</returns>
    internal byte[] GetHash()
    {
        return _hash.GetHashAndReset();
    }

    /// <summary>
    /// Reads and hashes bytes without counting lines or reporting progress.
    /// </summary>
    /// <param name="count">The number of bytes to skip</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the number of line feeds in the bytes skipped</returns>
    internal async Task<long> SkipAsync(long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[(int)Math.Min(count, 81920)];
        var remaining = count;

        _skipping = true;
        _skippedNewlines = 0;

        try
        {
            while (remaining > 0)
            {
                var read = await ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                remaining -= read;
            }
        }
        finally
        {
            _skipping = false;
        }

        return _skippedNewlines;
    }

    #endregion // Methods

    #region Private

    /// <summary>
    /// Passes the queued complete rows to the writer, in order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the rows are written</returns>
    private async Task WriteQueuedAsync(CancellationToken cancellationToken)
    {
        while (_queue.Count > 0 && LineHashWriter is { } writer)
        {
            var row = _queue.Peek();

            await writer(row, cancellationToken).ConfigureAwait(false);
            _queue.Dequeue();
            _queuedHashes -= row.Hashes.Length / StorageLimits.LineHashBytes;
        }
    }

    /// <summary>
    /// Turns the row being filled into a row to write and starts the next one at its last line end.
    /// </summary>
    /// <returns>The row, without <c>FileId</c> and <c>Attempt</c></returns>
    private ImportLineHashes TakeRow()
    {
        var row = new ImportLineHashes
                  {
                      FirstOffset = _rowFirst,
                      LastOffset = _rowLast,
                      Hashes = _row.AsSpan(0, _rowCount * StorageLimits.LineHashBytes).ToArray()
                  };

        _rowFirst = _rowLast;
        _rowCount = 0;

        return row;
    }

    /// <summary>
    /// Hashes the bytes and keeps the hash after every line feed.
    /// </summary>
    /// <param name="data">The bytes read</param>
    private void HashLines(ReadOnlySpan<byte> data)
    {
        var position = 0;
        Span<byte> current = stackalloc byte[32];

        while (position < data.Length)
        {
            var feed = data[position..].IndexOf((byte)'\n');

            if (feed < 0)
            {
                _hash.AppendData(data[position..]);

                return;
            }

            var end = position + feed + 1;

            _hash.AppendData(data[position..end]);
            _hash.GetCurrentHash(current);

            if (_row.Length == 0)
            {
                _row = new byte[StorageLimits.MaxLineHashes * StorageLimits.LineHashBytes];
            }

            current[..StorageLimits.LineHashBytes].CopyTo(_row.AsSpan(_rowCount * StorageLimits.LineHashBytes));
            _rowCount++;
            _rowLast = BytesRead + end;

            if (_rowCount == StorageLimits.MaxLineHashes)
            {
                _queue.Enqueue(TakeRow());
                _queuedHashes += StorageLimits.MaxLineHashes;
            }

            position = end;
        }
    }

    #endregion // Private

    #region FilterStream

    /// <inheritdoc />
    protected override void Observe(ReadOnlySpan<byte> data)
    {
        if (LineHashWriter is null)
        {
            _hash.AppendData(data);
        }
        else
        {
            HashLines(data);
        }

        BytesRead += data.Length;

        if (_skipping)
        {
            _skippedNewlines += data.Count((byte)'\n');

            return;
        }

        _counted += data.Length;
        _newlines += data.Count((byte)'\n');
        _last = data[^1];

        while (_newlines >= _next)
        {
            _report(_next);
            _next += ImportLimits.ProgressLines;
        }
    }

    #endregion // FilterStream

    #region Stream

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (LineHashWriter is not null)
        {
            throw new NotSupportedException("the content is read asynchronously while line hashes are kept");
        }

        var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _limit - BytesRead));

        return allowed == 0 ? 0 : base.Read(buffer[..allowed]);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await WriteQueuedAsync(cancellationToken).ConfigureAwait(false);

        var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _limit - BytesRead));

        return allowed == 0 ? 0 : await base.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion // Stream
}