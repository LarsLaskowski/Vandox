namespace Vandox.Core.Signatures;

/// <summary>
/// The type of a signature field.
/// </summary>
public enum SignatureFieldType
{
    #region Values

    /// <summary>
    /// The captured text, cut to 1,024 UTF-8 bytes.
    /// </summary>
    Text,

    /// <summary>
    /// A decimal integer.
    /// </summary>
#pragma warning disable CA1720 // The name is the catalog's field type keyword (integer)
    Integer,
#pragma warning restore CA1720

    /// <summary>
    /// A decimal number of kibibytes, converted to bytes.
    /// </summary>
    Kibibytes

    #endregion // Values
}