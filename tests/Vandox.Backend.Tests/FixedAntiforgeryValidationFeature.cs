using Microsoft.AspNetCore.Antiforgery;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IAntiforgeryValidationFeature"/> with a given result, as the antiforgery middleware records it.
/// </summary>
internal sealed class FixedAntiforgeryValidationFeature : IAntiforgeryValidationFeature
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedAntiforgeryValidationFeature"/> class.
    /// </summary>
    /// <param name="isValid">Whether the request carried a valid token</param>
    internal FixedAntiforgeryValidationFeature(bool isValid)
    {
        IsValid = isValid;
    }

    #endregion // Constructors

    #region IAntiforgeryValidationFeature

    /// <inheritdoc />
    public bool IsValid { get; }

    /// <inheritdoc />
    public Exception? Error => IsValid ? null : new InvalidOperationException("The token is not valid.");

    #endregion // IAntiforgeryValidationFeature
}