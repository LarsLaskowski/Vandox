using Microsoft.AspNetCore.Components.Forms;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="AntiforgeryStateProvider"/> that hands out one fixed token, for components with an <c>AntiforgeryToken</c> under a plain renderer.
/// </summary>
internal sealed class FixedAntiforgeryStateProvider : AntiforgeryStateProvider
{
    #region Constants

    /// <summary>
    /// The value of the token.
    /// </summary>
    internal const string TokenValue = "fixed-antiforgery-token";

    /// <summary>
    /// The name of the form field.
    /// </summary>
    internal const string FormFieldName = "__RequestVerificationToken";

    #endregion // Constants

    #region AntiforgeryStateProvider

    /// <inheritdoc />
    public override AntiforgeryRequestToken? GetAntiforgeryToken()
    {
        return new AntiforgeryRequestToken(TokenValue, FormFieldName);
    }

    #endregion // AntiforgeryStateProvider
}