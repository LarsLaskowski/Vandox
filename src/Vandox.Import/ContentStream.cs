using System.Security.Cryptography;

using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// The content a parser reads in pass 2: at most the size that was hashed in pass 1, hashed and counted again so that a change
/// between the passes is detected, with a progress report every <see cref="ImportLimits.ProgressLines"/> lines.
/// </summary>
#pragma warning disable RH2003, S2325

internal sealed class ContentStream : FilterStream
{
    #region Fields

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _limit;
    private readonly Action<long> _report;
    private long _next = ImportLimits.ProgressLines;
    private long _newlines;
    private long _counted;
    private long _skippedNewlines;
    private bool _skipping;
    private byte _last;

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
    internal int PendingLineHashes => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Passes the queued rows and the partial row of line hashes to the writer, in order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the rows are written</returns>
    internal Task FlushLineHashesAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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

    #region FilterStream

    /// <inheritdoc />
    protected override void Observe(ReadOnlySpan<byte> data)
    {
        _hash.AppendData(data);
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
        var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _limit - BytesRead));

        return allowed == 0 ? 0 : base.Read(buffer[..allowed]);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _limit - BytesRead));

        return allowed == 0 ? ValueTask.FromResult(0) : base.ReadAsync(buffer[..allowed], cancellationToken);
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