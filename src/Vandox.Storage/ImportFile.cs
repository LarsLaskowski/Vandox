namespace Vandox.Storage;

/// <summary>
/// The stored import state of a file content.
/// </summary>
public sealed class ImportFile
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 of the decompressed content.
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
    /// Gets or sets the file name of the first import, exact.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the modification time of the first import, in UTC; <c>null</c> when unknown.
    /// </summary>
    public DateTimeOffset? ModTime { get; set; }

    /// <summary>
    /// Gets or sets the parser type.
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of records stored for the file so far.
    /// </summary>
    public long Records { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the import is complete.
    /// </summary>
    public bool Complete { get; set; }

    /// <summary>
    /// Gets or sets the instant the import started.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant the import completed; <c>null</c> while it is not complete.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    #endregion // Properties
}