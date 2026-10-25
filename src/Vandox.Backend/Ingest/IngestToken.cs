using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Primitives;

using Vandox.Core.Configuration;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Checks the bearer token of an ingest request against the configured token.
/// </summary>
internal sealed class IngestToken
{
    #region Constants

    /// <summary>
    /// The scheme of the header value, followed by the one space that separates it from the token.
    /// </summary>
    private const string Scheme = "Bearer ";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The digest of the configured token.
    /// </summary>
    private readonly byte[] _digest;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestToken"/> class.
    /// </summary>
    /// <param name="token">The configured token</param>
    internal IngestToken(Secret token)
    {
        _digest = Digest(token.Reveal());
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
        return SHA256.HashData(Encoding.UTF8.GetBytes(token.ToString()));
    }

    /// <summary>
    /// Checks the <c>Authorization</c> header values: exactly one value, scheme <c>Bearer</c> in any ASCII case, one space, then the token.
    /// </summary>
    /// <param name="authorization">The header values</param>
    /// <returns><c>true</c> when the token matches</returns>
    internal bool Matches(StringValues authorization)
    {
        if (authorization.Count == 1 && authorization[0] is { } value && value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return CryptographicOperations.FixedTimeEquals(Digest(value.AsSpan(Scheme.Length)), _digest);
        }

        return false;
    }

    #endregion // Methods
}