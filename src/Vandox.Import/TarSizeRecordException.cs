namespace Vandox.Import;

/// <summary>
/// A PAX header of a tar archive carries a size record, which the import does not support.
/// </summary>
internal sealed class TarSizeRecordException : IOException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TarSizeRecordException"/> class.
    /// </summary>
    internal TarSizeRecordException()
        : base("tar size record is not supported")
    {
    }

    #endregion // Constructors
}