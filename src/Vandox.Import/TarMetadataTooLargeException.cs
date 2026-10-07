namespace Vandox.Import;

/// <summary>
/// An extended header of a tar archive declares more metadata than the import accepts.
/// </summary>
internal sealed class TarMetadataTooLargeException : IOException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TarMetadataTooLargeException"/> class.
    /// </summary>
    internal TarMetadataTooLargeException()
        : base("tar metadata header is too large")
    {
    }

    #endregion // Constructors
}