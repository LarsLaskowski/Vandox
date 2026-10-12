using Vandox.Core.Templates;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="TokenRules"/>
/// </summary>
[TestClass]
public class TokenRulesTests
{
    #region Methods

    /// <summary>
    /// A group list that ends in a colon is no IPv6 address. Normalization never hands such a token to the rule, because it cuts trailing colons first.
    /// </summary>
    [TestMethod]
    public void TokenRulesIsIpv6RefusesGroupsEndingInAColon()
    {
        // Act
        var withColon = TokenRules.IsIpv6("1:2:3:4:5:6:7:");
        var withoutColon = TokenRules.IsIpv6("1:2:3:4:5:6:7:8");

        // Assert
        Assert.IsFalse(withColon, "seven groups followed by a colon");
        Assert.IsTrue(withoutColon, "eight groups");
    }

    #endregion // Methods
}