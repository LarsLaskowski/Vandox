using System.Security.Cryptography;

namespace Vandox.Import;

/// <summary>
/// Hashes and counts what is read from a stream and reports every multiple of a step in bytes it passes.
/// </summary>
internal sealed class HashingStream : FilterStream
{
    #region Fields

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _step;
    private readonly Action<long> _report;
    private long _next;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HashingStream"/> class.
    /// </summary>
    /// <param name="inner">The stream to read</param>
    /// <param name="step">The number of bytes between two reports</param>
    /// <param name="report">Called with the multiple of the step that was passed</param>
    internal HashingStream(Stream inner, long step, Action<long> report)
        : base(inner)
    {
        _step = step;
        _next = step;
        _report = report;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of bytes read.
    /// </summary>
    internal long BytesRead { get; private set; }

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

        while (BytesRead >= _next)
        {
            _report(_next);
            _next += _step;
        }
    }

    #endregion // FilterStream

    #region Stream

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