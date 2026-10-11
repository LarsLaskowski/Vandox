namespace Vandox.Storage;

/// <summary>
/// A completely imported content that may be the start of a longer one.
/// </summary>
public sealed class ImportBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the SHA-256 of the complete content; 32 bytes.
    /// </summary>
    public byte[] Sha256 { get; set; } = new byte[32];

    /// <summary>
    /// Gets or sets the size of the content in bytes.
    /// </summary>
    public long Size { get; set; }

    #endregion // Properties
}