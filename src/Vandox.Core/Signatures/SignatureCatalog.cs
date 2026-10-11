namespace Vandox.Core.Signatures;

/// <summary>
/// A checked catalog of signatures.
/// </summary>
public sealed class SignatureCatalog
{
    #region Constants

    /// <summary>
    /// The most characters of a pattern.
    /// </summary>
    internal const int MaxPatternChars = 4096;

    /// <summary>
    /// The most entries of a program, source or event list.
    /// </summary>
    internal const int MaxListEntries = 16;

    /// <summary>
    /// The most fields of a signature.
    /// </summary>
    internal const int MaxFields = 8;

    /// <summary>
    /// The most UTF-8 bytes of a description.
    /// </summary>
    internal const int MaxDescriptionBytes = 1024;

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Creates a catalog.
    /// </summary>
    /// <param name="signatures">The signatures in catalog order</param>
    private SignatureCatalog(IReadOnlyList<Signature> signatures)
    {
        Signatures = signatures;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the signatures in catalog order.
    /// </summary>
    public IReadOnlyList<Signature> Signatures { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses and checks a catalog text.
    /// </summary>
    /// <param name="name">The name of the text in error messages</param>
    /// <param name="text">The YAML text</param>
    /// <returns>The catalog</returns>
    /// <exception cref="SignatureException">The text breaks a catalog rule</exception>
    public static SignatureCatalog Parse(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);

        return new SignatureCatalog(new SignatureCatalogReader(name).Read(text));
    }

    #endregion // Methods
}