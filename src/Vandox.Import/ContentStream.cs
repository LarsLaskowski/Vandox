using System.Security.Cryptography;

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
    private long _next = ImportLimits.ProgressLines;
    private long _newlines;
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
    internal long Lines => BytesRead > 0 && _last != (byte)'\n' ? _newlines + 1 : _newlines;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the SHA-256 of what was read.
    /// </summary>
    /// <returns>The hash, 32 bytes</returns>
    internal byte[] GetHash()
    {
        return _hash.GetHashAndReset();
    }

    #endregion // Methods

    #region FilterStream

    /// <inheritdoc />
    protected override void Observe(ReadOnlySpan<byte> data)
    {
        _hash.AppendData(data);
        BytesRead += data.Length;
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