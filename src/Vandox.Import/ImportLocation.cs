namespace Vandox.Import;

/// <summary>
/// Tells where the content of a found file is read again.
/// </summary>
internal sealed class ImportLocation
{
    #region Properties

    /// <summary>
    /// Gets or sets the file, or the archive that contains the entry; a path below the root.
    /// </summary>
    internal string FsPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ordinal of the entry among the archive's headers; -1 for a plain file.
    /// </summary>
    internal int Entry { get; set; } = -1;

    /// <summary>
    /// Gets or sets a value indicating whether the archive is gzip-compressed.
    /// </summary>
    internal bool ArchiveGzip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file or entry content is gzip-compressed.
    /// </summary>
    internal bool Gzip { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns a copy.
    /// </summary>
    /// <returns>The copy</returns>
    internal ImportLocation Copy()
    {
        return (ImportLocation)MemberwiseClone();
    }

    #endregion // Methods
}