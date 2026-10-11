using System.Security.Cryptography;

using Vandox.Storage;

#pragma warning disable RH2003, S2325

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
    /// <param name="bases">The complete contents the stream may start with; at most <see cref="StorageLimits.MaxImportBases"/></param>
    internal HashingStream(Stream inner, long step, Action<long> report, IReadOnlyList<ImportBase> bases)
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

    /// <summary>
    /// Gets the bases whose SHA-256 equals the hash of the first bytes read, with the position after the last line feed in them.
    /// </summary>
    internal List<ImportPrefix> Matches { get; } = [];

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