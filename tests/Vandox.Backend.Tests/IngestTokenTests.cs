using Microsoft.Extensions.Primitives;

using Vandox.Backend.Ingest;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestToken"/>
/// </summary>
[TestClass]
public class IngestTokenTests
{
    #region Constants

    private const string Token = "token-0123456789-abcdefghijklmnopqrstuvwxyz";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Every form of the Authorization header other than one bearer value with the token is refused.
    /// </summary>
    /// <param name="form">The name of the form</param>
    /// <param name="values">The values of the header</param>
    [TestMethod]
    [DynamicData(nameof(RefusedForms))]
    public void IngestTokenRefusesEveryOtherForm(string form, string[] values)
    {
        // Arrange
        var token = new IngestToken(new Secret(Token));

        // Act
        var matches = token.Matches(new StringValues(values));

        // Assert
        Assert.IsFalse(matches, $"the form \"{form}\" is refused");
    }

    /// <summary>
    /// The scheme is compared ignoring ASCII case and the token as it is.
    /// </summary>
    /// <param name="scheme">The scheme</param>
    [TestMethod]
    [DataRow("Bearer")]
    [DataRow("bearer")]
    [DataRow("BEARER")]
    [DataRow("bEaReR")]
    public void IngestTokenAcceptsSchemeInAnyCase(string scheme)
    {
        // Arrange
        var token = new IngestToken(new Secret(Token));

        // Act
        var matches = token.Matches(new StringValues($"{scheme} {Token}"));

        // Assert
        Assert.IsTrue(matches, "the token with one space after the scheme is accepted");
    }

    /// <summary>
    /// The comparison input is a 32-byte digest on both sides, for a presented token of any length.
    /// </summary>
    /// <param name="length">The length of the presented token</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    [DataRow(16000)]
    public void IngestTokenComparesFixedLengthDigests(int length)
    {
        // Arrange
        var presented = new string('x', length);

        // Act
        var presentedDigest = IngestToken.Digest(presented.AsSpan());
        var tokenDigest = IngestToken.Digest(Token.AsSpan());
        var again = IngestToken.Digest(presented.AsSpan());

        // Assert
        Assert.HasCount(32, presentedDigest, "digest of the presented token");
        Assert.HasCount(32, tokenDigest, "digest of the token");
        Assert.AreSequenceEqual(presentedDigest, again, "the same input gives the same digest");
        Assert.IsFalse(presentedDigest.AsSpan().SequenceEqual(tokenDigest), "other input gives another digest");
    }

    /// <summary>
    /// The forms the plan lists, each with the values of the Authorization header.
    /// </summary>
    /// <returns>The rows: the name and the header values</returns>
    private static IEnumerable<object[]> RefusedForms()
    {
        yield return ["no header", Array.Empty<string>()];
        yield return ["empty", new[] { string.Empty }];
        yield return ["basic", new[] { $"Basic {Token}" }];
        yield return ["wrong token", new[] { "Bearer wrong-0123456789-abcdefghijklmnopqrstuvwxyz" }];
        yield return ["prefix", new[] { $"Bearer {Token[..^1]}" }];
        yield return ["longer", new[] { $"Bearer {Token}x" }];
        yield return ["shorter", new[] { $"Bearer {Token[..8]}" }];
        yield return ["two spaces", new[] { $"Bearer  {Token}" }];
        yield return ["tab", new[] { $"Bearer\t{Token}" }];
        yield return ["two values", new[] { $"Bearer {Token}", $"Bearer {Token}" }];
        yield return ["non-ASCII", new[] { "Bearer tökén-0123456789-abcdefghijklmnopqrstuvwxyz" }];
    }

    #endregion // Methods
}