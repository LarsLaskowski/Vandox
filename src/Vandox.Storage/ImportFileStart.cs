namespace Vandox.Storage;

/// <summary>
/// Describes a file content the importer is about to import.
/// </summary>
public sealed class ImportFileStart
{
    #region Properties

    /// <summary>
    /// Gets or sets the SHA-256 of the decompressed content; 32 bytes.
    /// </summary>
    public byte[] Sha256 { get; set; } = new byte[32];

    /// <summary>
    /// Gets or sets the number of bytes of the decompressed content.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the display path of the first import.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name the parser gets; stored byte for byte, at most <see cref="StorageLimits.MaxImportNameBytes"/>.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the modification time the parser gets, in UTC; <c>null</c> when unknown.
    /// </summary>
    public DateTimeOffset? ModTime { get; set; }

    /// <summary>
    /// Gets or sets the parser type.
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant the import started, in UTC.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    #endregion // Properties
}