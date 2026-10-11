#pragma warning disable RH2003, S2325

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

    #region Properties

    /// <summary>
    /// Gets the built-in catalog, loaded once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing</exception>
    public static SignatureCatalog Catalog => throw new NotImplementedException();

    #endregion // Properties
}