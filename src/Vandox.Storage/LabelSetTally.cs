using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vandox.Storage;

/// <summary>
/// Keeps the smallest label sets of a scan and counts every distinct one: the kept texts are at most the limit, and every
/// distinct text is counted by the first 8 bytes of its SHA-256 without keeping the text, so a scan never holds all its texts.
/// </summary>
internal sealed class LabelSetTally
{
    #region Constants

    private const int HashBytes = 32;

    #endregion // Constants

    #region Fields

    private readonly int _limit;
    private readonly HashSet<ulong> _seen = [];
    private readonly List<(string Labels, DateTimeOffset CapturedAt, double Value)> _kept = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LabelSetTally"/> class.
    /// </summary>
    /// <param name="limit">The maximum number of label texts kept</param>
    internal LabelSetTally(int limit)
    {
        _limit = limit;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of distinct label texts seen.
    /// </summary>
    internal int Count => _seen.Count;

    /// <summary>
    /// Gets the kept label texts in ordinal order, each with its first row.
    /// </summary>
    internal IReadOnlyList<(string Labels, DateTimeOffset CapturedAt, double Value)> Kept => _kept;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a row, newest first.
    /// </summary>
    /// <param name="labels">The label text</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="value">The value</param>
    internal void Add(string labels, DateTimeOffset capturedAt, double value)
    {
        if (_seen.Add(HashOf(labels)) == false)
        {
            return;
        }

        if (_kept.Count >= _limit)
        {
            if (string.CompareOrdinal(labels, _kept[^1].Labels) > 0)
            {
                return;
            }

            _kept.RemoveAt(_kept.Count - 1);
        }

        var index = _kept.BinarySearch((labels, capturedAt, value), Comparer<(string Labels, DateTimeOffset CapturedAt, double Value)>.Create((left, right) => string.CompareOrdinal(left.Labels, right.Labels)));

        _kept.Insert(~index, (labels, capturedAt, value));
    }

    /// <summary>
    /// Hashes a label text to the first 8 bytes of its SHA-256 without copying the text.
    /// </summary>
    /// <param name="labels">The label text</param>
    /// <returns>The hash</returns>
    private static ulong HashOf(string labels)
    {
        Span<byte> hash = stackalloc byte[HashBytes];

        SHA256.HashData(MemoryMarshal.AsBytes(labels.AsSpan()), hash);

        return BitConverter.ToUInt64(hash);
    }

    #endregion // Methods
}