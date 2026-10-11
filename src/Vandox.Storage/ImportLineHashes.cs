namespace Vandox.Storage;

/// <summary>
/// One stored row of line hashes of an imported content.
/// </summary>
public sealed class ImportLineHashes
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the import file.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Gets or sets the attempt that computed the hashes.
    /// </summary>
    public long Attempt { get; set; }

    /// <summary>
    /// Gets or sets the offset after the line end before the row's first one; 0 for the first row.
    /// </summary>
    public long FirstOffset { get; set; }

    /// <summary>
    /// Gets or sets the offset of the row's last line end.
    /// </summary>
    public long LastOffset { get; set; }

    /// <summary>
    /// Gets or sets the hashes, <see cref="StorageLimits.LineHashBytes"/> bytes per line end, in ascending order of their line ends.
    /// </summary>
    public byte[] Hashes { get; set; } = [];

    #endregion // Properties
}