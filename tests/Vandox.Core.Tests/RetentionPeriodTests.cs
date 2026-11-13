using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="RetentionPeriod"/>
/// </summary>
[TestClass]
public class RetentionPeriodTests
{
    #region Properties

    /// <summary>
    /// Gets the refused texts: other units, signs, white space, leading zeros, other letter cases and digits of other scripts.
    /// </summary>
    public static IEnumerable<string> RefusedForms
    {
        get
        {
            var fullWidthOne = char.ConvertFromUtf32(0xFF11);
            var fullWidthZero = char.ConvertFromUtf32(0xFF10);
            var arabicIndicThree = char.ConvertFromUtf32(0x0663);
            var arabicIndicZero = char.ConvertFromUtf32(0x0660);

            return
            [
                string.Empty,
                "d",
                "0d",
                "00d",
                "10000d",
                "30",
                "30D",
                "1y",
                "1w",
                "24h",
                "+30d",
                "-30d",
                "1.5d",
                "1 d",
                " 30d",
                "30d ",
                "\t30d",
                "30d\t",
                "030d",
                "0030d",
                "30dd",
                "d30",
                "1_0d",
                "0x1d",
                fullWidthOne + "d",
                fullWidthOne + fullWidthZero + "d",
                arabicIndicThree + arabicIndicZero + "d",
                arabicIndicThree + "d",
                "Unlimited",
                "UNLIMITED",
                "unlimited ",
                " unlimited",
                "unlimit",
                "infinite",
                "never",
                "0"
            ];
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// One to four ASCII digits without a leading zero and the unit d give that many days from 1d to 9999d, and <c>unlimited</c> gives no period.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="days">The expected number of days, or -1 for no period</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("1d", 1)]
    [DataRow("7d", 7)]
    [DataRow("30d", 30)]
    [DataRow("90d", 90)]
    [DataRow("365d", 365)]
    [DataRow("1095d", 1095)]
    [DataRow("9999d", 9999)]
    [DataRow("unlimited", -1)]
    public void RetentionPeriodTryParseAcceptsDaysAndUnlimited(string text, int days)
    {
        // Arrange
        TimeSpan? expected = days < 0 ? null : TimeSpan.FromDays(days);

        // Act
        var parsed = RetentionPeriod.TryParse(text, out var value);

        // Assert
        Assert.IsTrue(parsed, $"'{text}' is accepted");
        Assert.AreEqual(expected, value, $"period of '{text}'");
    }

    /// <summary>
    /// Every other form is refused and gives no period.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DynamicData(nameof(RefusedForms))]
    public void RetentionPeriodTryParseRefusesOtherForms(string text)
    {
        // Act
        var parsed = RetentionPeriod.TryParse(text, out var value);

        // Assert
        Assert.IsFalse(parsed, $"'{text}' is refused");
        Assert.IsNull(value, $"no period is returned for '{text}'");
    }

    #endregion // Methods
}
