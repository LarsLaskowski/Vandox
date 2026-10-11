namespace Vandox.Storage;

/// <summary>
/// A base a content starts with, with the point where the new part of the content starts.
/// </summary>
public sealed class ImportPrefix
{
    #region Properties

    /// <summary>
    /// Gets or sets the SHA-256 of the base the content starts with; 32 bytes.
    /// </summary>
    public byte[] Sha256 { get; set; } = new byte[32];

    /// <summary>
    /// Gets or sets the size of the base in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the offset where the new part starts: after the last line feed of the base.
    /// </summary>
    public long Offset { get; set; }

    #endregion // Properties
}