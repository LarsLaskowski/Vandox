namespace Vandox.Core.Wire;

/// <summary>
/// Bounds what the decoder accepts.
/// </summary>
public sealed class WireLimits
{
    #region Properties

    /// <summary>
    /// Gets or sets the longest line in bytes, without the line feed; 0 selects the default.
    /// </summary>
    public int MaxLineBytes { get; set; } = 1 << 20;

    /// <summary>
    /// Gets or sets the most decompressed bytes of a batch; 0 selects the default.
    /// </summary>
    public long MaxBatchBytes { get; set; } = 16L << 20;

    /// <summary>
    /// Gets or sets the most records of a batch; 0 selects the default.
    /// </summary>
    public int MaxRecords { get; set; } = 20000;

    #endregion // Properties
}