using Microsoft.AspNetCore.Antiforgery;

namespace Vandox.Backend.Security;

/// <summary>
/// Marks an endpoint whose form post must carry a valid antiforgery token.
/// </summary>
internal sealed class RequiredAntiforgery : IAntiforgeryMetadata
{
    #region Properties

    /// <summary>
    /// Gets the only instance.
    /// </summary>
    public static RequiredAntiforgery Instance { get; } = new();

    #endregion // Properties

    #region IAntiforgeryMetadata

    /// <inheritdoc/>
    public bool RequiresValidation => true;

    #endregion // IAntiforgeryMetadata
}