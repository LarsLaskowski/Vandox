using System.Text;

namespace Vandox.Core.Signatures;

/// <summary>
/// The catalog of signatures embedded in the backend.
/// </summary>
public static class BuiltInSignatures
{
    #region Constants

    /// <summary>
    /// The name of the embedded resource.
    /// </summary>
    internal const string ResourceName = "Vandox.Core.Signatures.signatures.yaml";

    #endregion // Constants

    #region Fields

    private static readonly Lazy<SignatureCatalog> _catalog = new(Load);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the built-in catalog, loaded once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing</exception>
    public static SignatureCatalog Catalog => _catalog.Value;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the embedded resource and parses it.
    /// </summary>
    /// <returns>The catalog</returns>
    private static SignatureCatalog Load()
    {
        using var stream = typeof(BuiltInSignatures).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("signatures: the built-in catalog is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return SignatureCatalog.Parse("signatures.yaml", reader.ReadToEnd());
    }

    #endregion // Methods
}