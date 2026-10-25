using Microsoft.Extensions.Primitives;

using Vandox.Core.Configuration;

namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Checks the bearer token of an ingest request against the configured token.
/// </summary>
internal sealed class IngestToken
{
    #region Fields

    /// <summary>
    /// The configured token.
    /// </summary>
    private readonly Secret _token;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestToken"/> class.
    /// </summary>
    /// <param name="token">The configured token</param>
    internal IngestToken(Secret token)
    {
        _token = token;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Computes the fixed-length digest a token is compared by.
    /// </summary>
    /// <param name="token">The token text</param>
    /// <returns>The digest</returns>
    internal static byte[] Digest(ReadOnlySpan<char> token)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks the <c>Authorization</c> header values: exactly one value, scheme <c>Bearer</c> in any ASCII case, one space, then the token.
    /// </summary>
    /// <param name="authorization">The header values</param>
    /// <returns><c>true</c> when the token matches</returns>
    internal bool Matches(StringValues authorization)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}